using System.Diagnostics;
using System.Runtime.InteropServices;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Win32.Interop;
using static Cotabby.Win32.Interop.NativeMethods;
namespace Cotabby.Win32.Insertion;

internal interface IEmojiReplacementFocusValidator : IDisposable
{
    bool Matches(FocusedField expected, string expectedTail, int expectedCaretOffset);
}

/// <summary>
/// Uses one IUIAutomation2 client for both checks in a replacement transaction.
/// The transaction timeout is set on that exact client before it is used; all
/// focused-element, property, pattern, and text-range reads come from that client.
/// </summary>
internal sealed class BoundedUiaReplacementFocusValidator : IEmojiReplacementFocusValidator
{
    internal const uint TransactionTimeoutMilliseconds = 100;
    // Before each UIA transaction we enforce a 120ms total budget; an already
    // started transaction can exceed it only within the 100ms client timeout.
    private const int ValidationBudgetMilliseconds = 120;
    private const int MaximumSnapshotCharacters = 16 * 1024;
    private const int ProcessIdProperty = 30002;
    private const int ControlTypeProperty = 30003;
    private const int NameProperty = 30005;
    private const int HasKeyboardFocusProperty = 30008;
    private const int AutomationIdProperty = 30011;
    private const int HelpTextProperty = 30013;
    private const int LabeledByProperty = 30018;
    private const int IsPasswordProperty = 30019;
    private const int IsTextPatternAvailableProperty = 30040;
    private const int IsValuePatternAvailableProperty = 30043;
    private const int ValueValueProperty = 30045;
    private const int TextPatternId = 10014;
    private const int EditControlType = 50004;
    private const int ComboBoxControlType = 50003;
    private const int DocumentControlType = 50030;
    private const int PaneControlType = 50033;
    private const int CustomControlType = 50025;

    private readonly IUIAutomation2 _automation;
    private readonly Action? _afterContentReadForTesting;
    private readonly Action? _afterMetadataReadForTesting;
    private Stopwatch? _budget;

    internal BoundedUiaReplacementFocusValidator(
        Action? afterContentReadForTesting = null,
        Action? afterMetadataReadForTesting = null)
    {
        _afterContentReadForTesting = afterContentReadForTesting;
        _afterMetadataReadForTesting = afterMetadataReadForTesting;
        var coclass = Activator.CreateInstance(typeof(CUIAutomationClass))
            ?? throw new InvalidOperationException("Could not create the UI Automation client.");
        try
        {
            _automation = (IUIAutomation2)coclass;
            CheckHResult(_automation.put_TransactionTimeout(TransactionTimeoutMilliseconds));
            CheckHResult(_automation.put_ConnectionTimeout(TransactionTimeoutMilliseconds));
            CheckHResult(_automation.get_TransactionTimeout(out uint transactionTimeout));
            CheckHResult(_automation.get_ConnectionTimeout(out uint connectionTimeout));
            if (transactionTimeout != TransactionTimeoutMilliseconds
                || connectionTimeout != TransactionTimeoutMilliseconds)
                throw new InvalidOperationException("UI Automation client did not retain the configured timeouts.");
        }
        catch
        {
            ReleaseComObject(coclass);
            throw;
        }
    }

    public bool Matches(FocusedField expected, string expectedTail, int expectedCaretOffset)
    {
        if (expected.IsSecure || expected.IsCredentialField || !expected.HasRuntimeIdIdentity
            || string.IsNullOrWhiteSpace(expected.StableFieldIdentity)) return false;

        _budget = Stopwatch.StartNew();
        IUIAutomationElement? current = null;
        IUIAutomationElement? labeledBy = null;
        try
        {
            BeforeUiaCall();
            CheckHResult(_automation.GetFocusedElement(out current));
            if (current is null) return false;

            var processId = ReadProperty<int>(current, ProcessIdProperty);
            var hasKeyboardFocus = ReadProperty<bool>(current, HasKeyboardFocusProperty);
            var isPassword = ReadProperty<bool>(current, IsPasswordProperty);
            int controlTypeId = ReadProperty<int>(current, ControlTypeProperty);
            string controlType = ControlTypeName(controlTypeId);
            string? name = ReadOptionalString(current, NameProperty);
            string? helpText = ReadOptionalString(current, HelpTextProperty);
            string? automationId = ReadOptionalString(current, AutomationIdProperty);
            object? labeledObject = ReadPropertyObject(current, LabeledByProperty);
            if (labeledObject is IUIAutomationElement labelElement) labeledBy = labelElement;
            string? labeledByName = labeledBy is null ? null : ReadOptionalString(labeledBy, NameProperty);

            IntPtr foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
            if (foreground == IntPtr.Zero || processId != expected.ProcessId
                || foregroundProcessId != expected.ProcessId || !hasKeyboardFocus || isPassword)
                return false;

            BeforeUiaCall();
            CheckHResult(current.GetRuntimeId(out int[] runtimeId));
            if (runtimeId is not { Length: > 0 }
                || !string.Equals(string.Join('.', runtimeId), expected.StableFieldIdentity, StringComparison.Ordinal))
                return false;

            var labels = new[] { name, helpText, labeledByName };
            if (SearchFieldDetector.IsSearchField(controlType, labels, automationId)
                || CredentialFieldDetector.IsCredentialField(controlType, labels, automationId, text: null, isPassword))
                return false;

            var content = ReadTextBeforeCaret(current);
            if (content is null) return false;
            _afterContentReadForTesting?.Invoke();
            string textBeforeCaret = content.Value.Text;

            var live = expected with
            {
                StableFieldIdentity = string.Join('.', runtimeId),
                HasRuntimeIdIdentity = true,
                ProcessId = processId,
                Text = textBeforeCaret,
                CaretOffset = textBeforeCaret.Length,
                CaretOffsetIsProven = content.Value.CaretOffsetIsProven,
                IsSecure = false,
                IsCredentialField = false,
            };

            // Content reads can take long enough for programmatic focus changes
            // despite BlockInput. Re-resolve the focused element and prove its
            // RuntimeId/foreground immediately before the caller dispatches input.
            if (!FocusStillMatchesExpectedAfterContentRead(expected, textBeforeCaret)) return false;

            return EmojiFocusPolicy.MatchesExpectedReplacement(
                expected, live, expectedTail, expectedCaretOffset);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(labeledBy);
            ReleaseComObject(current);
            _budget = null;
        }
    }

