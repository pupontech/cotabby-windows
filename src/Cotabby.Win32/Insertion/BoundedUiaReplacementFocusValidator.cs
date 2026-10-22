using System.Diagnostics;
using System.Runtime.InteropServices;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
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
    private Stopwatch? _budget;

    public BoundedUiaReplacementFocusValidator()
    {
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

            string? textBeforeCaret = ReadTextBeforeCaret(current);
            if (textBeforeCaret is null
                || CredentialFieldDetector.IsCredentialField(
                    controlType, labels, automationId, textBeforeCaret, isPassword)) return false;

            var live = expected with
            {
                StableFieldIdentity = string.Join('.', runtimeId),
                HasRuntimeIdIdentity = true,
                ProcessId = processId,
                Text = textBeforeCaret,
                CaretOffset = textBeforeCaret.Length,
                IsSecure = false,
                IsCredentialField = false,
            };
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

    private string? ReadTextBeforeCaret(IUIAutomationElement element)
    {
        if (ReadProperty<bool>(element, IsTextPatternAvailableProperty))
        {
            object? rawPattern = null;
            IUIAutomationTextRange? document = null;
            IUIAutomationTextRange[]? selections = null;
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
                if (selections is not { Length: > 0 } || selections[0] is null) return null;
                var selection = selections[0];
                BeforeUiaCall();
                CheckHResult(selection.CompareEndpoints(0, selection, 1, out int collapsed));
                if (collapsed != 0) return null;
                BeforeUiaCall();
                CheckHResult(document.Clone(out prefix));
                BeforeUiaCall();
                CheckHResult(prefix.MoveEndpointByRange(1, selection, 0));
                BeforeUiaCall();
                CheckHResult(prefix.GetText(MaximumSnapshotCharacters + 1, out string text));
                return text.Length <= MaximumSnapshotCharacters ? text : null;
            }
            finally
            {
                if (selections is not null)
                    foreach (var selection in selections) ReleaseComObject(selection);
                ReleaseComObject(prefix);
                ReleaseComObject(document);
                ReleaseComObject(rawPattern);
            }
        }

        if (!ReadProperty<bool>(element, IsValuePatternAvailableProperty)) return null;
        string? value = ReadOptionalString(element, ValueValueProperty);
        if (value is null || value.Length > MaximumSnapshotCharacters) return null;
        // ValuePattern exposes no caret. UiaFocusTracker uses the end-of-value
        // fallback for this same case, so only that exact caret can be proven.
        return value;
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

[ComImport, Guid("e22ad333-b25f-460c-83d0-0581107395c9"), ClassInterface(ClassInterfaceType.None)]
internal sealed class CUIAutomationClass { }

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    [PreserveSig] int CompareElements();
    [PreserveSig] int CompareRuntimeIds();
    [PreserveSig] int GetRootElement();
    [PreserveSig] int ElementFromHandle();
    [PreserveSig] int ElementFromPoint();
    [PreserveSig] int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
    [PreserveSig] int GetRootElementBuildCache();
    [PreserveSig] int ElementFromHandleBuildCache();
    [PreserveSig] int ElementFromPointBuildCache();
    [PreserveSig] int GetFocusedElementBuildCache();
    [PreserveSig] int CreateTreeWalker();
    [PreserveSig] int get_ControlViewWalker();
    [PreserveSig] int get_ContentViewWalker();
    [PreserveSig] int get_RawViewWalker();
    [PreserveSig] int get_RawViewCondition();
    [PreserveSig] int get_ControlViewCondition();
    [PreserveSig] int get_ContentViewCondition();
    [PreserveSig] int CreateCacheRequest();
    [PreserveSig] int CreateTrueCondition();
    [PreserveSig] int CreateFalseCondition();
    [PreserveSig] int CreatePropertyCondition();
    [PreserveSig] int CreatePropertyConditionEx();
    [PreserveSig] int CreateAndCondition();
    [PreserveSig] int CreateAndConditionFromArray();
    [PreserveSig] int CreateAndConditionFromNativeArray();
    [PreserveSig] int CreateOrCondition();
    [PreserveSig] int CreateOrConditionFromArray();
    [PreserveSig] int CreateOrConditionFromNativeArray();
    [PreserveSig] int CreateNotCondition();
    [PreserveSig] int AddAutomationEventHandler();
    [PreserveSig] int RemoveAutomationEventHandler();
    [PreserveSig] int AddPropertyChangedEventHandlerNativeArray();
    [PreserveSig] int AddPropertyChangedEventHandler();
    [PreserveSig] int RemovePropertyChangedEventHandler();
    [PreserveSig] int AddStructureChangedEventHandler();
    [PreserveSig] int RemoveStructureChangedEventHandler();
    [PreserveSig] int AddFocusChangedEventHandler();
    [PreserveSig] int RemoveFocusChangedEventHandler();
    [PreserveSig] int RemoveAllEventHandlers();
    [PreserveSig] int IntNativeArrayToSafeArray();
    [PreserveSig] int IntSafeArrayToNativeArray();
    [PreserveSig] int RectToVariant();
    [PreserveSig] int VariantToRect();
    [PreserveSig] int SafeArrayToRectNativeArray();
    [PreserveSig] int CreateProxyFactoryEntry();
    [PreserveSig] int GetProxyFactoryMapping();
    [PreserveSig] int GetPropertyProgrammaticName();
    [PreserveSig] int GetPatternProgrammaticName();
    [PreserveSig] int PollForPotentialSupportedPatterns();
    [PreserveSig] int PollForPotentialSupportedProperties();
    [PreserveSig] int CheckNotSupported();
    [PreserveSig] int get_ReservedNotSupportedValue();
    [PreserveSig] int get_ReservedMixedAttributeValue();
    [PreserveSig] int ElementFromIAccessible();
    [PreserveSig] int ElementFromIAccessibleBuildCache();
}

