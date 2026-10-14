using System.Diagnostics;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Automation.Provider;
using Cotabby.Core.Focus;
using Cotabby.Win32.Interop;
using Microsoft.Extensions.Logging;

namespace Cotabby.Win32.Focus;

/// <summary>
/// UI Automation implementation of <see cref="IFocusTracker"/>.
/// </summary>
/// <remarks>
/// We subscribe to <see cref="Automation.AddAutomationFocusChangedEventHandler"/>
/// rather than polling like the macOS port. UIA event delivery happens on a
/// background pool thread, so consumers wanting to touch UI must hop through
/// their own <see cref="SynchronizationContext"/>.
///
/// Reads against an element can throw at any time (the host process exits,
/// COM tear-down races, RPC server unavailable). Every read is wrapped in a
/// best-effort try/catch and falls back to <c>null</c>.
/// </remarks>
public sealed class UiaFocusTracker : IFocusTracker
{
    private readonly ILogger<UiaFocusTracker> _logger;
    private readonly object _gate = new();

    private AutomationFocusChangedEventHandler? _handler;
    private FocusedField? _current;
    private bool _running;
    private bool _disposed;

    public UiaFocusTracker(ILogger<UiaFocusTracker> logger) => _logger = logger;

    public FocusedField? Current => _fakeField ?? Volatile.Read(ref _current);

    public event EventHandler<FocusedField?>? FocusChanged;

    private FocusedField? _fakeField;