    private (string Text, bool CaretOffsetIsProven)? ReadTextBeforeCaret(IUIAutomationElement element)
    {
        if (ReadProperty<bool>(element, IsTextPatternAvailableProperty))
        {
            object? rawPattern = null;
            IUIAutomationTextRange? document = null;
            IUIAutomationTextRangeArray? selections = null;
            IUIAutomationTextRange? selection = null;
            IUIAutomationTextRange? prefix = null;
            try
            {
                BeforeUiaCall();
                CheckHResult(element.GetCurrentPattern(TextPatternId, out rawPattern));
                if (rawPattern is not IUIAutomationTextPattern pattern) return null;
                BeforeUiaCall();
                CheckHResult(pattern.get_DocumentRange(out document));
                BeforeUiaCall();
                CheckHResult(pattern.GetSelection(out selections));
                if (selections is null) return null;
                BeforeUiaCall();
                CheckHResult(selections.get_Length(out int selectionCount));
                if (selectionCount <= 0) return null;
                BeforeUiaCall();
                CheckHResult(selections.GetElement(0, out selection));
                if (selection is null) return null;
                BeforeUiaCall();
                CheckHResult(selection.CompareEndpoints(0, selection, 1, out int collapsed));
                if (collapsed != 0) return null;
                BeforeUiaCall();
                CheckHResult(document.Clone(out prefix));
                BeforeUiaCall();
                CheckHResult(prefix.MoveEndpointByRange(1, selection, 0));
                BeforeUiaCall();
                CheckHResult(prefix.GetText(MaximumSnapshotCharacters + 1, out string text));
                return text.Length <= MaximumSnapshotCharacters ? (text, true) : null;
            }
            finally
            {
                ReleaseComObject(selection);
                ReleaseComObject(selections);
                ReleaseComObject(prefix);
                ReleaseComObject(document);
                ReleaseComObject(rawPattern);
            }
        }

        if (!ReadProperty<bool>(element, IsValuePatternAvailableProperty)) return null;
        string? value = ReadOptionalString(element, ValueValueProperty);
        if (value is null || value.Length > MaximumSnapshotCharacters) return null;
        // ValuePattern exposes no caret, so this payload cannot authorize replacement.
        return (value, false);
    }

