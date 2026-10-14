using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Cotabby.Tools.DoubleTapSmoke;

internal sealed record FixtureState(string Text, int Caret);
internal sealed record FixtureCommand(
    int Sequence, int ProcessId, string AutomationId, string Operation,
    string? ExpectedText, int ExpectedCaret, string? Text, int Caret);
internal sealed record FixtureReply(
    int Sequence, bool Success, string Reason, int ProcessId,
    string AutomationId, string Text, int Caret);

internal sealed class FixtureProcess : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Process _process;
    private readonly string _runDirectory;
    private readonly AutomationElement _editor;
    private readonly AutomationElement? _secondaryEditor;
    private readonly object _commandGate = new();
    private int _commandSequence;
    private bool _disposed;

    private FixtureProcess(Process process, string runDirectory, int pid, long hwnd,
        string caseId, AutomationElement editor, AutomationElement? secondaryEditor)
    {
        _process = process;
        _runDirectory = runDirectory;
        Pid = pid;
        Hwnd = hwnd;
        CaseId = caseId;
        _editor = editor;
        _secondaryEditor = secondaryEditor;
        EditorAutomationId = "fixture-editor-" + caseId;
        SecondaryEditorAutomationId = "fixture-editor-secondary-" + caseId;
        NextAutomationId = "fixture-next-" + caseId;
    }

    public int Pid { get; }
    public long Hwnd { get; }
    public string CaseId { get; }
    public string EditorAutomationId { get; }
    public string SecondaryEditorAutomationId { get; }
    public string NextAutomationId { get; }
    public bool HasSecondaryEditor => _secondaryEditor is not null;

    public static FixtureProcess Start(string caseId)
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), "cotabby-doubletap-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        Process? process = null;
        try
        {
            string host = Environment.ProcessPath ?? throw Program.ProofException.Semantic("current executable path is unavailable");
            var start = new ProcessStartInfo { FileName = host, UseShellExecute = false, CreateNoWindow = false };
            if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add("--fixture");
            start.ArgumentList.Add(caseId);
            start.ArgumentList.Add(runDirectory);
            try { process = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw Program.ProofException.Harness("fixture child process could not start (Win32 error " + ex.NativeErrorCode.ToString(CultureInfo.InvariantCulture) + ")");
            }
            if (process is null) throw Program.ProofException.Semantic("WPF fixture child process did not start");

            string readyPath = Path.Combine(runDirectory, "ready.txt");
            string? ready = null;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(18))
            {
                if (File.Exists(readyPath))
                {
                    try { ready = File.ReadAllText(readyPath); } catch (IOException) { }
                    if (ready is not null) break;
                }
                if (process.HasExited) throw Program.ProofException.Semantic("WPF fixture exited before readiness");
                Thread.Sleep(40);
            }
            if (ready is null) throw Program.ProofException.Semantic("WPF fixture readiness deadline expired");

            var values = ready.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
            if (!values.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) || pid != process.Id ||
                !values.TryGetValue("hwnd", out var hwndText) || !long.TryParse(hwndText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long hwnd) || hwnd == 0 ||
                !values.TryGetValue("title", out var title))
                throw Program.ProofException.Semantic("WPF fixture readiness did not identify its own PID/window");

            bool hasSecondary = caseId == "same-process";
            var windowDeadline = Stopwatch.StartNew();
            AutomationElement? window = null;
            AutomationElement? editor = null;
            AutomationElement? secondaryEditor = null;
            string editorId = "fixture-editor-" + caseId;
            string secondaryId = "fixture-editor-secondary-" + caseId;
            while (windowDeadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                try
                {
                    window = AutomationElement.FromHandle(new IntPtr(hwnd));
                    if (window is not null && window.Current.ProcessId == pid && window.Current.NativeWindowHandle == hwnd && window.Current.Name == title)
                    {
                        editor = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, editorId));
                        if (hasSecondary)
                            secondaryEditor = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, secondaryId));
                        if (editor is not null && editor.Current.ProcessId == pid &&
                            (!hasSecondary || secondaryEditor is not null && secondaryEditor.Current.ProcessId == pid)) break;
                    }
                }
                catch (ElementNotAvailableException) { }
                Thread.Sleep(50);
            }
            if (window is null || editor is null || hasSecondary && secondaryEditor is null)
                throw Program.ProofException.Semantic("fixture WPF controls were not discoverable through UIA");
            return new FixtureProcess(process, runDirectory, pid, hwnd, caseId, editor, secondaryEditor);
        }
        catch
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
            try { Directory.Delete(runDirectory, recursive: true); } catch { }
            throw;
        }
    }

    public void FocusEditor() => FocusControl(EditorAutomationId, _editor);

    public void FocusSecondaryEditor()
    {
        if (_secondaryEditor is null) throw Program.ProofException.Semantic("same-process secondary fixture control is missing");
        FocusControl(SecondaryEditorAutomationId, _secondaryEditor);
    }

    private void FocusControl(string automationId, AutomationElement element)
    {
        element.SetFocus();
        if (!WaitUntil(() => IsFocused(automationId), TimeSpan.FromSeconds(4)))
            throw Program.ProofException.Semantic("real UIA focus did not land on the requested fixture control");
    }

    public FixtureState ReadState() => ReadStateFor(EditorAutomationId, _editor);

    public FixtureState ReadSecondaryState()
    {
        if (_secondaryEditor is null) throw Program.ProofException.Semantic("same-process secondary fixture control is missing");
        return ReadStateFor(SecondaryEditorAutomationId, _secondaryEditor);
    }

    private FixtureState ReadStateFor(string automationId, AutomationElement element)
    {
        FixtureReply reply = SendCommand(automationId, "read", null, 0, null, 0);
        RequireReplyIdentity(reply, automationId);
        if (!reply.Success) throw Program.ProofException.Semantic("fixture state protocol read failed: " + reply.Reason);
        string uiaText = ReadTextFor(automationId, element);
        if (!string.Equals(reply.Text, uiaText, StringComparison.Ordinal))
            throw Program.ProofException.Semantic("fixture protocol text disagreed with UIA ValuePattern readback");
        return new FixtureState(uiaText, reply.Caret);
    }

    private string ReadTextFor(string automationId, AutomationElement element)
    {
        if (element.Current.ProcessId != Pid || element.Current.AutomationId != automationId)
            throw Program.ProofException.Semantic("fixture TextBox UIA identity changed");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern value)
            throw Program.ProofException.Semantic("fixture TextBox does not expose UIA ValuePattern");
        return value.Current.Value;
    }

    public FixtureReply TrySetState(string automationId, string expectedText, int expectedCaret, string text, int caret)
    {
        RequireDummyText(expectedText);
        RequireDummyText(text);
        if (!IsFocused(automationId))
            return new FixtureReply(0, false, "ParentFocusMismatch", Pid, automationId, "", 0);
        return SendCommand(automationId, "set", expectedText, expectedCaret, text, caret);
    }

    private FixtureReply SendCommand(string automationId, string operation,
        string? expectedText, int expectedCaret, string? text, int caret)
    {
        lock (_commandGate)
        {
            int sequence = ++_commandSequence;
            string requestPath = Path.Combine(_runDirectory, $"request-{sequence:D6}.json");
            string responsePath = Path.Combine(_runDirectory, $"response-{sequence:D6}.json");
            var command = new FixtureCommand(sequence, Pid, automationId, operation,
                expectedText, expectedCaret, text, caret);
            string temporaryPath = requestPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(command, JsonOptions));
            File.Move(temporaryPath, requestPath, overwrite: true);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(4))
            {
                if (_process.HasExited) throw Program.ProofException.Semantic("fixture exited while servicing a bounded state request");
                if (File.Exists(responsePath))
                {
                    string payload = File.ReadAllText(responsePath);
                    File.Delete(responsePath);
                    var reply = JsonSerializer.Deserialize<FixtureReply>(payload, JsonOptions);
                    if (reply is null || reply.Sequence != sequence)
                        throw Program.ProofException.Semantic("fixture state protocol returned a malformed response");
                    return reply;
                }
                Thread.Sleep(10);
            }
            throw Program.ProofException.Semantic("fixture state protocol deadline expired");
        }
    }

    private void RequireReplyIdentity(FixtureReply reply, string automationId)
    {
        if (reply.ProcessId != Pid || reply.AutomationId != automationId)
            throw Program.ProofException.Semantic("fixture protocol response identity did not match the target control");
    }

    public bool IsFocused(string automationId)
    {
        var focused = AutomationElement.FocusedElement;
        return focused is not null && focused.Current.ProcessId == Pid && focused.Current.AutomationId == automationId;
    }

    public string? FocusedAutomationId()
    {
        var focused = AutomationElement.FocusedElement;
        return focused is not null && focused.Current.ProcessId == Pid ? focused.Current.AutomationId : null;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            try { if (condition()) return true; }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(20);
        }
        return false;
    }

    private static void RequireDummyText(string text)
    {
        if (text.Length > 512 || text.Any(c => c != ' ' && (c < 'a' || c > 'z')))
            throw Program.ProofException.Semantic("fixture protocol rejected text outside the lowercase dummy-data alphabet");
    }

    private void RestoreFixtures()
    {
        RestoreControl(EditorAutomationId, _editor);
        if (_secondaryEditor is not null) RestoreControl(SecondaryEditorAutomationId, _secondaryEditor);
    }

    private void RestoreControl(string automationId, AutomationElement element)
    {
        try
        {
            FocusControl(automationId, element);
            var state = ReadStateFor(automationId, element);
            if (state.Text == "seed" && state.Caret == 4) return;
            var reply = TrySetState(automationId, state.Text, state.Caret, "seed", 4);
            RequireReplyIdentity(reply, automationId);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreFixtures();
        try { File.WriteAllText(Path.Combine(_runDirectory, "stop.marker"), "stop"); } catch { }
        try
        {
            if (!_process.HasExited && !_process.WaitForExit(2500)) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(2500);
        }
        catch { }
        _process.Dispose();
        try { Directory.Delete(_runDirectory, recursive: true); } catch { }
    }
}