    /// <summary>
    /// Test backdoor: when non-null, <see cref="Current"/> and <see cref="Refresh"/>
    /// return this synthetic field instead of consulting UIA. Used by the App's
    /// self-test to exercise the coordinator without depending on real Windows
    /// focus behavior. Never set from production code paths.
    /// </summary>
    public void SetFakeFieldForTesting(FocusedField? fake)
    {
        _fakeField = fake;
        FocusChanged?.Invoke(this, fake);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            _running = true;

            _handler = OnFocusChanged;
            Automation.AddAutomationFocusChangedEventHandler(_handler);
            _logger.LogInformation("UIA focus tracker started.");

            // Seed the current snapshot so consumers calling Current right after
            // Start don't see null until the first focus change.
            TryCaptureFocused(out var initial);
            _current = initial;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            if (_handler is not null)
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(_handler); }
                catch (Exception ex) { _logger.LogWarning(ex, "RemoveAutomationFocusChangedEventHandler threw."); }
                _handler = null;
            }
            _current = null;
            _logger.LogInformation("UIA focus tracker stopped.");
        }
    }

    public FocusedField? Refresh()
    {
        if (_fakeField is not null) return _fakeField;
        TryCaptureFocused(out var snap);
        _current = snap;
        return snap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
    {
        if (!_running) return;

        try
        {
            TryCaptureFocused(out var snap);
            // Treat focus to non-editable as "left supported field" so the
            // coordinator tears down its session and we don't leak ghost text.
            if (snap is null)
            {
                _current = null;
                FocusChanged?.Invoke(this, null);
                return;
            }

            // Identity changed if pid changed or element bounds shifted significantly.
            // We don't have a stable element identity through ElementHandle since
            // UIA's RuntimeId is a managed int[] and equality isn't trivial across
            // events. Treat every focus event as a state change — cheap enough.
            _current = snap;
            FocusChanged?.Invoke(this, snap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Focus change handling failed.");
        }
    }

    private bool TryCaptureFocused(out FocusedField? snapshot)
    {
        snapshot = null;
        AutomationElement? element;
        try
        {
            element = AutomationElement.FocusedElement;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AutomationElement.FocusedElement threw.");
            return false;
        }
        if (element is null) return false;

        try
        {
            var info = element.Current;
            string? stableFieldIdentity = StableFieldIdentity(element, info);
            string? automationId = SafeRead(() => info.AutomationId);
            string? name = SafeRead(() => info.Name);
            string? helpText = SafeRead(() => info.HelpText);
            // Native WinForms/WPF login forms label their input with a separate
            // Label control rather than the input's own Name, so LabeledBy is the
            // accessible label that actually carries the text.
            string? labeledByName = ReadLabeledByName(element);
            string controlType = ControlTypeName(info.ControlType);

            // Privacy boundary: a password flag or credential-shaped metadata must
            // reject the field BEFORE any TextPattern/ValuePattern payload read.
            // (Upstream #848.) This also covers password fields, which the tracker
            // used to read first and only mark secure afterwards.
            bool isPassword = SafeRead(() => info.IsPassword);
            if (IsCredentialBlocked(controlType, name, helpText, labeledByName, automationId, isPassword, text: null))
            {
                snapshot = BlockedSnapshot(element, info, isPassword, stableFieldIdentity);
                return true;
            }

            if (!IsEditableField(element, info)) return false;

            // Neutral metadata: the payload read below is the single one allowed
            // for the address-shape fallback (it also produces the completion
            // prefix). If it reveals a credential, the text must not travel any
            // further — the returned snapshot carries no text.
            var (text, caretOffset) = ReadTextAndCaret(element);
            if (IsCredentialBlocked(controlType, name, helpText, labeledByName, automationId, isPassword, text))
            {
                _logger.LogInformation(
                    "Sign-in/verification field left alone (host={Host}, control={Control}). " +
                    "No text, metadata value or request leaves this read.",
                    TryGetProcessName(info.ProcessId), controlType);
                snapshot = BlockedSnapshot(element, info, isPassword, stableFieldIdentity);
                return true;
            }

            var caretRect = ReadCaretRect(element);
            var fieldRect = ToScreenRect(info.BoundingRectangle);

            snapshot = new FocusedField
            {
                ElementHandle = element,
                StableFieldIdentity = stableFieldIdentity,
                ProcessId = info.ProcessId,
                ProcessName = TryGetProcessName(info.ProcessId),
                Text = text,
                CaretOffset = caretOffset,
                CaretRect = caretRect.IsEmpty ? fieldRect : caretRect,
                FieldRect = fieldRect,
                IsSingleLine = controlType == "Edit",
                IsSecure = isPassword,
                IsCredentialField = false,
            };
            return true;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Snapshot build failed.");
            return false;
        }
    }

    /// <summary>
    /// Builds a snapshot for a field Cotabby must not suggest into. It carries the
    /// geometry and process identity (so the coordinator tears down cleanly) but
    /// deliberately NO text: the payload is never propagated once the field is
    /// recognized as a sign-in/verification input.
    /// </summary>
    private FocusedField BlockedSnapshot(
        AutomationElement element,
        AutomationElement.AutomationElementInformation info,
        bool isPassword,
        string? stableFieldIdentity)
    {
        var fieldRect = ToScreenRect(info.BoundingRectangle);
        return new FocusedField
        {
            ElementHandle = element,
            StableFieldIdentity = stableFieldIdentity,
            ProcessId = info.ProcessId,
            ProcessName = TryGetProcessName(info.ProcessId),
            Text = string.Empty,
            CaretOffset = 0,
            CaretRect = fieldRect,
            FieldRect = fieldRect,
            IsSingleLine = ControlTypeName(info.ControlType) == "Edit",
            IsSecure = isPassword,
            IsCredentialField = true,
        };
    }

    /// <summary>
    /// The single credential decision. Metadata is evaluated before any payload
    /// read; when <paramref name="text"/> is supplied (the one read allowed for a
    /// neutral field) it is the address-shape fallback.
    /// </summary>
    private static bool IsCredentialBlocked(
        string controlType,
        string? name,
        string? helpText,
        string? labeledByName,
        string? automationId,
        bool isPassword,
        string? text) =>
        CredentialFieldDetector.IsCredentialField(
            controlType,
            Labels(name, helpText, labeledByName),
            automationId,
            text,
            isPassword);

    /// <summary>UIA accessible-name sources, each evaluated independently so a
    /// prose word in one cannot mask a credential word in another.</summary>
    private static IEnumerable<string?> Labels(string? name, string? helpText, string? labeledByName)
    {
        yield return name;
        yield return helpText;
        yield return labeledByName;
    }

    /// <summary>Name of the element this control is labelled by, if any.</summary>
    private static string? ReadLabeledByName(AutomationElement element)
    {
        try
        {
            return element.GetCurrentPropertyValue(AutomationElement.LabeledByProperty) is AutomationElement label
                ? SafeReadName(label)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeReadName(AutomationElement element)
    {
        try { return element.Current.Name; }
        catch (Exception) { return null; }
    }

    private static string ControlTypeName(ControlType controlType) =>
        controlType == ControlType.Edit ? "Edit"
        : controlType == ControlType.Document ? "Document"
        : controlType == ControlType.Pane ? "Pane"
        : controlType == ControlType.ComboBox ? "ComboBox"
        : controlType.ProgrammaticName ?? string.Empty;

    /// <summary>
    /// Stable identity for a field. UIA's RuntimeId is the primary source, but a
    /// provider is allowed to throw or to return an empty array (a proxied or
    /// custom control, an element torn down mid-read, a host that does not
    /// implement <c>IRawElementProviderFragment.GetRuntimeId</c>). Losing it must
    /// not silently switch acceptance off, so we fall back to a bounded,
    /// content-free descriptor; only when even that is unavailable do we return
    /// null, which the coordinator reads as UNPROVEN ownership — never as proof of
    /// a different field.
    /// </summary>
    private string? StableFieldIdentity(
        AutomationElement element,
        AutomationElement.AutomationElementInformation info)
    {
        int[]? runtimeId;
        try
        {
            runtimeId = element.GetRuntimeId();
        }
        catch (Exception)
        {
            runtimeId = null;
        }

        return runtimeId is { Length: > 0 }
            ? string.Join('.', runtimeId)
            : FallbackFieldIdentity(info);
    }

    /// <summary>
    /// Content-free, length-bounded stand-in identity used when the runtime id is
    /// missing: control type + AutomationId + accessible name + owning window
    /// handle + process id. Field text is never read here, and every string part
    /// is truncated so the value cannot grow with host content.
    /// </summary>
    /// <remarks>
    /// Granularity is per host window in the common case where a host gives its
    /// edits neither an AutomationId nor an accessible name — the same
    /// granularity the pre-identity process/window check had, and strictly better
    /// than no identity, because fields the host does distinguish stay
    /// distinguished. If a host derives its accessible name from the field content
    /// the fallback flaps as text changes; the failure mode there is a Tab that
    /// passes through to the host, never an insertion into a different field.
    /// </remarks>
    private string? FallbackFieldIdentity(AutomationElement.AutomationElementInformation info)
    {
        string controlType = ControlTypeNameOrEmpty(info.ControlType);
        string automationId = Truncate(SafeRead(() => info.AutomationId));
        string name = Truncate(SafeRead(() => info.Name));
        int windowHandle = SafeRead(() => info.NativeWindowHandle);
        int processId = SafeRead(() => info.ProcessId);

        if (controlType.Length == 0 && automationId.Length == 0 && name.Length == 0
            && windowHandle == 0 && processId == 0)
        {
            return null;
        }

        // Fixed key order and fixed separators, so two fields cannot collide by
        // reordering; invariant formatting keeps the value identical across
        // cultures, which matters because it is only ever compared for equality.
        return string.Concat(
            "ct=", controlType,
            "|aid=", automationId,
            "|nm=", name,
            "|hwnd=", windowHandle.ToString(CultureInfo.InvariantCulture),
            "|pid=", processId.ToString(CultureInfo.InvariantCulture));
    }

    private static string ControlTypeNameOrEmpty(ControlType? controlType) =>
        controlType is null ? string.Empty : ControlTypeName(controlType);

    /// <summary>Bounded slice of a metadata string so a fallback identity cannot
    /// grow with host content.</summary>
    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= MaxIdentityPartLength ? value : value[..MaxIdentityPartLength];

    private const int MaxIdentityPartLength = 128;

    private string? SafeRead(Func<string> read)
    {
        try { return read(); }
        catch (Exception) { return null; }
    }

    private int SafeRead(Func<int> read)
    {
        try { return read(); }
        catch (Exception) { return 0; }
    }

    private bool SafeRead(Func<bool> read)
    {
        try { return read(); }
        catch (Exception) { return false; }
    }

    private static bool IsEditableField(
        AutomationElement element, AutomationElement.AutomationElementInformation info)
    {
        try
        {
            if (!info.IsEnabled) return false;
            // IsKeyboardFocusable is unreliable — Win11 Notepad's RichEditBox
            // exposes false for the document control even though it accepts
            // keystrokes — so we don't gate on it. We rely on the control type
            // and a TextPattern / ValuePattern probe instead.

            var ct = info.ControlType;
            if (ct != ControlType.Edit && ct != ControlType.Document && ct != ControlType.Pane)
            {
                return false;
            }

            // Read-only fields (URL bars in read mode, disabled inputs) shouldn't
            // get suggestions. Best-effort — not every editor exposes ValuePattern.
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw)
                && raw is ValuePattern vp && vp.Current.IsReadOnly)
            {
                return false;
            }

            // Require at least one of TextPattern or ValuePattern so we know we
            // can actually read text out of this element. Without that the
            // request would have an empty prefix. Called only after the
            // credential check, so this probe never runs on a blocked field.
            bool hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
            bool hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
            return hasText || hasValue;
        }
        catch (Exception) { return false; }
    }

    private (string text, int caretOffset) ReadTextAndCaret(AutomationElement element)
    {
        // Prefer TextPattern: it gives both full text and the caret-as-selection.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawText)
            && rawText is TextPattern textPattern)
        {
            try
            {
                // Cap to 16KB — coordinators only need a window around the caret.
                string full = textPattern.DocumentRange.GetText(16 * 1024);
                int caret = 0;
                var sel = textPattern.GetSelection();
                if (sel is { Length: > 0 })
                {
                    var range = sel[0];
                    var preCaret = textPattern.DocumentRange.Clone();
                    preCaret.MoveEndpointByRange(
                        TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                    caret = preCaret.GetText(16 * 1024).Length;
                }
                return (full, caret);
            }
            catch (Exception) { /* fall through */ }
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue)
            && rawValue is ValuePattern vp)
        {
            string v = vp.Current.Value ?? string.Empty;
            return (v, v.Length); // caret at end as a fallback
        }

        return (string.Empty, 0);
    }

    private ScreenRect ReadCaretRect(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var raw)
            && raw is TextPattern textPattern)
        {
            try
            {
                var sel = textPattern.GetSelection();
                if (sel is { Length: > 0 })
                {
                    var rects = sel[0].GetBoundingRectangles();
                    if (rects.Length > 0)
                    {
                        return ToScreenRect(rects[^1]);
                    }
                }
            }
            catch (Exception) { /* fall through */ }
        }
        return ScreenRect.Empty;
    }

    private static ScreenRect ToScreenRect(System.Windows.Rect r) =>
        new(r.X, r.Y, r.Width, r.Height);

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception) { return string.Empty; }
    }
}