    private bool FocusStillMatchesExpectedAfterContentRead(FocusedField expected, string textBeforeCaret)
    {
        IUIAutomationElement? focused = null;
        IUIAutomationElement? labeledBy = null;
        IUIAutomationElement? confirmedFocused = null;
        IUIAutomationElement? confirmedLabeledBy = null;
        try
        {
            BeforeUiaCall();
            CheckHResult(_automation.GetFocusedElement(out focused));
            if (focused is null) return false;

            int processId = ReadProperty<int>(focused, ProcessIdProperty);
            bool hasKeyboardFocus = ReadProperty<bool>(focused, HasKeyboardFocusProperty);
            bool isPassword = ReadProperty<bool>(focused, IsPasswordProperty);
            int controlTypeId = ReadProperty<int>(focused, ControlTypeProperty);
            string controlType = ControlTypeName(controlTypeId);
            string? name = ReadOptionalString(focused, NameProperty);
            string? helpText = ReadOptionalString(focused, HelpTextProperty);
            string? automationId = ReadOptionalString(focused, AutomationIdProperty);
            object? labeledObject = ReadPropertyObject(focused, LabeledByProperty);
            if (labeledObject is IUIAutomationElement labelElement) labeledBy = labelElement;
            string? labeledByName = labeledBy is null ? null : ReadOptionalString(labeledBy, NameProperty);

            if (processId != expected.ProcessId || !hasKeyboardFocus || isPassword) return false;
            var labels = new[] { name, helpText, labeledByName };
            if (SearchFieldDetector.IsSearchField(controlType, labels, automationId)
                || CredentialFieldDetector.IsCredentialField(
                    controlType, labels, automationId, textBeforeCaret, isPassword))
                return false;

            // Test seam for a same-element metadata change between checks.
            // Production leaves this unset.
            _afterMetadataReadForTesting?.Invoke();

            BeforeUiaCall();
            CheckHResult(focused.GetRuntimeId(out int[] runtimeId));
            if (runtimeId is not { Length: > 0 }) return false;

            string identity = string.Join('.', runtimeId);
            if (!string.Equals(identity, expected.StableFieldIdentity, StringComparison.Ordinal)) return false;

            // The metadata reads above can themselves take time. Confirm that
            // the same strong-identity element still owns focus after them.
            BeforeUiaCall();
            CheckHResult(_automation.GetFocusedElement(out confirmedFocused));
            if (confirmedFocused is null) return false;
            int confirmedProcessId = ReadProperty<int>(confirmedFocused, ProcessIdProperty);
            bool confirmedHasKeyboardFocus = ReadProperty<bool>(confirmedFocused, HasKeyboardFocusProperty);
            bool confirmedIsPassword = ReadProperty<bool>(confirmedFocused, IsPasswordProperty);
            if (confirmedProcessId != expected.ProcessId || !confirmedHasKeyboardFocus || confirmedIsPassword)
                return false;

            // The field can change semantic role without changing RuntimeId.
            // Reclassify the freshly confirmed element before authorizing input.
            int confirmedControlTypeId = ReadProperty<int>(confirmedFocused, ControlTypeProperty);
            string confirmedControlType = ControlTypeName(confirmedControlTypeId);
            string? confirmedName = ReadOptionalString(confirmedFocused, NameProperty);
            string? confirmedHelpText = ReadOptionalString(confirmedFocused, HelpTextProperty);
            string? confirmedAutomationId = ReadOptionalString(confirmedFocused, AutomationIdProperty);
            object? confirmedLabeledObject = ReadPropertyObject(confirmedFocused, LabeledByProperty);
            if (confirmedLabeledObject is IUIAutomationElement confirmedLabelElement)
                confirmedLabeledBy = confirmedLabelElement;
            else
                ReleaseComObject(confirmedLabeledObject);
            string? confirmedLabeledByName = confirmedLabeledBy is null
                ? null
                : ReadOptionalString(confirmedLabeledBy, NameProperty);
            var confirmedLabels = new[] { confirmedName, confirmedHelpText, confirmedLabeledByName };
            if (SearchFieldDetector.IsSearchField(confirmedControlType, confirmedLabels, confirmedAutomationId)
                || CredentialFieldDetector.IsCredentialField(
                    confirmedControlType, confirmedLabels, confirmedAutomationId, textBeforeCaret, confirmedIsPassword))
                return false;

            BeforeUiaCall();
            CheckHResult(confirmedFocused.GetRuntimeId(out int[] confirmedRuntimeId));
            if (confirmedRuntimeId is not { Length: > 0 }
                || !string.Equals(string.Join('.', confirmedRuntimeId), expected.StableFieldIdentity, StringComparison.Ordinal))
                return false;

            IntPtr foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
            return foreground != IntPtr.Zero && foregroundProcessId == (uint)expected.ProcessId;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(confirmedLabeledBy);
            ReleaseComObject(labeledBy);
            ReleaseComObject(confirmedFocused);
            ReleaseComObject(focused);
        }
    }

    private T ReadProperty<T>(IUIAutomationElement element, int propertyId)
    {
        object value = ReadPropertyObject(element, propertyId)
            ?? throw new InvalidOperationException("UI Automation property was unavailable.");
        if (value is T typed) return typed;
        throw new InvalidOperationException("UI Automation property had an unexpected type.");
    }

    private string? ReadOptionalString(IUIAutomationElement element, int propertyId) =>
        ReadPropertyObject(element, propertyId) as string;

    private object? ReadPropertyObject(IUIAutomationElement element, int propertyId)
    {
        BeforeUiaCall();
        CheckHResult(element.GetCurrentPropertyValue(propertyId, out object value));
        return value;
    }

    private void BeforeUiaCall()
    {
        if (_budget is not null && _budget.Elapsed >= TimeSpan.FromMilliseconds(ValidationBudgetMilliseconds))
            throw new TimeoutException("Bounded UI Automation validation budget elapsed.");
    }

    private static string ControlTypeName(int id) => id switch
    {
        EditControlType => "Edit",
        ComboBoxControlType => "ComboBox",
        DocumentControlType => "Document",
        PaneControlType => "Pane",
        CustomControlType => "Custom",
        _ => string.Empty,
    };

    private static void CheckHResult(int hresult) => Marshal.ThrowExceptionForHR(hresult);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public void Dispose() => ReleaseComObject(_automation);
}
