using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using Microsoft.Extensions.Logging;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Insertion;

/// <summary>
/// <see cref="ITextInserter"/> that pastes via the clipboard: save the
/// current clipboard, write the suggestion, synthesize Ctrl+V to make the
/// host's standard paste handler insert it atomically, then restore the
/// original clipboard.
/// </summary>
/// <remarks>
/// Why clipboard paste instead of per-char SendInput Unicode:
/// the obvious SendInput KEYEVENTF_UNICODE approach drops events in many
/// hosts — Notepad in particular accepts only the first ~10 of a 40-char
/// burst before its input queue overflows and silently discards the rest
/// (verified via post-insertion UIA reads). Electron-based editors fare a
/// little better but still drop characters under load. A single Ctrl+V
/// keystroke routes through the host's paste pipeline which is built for
/// atomic multi-character insertion of arbitrary length.
///
/// Why bracket with <c>BlockInput</c>:
/// physical keystrokes arriving between our Ctrl+V keydown and Ctrl+V
/// keyup, or between paste and clipboard restore, would otherwise be
/// processed as live input and interleave with the paste.
///
/// Trade-off: clipboard contents change during a long paste. Native formats are
/// materialized before replacement and restored only while we still own the
/// clipboard sequence; unreadable formats refuse insertion without changing it.
/// Input blocking, injection and unblocking run on the same STA thread.
/// </remarks>
public sealed class SendInputTextInserter : ITextInserter
{
    private readonly ILogger<SendInputTextInserter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _insertionStrategy = "auto";

    public SendInputTextInserter(ILogger<SendInputTextInserter> logger) => _logger = logger;

    /// <summary>
    /// Atomically updated global strategy. InsertAsync resolves it to a concrete
    /// path before waiting on the insertion gate, so a queued call is unaffected
    /// by later settings changes.
    /// </summary>
    public string? InsertionStrategy
    {
        get => Volatile.Read(ref _insertionStrategy);
        set => Volatile.Write(ref _insertionStrategy, value);
    }

    public async Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return true;

        string? strategySnapshot = InsertionStrategy;
        if (!InsertionStrategySelector.TrySelect(strategySnapshot, text, out var insertionMethod))
        {
            _logger.LogWarning("Unsupported insertion strategy; insertion refused.");
            return false;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            if (insertionMethod == InsertionMethod.Unicode)
            {
                _logger.LogInformation(
                    "Inserter (unicode) {Len} chars target.proc={Proc}",
                    text.Length, target.ProcessName);
                return await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (!TargetStillFocused(target)) return false;
                    return SendUnicodeString(text);
                }, ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Inserter (clipboard paste) received {Len} chars target.pid={Pid} target.proc={Proc}",
                text.Length, target.ProcessId, target.ProcessName);

            return await OnStaThreadAsync(() => PasteOnStaThread(target, text, ct)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool PasteOnStaThread(FocusedField target, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TargetStillFocused(target)) return false;
        // Materialize every native format before replacing its OLE owner. If a
        // format cannot be copied, refuse the paste without touching clipboard.
        DataObject? savedClipboard;
        try
        {
            var original = Clipboard.GetDataObject();
            savedClipboard = original is null ? null : new DataObject();
            if (original is not null)
            {
                foreach (var format in original.GetFormats(autoConvert: false))
                {
                    var data = original.GetData(format, autoConvert: false);
                    if (data is null) return false;
                    savedClipboard!.SetData(format, data, autoConvert: false);
                }
            }
        }
        catch (Exception) { return false; }
        if (!TryWriteClipboardText(text)) return false;
        uint ownedClipboardSequence = GetClipboardSequenceNumber();
        try
        {
            Thread.Sleep(15);
            ct.ThrowIfCancellationRequested();
            if (!TargetStillFocused(target)) return false;
            // Block, inject and unblock on ONE native thread. An await here can
            // move injection to a thread whose input Windows is still blocking.
            bool blocked = BlockInput(true);
            try
            {
                bool sent = SendCtrlV();
                // Dispatch is irreversible. Cancellation after this point must
                // not turn a sent paste into a reported refusal or skip cleanup.
                Thread.Sleep(80);
                return sent;
            }
            finally
            {
                if (blocked) BlockInput(false);
            }
        }
        finally
        {
            // Do not overwrite a clipboard copied by another app during paste.
            if (GetClipboardSequenceNumber() == ownedClipboardSequence)
            {
                try
                {
                    if (savedClipboard is null) Clipboard.Clear();
                    else Clipboard.SetDataObject(savedClipboard, copy: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Clipboard restore failed (type={ExceptionType}).", ex.GetType().Name);
                }
            }
        }
    }

    // Runs off the WPF dispatcher: querying UIA in-process from the UI thread
    // can deadlock. Do not trust a snapshot across the semaphore/clipboard waits.
    private static bool TargetStillFocused(FocusedField target)
    {
        if (target.IsCredentialField || target.IsSecure
            || target.ElementHandle is not AutomationElement expected) return false;
        try
        {
            var current = AutomationElement.FocusedElement;
            if (current is null || current.Current.ProcessId != target.ProcessId
                || current.Current.IsPassword || !current.Current.HasKeyboardFocus
                || !Automation.Compare(expected, current)) return false;
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint pid);
            return foreground != IntPtr.Zero && pid == target.ProcessId;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Run <paramref name="func"/> on a fresh STA thread. WPF's UI thread is
    /// STA but calling InsertAsync from there blocks on the SemaphoreSlim
    /// above — so spin up a one-shot STA helper. The work itself is cheap
    /// (couple of OLE calls), so the thread creation cost is fine.
    /// </summary>
    private static Task<T> OnStaThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    private static bool TryWriteClipboardText(string text)
    {
        try
        {
            // SetDataObject(copy: true) leaves the clipboard owning a separate
            // copy of the string so our process can exit without invalidating
            // the host's just-read paste.
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    /// <summary>
    /// Send Ctrl-down, V-down, V-up, Ctrl-up via SendInput. We use regular
    /// virtual-key SendInput rather than KEYEVENTF_UNICODE because Ctrl+V is
    /// a virtual-key shortcut, not a character.
    /// </summary>
    private static bool SendCtrlV()
    {
        var inputs = new INPUT[]
        {
            MakeVkInput(VK_CONTROL, keyUp: false),
            MakeVkInput(VK_V,       keyUp: false),
            MakeVkInput(VK_V,       keyUp: true),
            MakeVkInput(VK_CONTROL, keyUp: true),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == (uint)inputs.Length;
    }

    /// <summary>
    /// Type <paramref name="text"/> as one SendInput batch of
    /// KEYEVENTF_UNICODE down/up pairs. UTF-16 code units go through as-is,
    /// so surrogate pairs (emoji) arrive intact.
    /// </summary>
    private static bool SendUnicodeString(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = MakeUnicodeInput(text[i], keyUp: false);
            inputs[i * 2 + 1] = MakeUnicodeInput(text[i], keyUp: true);
        }
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent > 0 && sent != (uint)inputs.Length)
            throw new IndeterminateTextInsertionException();
        return sent == (uint)inputs.Length;
    }

    private static INPUT MakeUnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static INPUT MakeVkInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

}