[ComImport, Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation2 : IUIAutomation
{
    [PreserveSig] int get_AutoSetFocus([MarshalAs(UnmanagedType.Bool)] out bool value);
    [PreserveSig] int get_ConnectionTimeout(out uint value);
    [PreserveSig] int get_TransactionTimeout(out uint value);
    [PreserveSig] int put_AutoSetFocus([MarshalAs(UnmanagedType.Bool)] bool value);
    [PreserveSig] int put_ConnectionTimeout(uint value);
    [PreserveSig] int put_TransactionTimeout(uint value);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    [PreserveSig] int SetFocus();
    [PreserveSig] int GetRuntimeId([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] runtimeId);
    [PreserveSig] int FindFirst();
    [PreserveSig] int FindAll();
    [PreserveSig] int FindFirstBuildCache();
    [PreserveSig] int FindAllBuildCache();
    [PreserveSig] int BuildUpdatedCache();
    [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int GetCurrentPropertyValueEx();
    [PreserveSig] int GetCachedPropertyValue();
    [PreserveSig] int GetCachedPropertyValueEx();
    [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.Interface)] out object pattern);
    [PreserveSig] int GetCurrentPatternAs();
}

[ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextPattern
{
    [PreserveSig] int GetSelection([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)] out IUIAutomationTextRange[] ranges);
    [PreserveSig] int GetVisibleRanges();
    [PreserveSig] int RangeFromChild();
    [PreserveSig] int RangeFromPoint();
    [PreserveSig] int get_DocumentRange([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
    [PreserveSig] int get_SupportedTextSelection();
}

[ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRange
{
    [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
    [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange range, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    [PreserveSig] int CompareEndpoints(int endpoint, [MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange targetRange, int targetEndpoint, out int result);
    [PreserveSig] int ExpandToEnclosingUnit(int unit);
    [PreserveSig] int FindAttribute();
    [PreserveSig] int FindText();
    [PreserveSig] int GetAttributeValue();
    [PreserveSig] int GetBoundingRectangles();
    [PreserveSig] int GetEnclosingElement();
    [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text);
    [PreserveSig] int Move();
    [PreserveSig] int MoveEndpointByUnit();
    [PreserveSig] int MoveEndpointByRange(int endpoint, [MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange targetRange, int targetEndpoint);
    [PreserveSig] int Select();
    [PreserveSig] int AddToSelection();
    [PreserveSig] int RemoveFromSelection();
    [PreserveSig] int ScrollIntoView();
    [PreserveSig] int GetChildren();
}