internal static class FixtureWindow
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static int Run(string caseId, string runDirectory)
    {
        Directory.CreateDirectory(runDirectory);
        bool sameProcess = caseId == "same-process";
        var editor = CreateEditor("fixture-editor-" + caseId, tabIndex: 0);
        TextBox? secondaryEditor = sameProcess ? CreateEditor("fixture-editor-secondary-" + caseId, tabIndex: 1) : null;
        var next = new Button { Content = "Tab destination", Width = 180, Height = 32, Margin = new Thickness(12), TabIndex = sameProcess ? 2 : 1 };
        AutomationProperties.SetAutomationId(next, "fixture-next-" + caseId);
        AutomationProperties.SetName(next, "Proof fixture next control");
        var controls = new Dictionary<string, TextBox>(StringComparer.Ordinal) { ["fixture-editor-" + caseId] = editor };
        if (secondaryEditor is not null) controls["fixture-editor-secondary-" + caseId] = secondaryEditor;
        var panel = new StackPanel();
        panel.Children.Add(editor);
        if (secondaryEditor is not null) panel.Children.Add(secondaryEditor);
        panel.Children.Add(next);
        string title = "Cotabby DoubleTap Fixture " + caseId;
        var window = new Window
        {
            Title = title,
            Width = 440,
            Height = sameProcess ? 225 : 150,
            Content = panel,
            WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = true,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        string readyPath = Path.Combine(runDirectory, "ready.txt");
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) =>
        {
            if (File.Exists(Path.Combine(runDirectory, "stop.marker")))
            {
                window.Close();
                return;
            }
            ServiceCommands(runDirectory, controls);
        }, Dispatcher.CurrentDispatcher);
        window.Loaded += (_, _) =>
        {
            window.Activate();
            editor.Focus();
            editor.CaretIndex = editor.Text.Length;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                window.Activate();
                editor.Focus();
                editor.CaretIndex = editor.Text.Length;
                long hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64();
                string content = $"pid={Environment.ProcessId}\nhwnd={hwnd}\ntitle={title}\n";
                string temporary = readyPath + ".tmp";
                File.WriteAllText(temporary, content);
                File.Move(temporary, readyPath, overwrite: true);
            }));
            timer.Start();
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(window);
        return 0;
    }

    private static TextBox CreateEditor(string automationId, int tabIndex)
    {
        var editor = new TextBox
        {
            Text = "seed",
            CaretIndex = 4,
            Width = 360,
            Height = 34,
            Margin = new Thickness(12),
            FontSize = 16,
            AcceptsReturn = false,
            TabIndex = tabIndex,
        };
        AutomationProperties.SetAutomationId(editor, automationId);
        AutomationProperties.SetName(editor, "Proof fixture editor");
        return editor;
    }

    private static void ServiceCommands(string runDirectory, IReadOnlyDictionary<string, TextBox> controls)
    {
        foreach (string requestPath in Directory.EnumerateFiles(runDirectory, "request-*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            FixtureReply reply;
            try
            {
                var command = JsonSerializer.Deserialize<FixtureCommand>(File.ReadAllText(requestPath), JsonOptions);
                File.Delete(requestPath);
                reply = command is null
                    ? Failure(0, "", "MalformedRequest", "")
                    : Execute(command, controls);
            }
            catch
            {
                reply = Failure(0, "", "ProtocolException", "");
                try { File.Delete(requestPath); } catch { }
            }
            try
            {
                string responsePath = Path.Combine(runDirectory, $"response-{reply.Sequence:D6}.json");
                string temporaryPath = responsePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(reply, JsonOptions));
                File.Move(temporaryPath, responsePath, overwrite: true);
            }
            catch { }
        }
    }

    private static FixtureReply Execute(FixtureCommand command, IReadOnlyDictionary<string, TextBox> controls)
    {
        if (command.ProcessId != Environment.ProcessId || !controls.TryGetValue(command.AutomationId, out var control))
            return Failure(command.Sequence, command.AutomationId, "IdentityMismatch", "");
        if (command.Operation == "read")
            return Success(command, control);
        if (command.Operation != "set" || command.ExpectedText is null || command.Text is null ||
            command.Text.Length > 512 || command.Caret < 0 || command.Caret > command.Text.Length)
            return Failure(command.Sequence, command.AutomationId, "InvalidRequest", control.Text);
        if (!IsDummyText(command.ExpectedText) || !IsDummyText(command.Text))
            return Failure(command.Sequence, command.AutomationId, "InvalidDummyText", control.Text);
        var focused = AutomationElement.FocusedElement;
        if (focused is null || focused.Current.ProcessId != Environment.ProcessId ||
            focused.Current.AutomationId != command.AutomationId || !control.IsKeyboardFocusWithin)
            return Failure(command.Sequence, command.AutomationId, "FocusMismatch", control.Text);
        if (!string.Equals(control.Text, command.ExpectedText, StringComparison.Ordinal) || control.CaretIndex != command.ExpectedCaret)
            return Failure(command.Sequence, command.AutomationId, "StaleState", control.Text, control.CaretIndex);

        control.Text = command.Text;
        control.CaretIndex = command.Caret;
        return Success(command, control);
    }

    private static FixtureReply Success(FixtureCommand command, TextBox control) =>
        new(command.Sequence, true, "", Environment.ProcessId, command.AutomationId, control.Text, control.CaretIndex);

    private static FixtureReply Failure(int sequence, string automationId, string reason, string text, int caret = 0) =>
        new(sequence, false, reason, Environment.ProcessId, automationId, text, caret);

    private static bool IsDummyText(string text) =>
        text.Length <= 512 && text.All(c => c == ' ' || c is >= 'a' and <= 'z');
}
