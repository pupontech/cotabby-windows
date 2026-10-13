// ---------------------------------------------------------------------------
// Cotabby credential proof tool (Windows only).
//
// Purpose: prove against a REAL UIA focus element living in a separate
// synthetic fixture process that the production UiaFocusTracker.Refresh()
// refuses to read payload text out of recognized credential fields (username,
// verification code, card number, password flag) BEFORE any payload read
// happens, while ordinary writing / email subject / email search / multiline
// email body fields stay available for suggestions.
//
// Every payload in this tool is dummy data. Payload text is never printed:
// output is limited to case ids, read counters and PASS/FAIL.
//
// Modes:
//   (no args)                    run the whole proof in the parent process
//   --fixture <caseId> <runDir>  run the synthetic fixture child process
//   --case <caseId>              run a single case (parent, diagnostics)
//
// The fixture child is this same executable. It shows a WPF window whose only
// editable control is a custom FrameworkElement with a custom
// FrameworkElementAutomationPeer that:
//   * exposes ONLY ValuePattern (no TextPattern),
//   * reports fixed metadata (AutomationId / Name / HelpText) and IsPassword,
//   * counts every payload read (IValueProvider.Value getter) into a private
//     per-run stats file, so the parent can prove zero reads.
//
// The parent sets focus with real UIA (AutomationElement.SetFocus), verifies
// the focused element's process id and AutomationId, then calls the production
// UiaFocusTracker.Refresh(). No test backdoor, no policy re-implementation.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cotabby.Win32.Focus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Tools.CredentialSmoke;

internal static class Program
{
    private const string FixtureArgument = "--fixture";
    private const string CaseArgument = "--case";
    // Interactive-desktop CI: an environment that cannot give UIA focus to a
    // synthetic window is reported as SKIP (exit 0 with a marker) instead of as a
    // credential-protection failure. Assertion failures always fail.
    private const string AllowHarnessSkipArgument = "--allow-harness-skip";
    private const int HarnessSkipExitCode = 4;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 3 && string.Equals(args[0], FixtureArgument, StringComparison.Ordinal))
            {
                var fixtureCase = FixtureCases.ById(args[1]);
                if (fixtureCase is null) return FixtureExitCodeBadCase;
                return RunFixture(fixtureCase, args[2]);
            }

            string? onlyCase = null;
            var allowHarnessSkip = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], CaseArgument, StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    onlyCase = args[i + 1];
                }
                else if (string.Equals(args[i], AllowHarnessSkipArgument, StringComparison.Ordinal))
                {
                    allowHarnessSkip = true;
                }
            }

            return RunProof(onlyCase, allowHarnessSkip);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL harness: " + ex.GetType().Name + " in " + DescribeFrame(ex));
            return HarnessExitCode;
        }
    }

    private const int HarnessExitCode = 2;
    private const int FixtureExitCodeBadCase = 3;

    /// <summary>Frame description without any payload text (file + line only).</summary>
    private static string DescribeFrame(Exception ex)
    {
        var frame = new StackTrace(ex, false).GetFrames()?.FirstOrDefault();
        return frame is null
            ? "unknown frame"
            : Path.GetFileName(frame.GetMethod()?.DeclaringType?.Name ?? "unknown");
    }

    // -----------------------------------------------------------------------
    // Parent: proof driver
    // -----------------------------------------------------------------------

    private static int RunProof(string? onlyCase, bool allowHarnessSkip)
    {
        var cases = FixtureCases.All
            .Where(c => onlyCase is null || string.Equals(c.CaseId, onlyCase, StringComparison.Ordinal))
            .ToArray();

        if (cases.Length == 0)
        {
            Console.Error.WriteLine("FAIL harness: unknown case id '" + onlyCase + "'");
            return HarnessExitCode;
        }

        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "cotabby-credential-smoke",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" +
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        Directory.CreateDirectory(runRoot);

        Console.WriteLine("== Cotabby credential proof: synthetic real-UIA fixture ==");
        Console.WriteLine("payloads: dummy data only, never printed; cases=" + cases.Length.ToString(CultureInfo.InvariantCulture));

        var failures = 0;
        var harnessErrors = 0;
        var negativeControlLine = (string?)null;

        try
        {
            foreach (var fixtureCase in cases)
            {
                var result = RunCase(fixtureCase, Path.Combine(runRoot, fixtureCase.CaseId));
                if (!result.Passed)
                {
                    failures++;
                    if (result.HarnessError) harnessErrors++;
                }

                Console.WriteLine(result.Line);
                if (result.ExtraLine is not null)
                {
                    Console.WriteLine(result.ExtraLine);
                    negativeControlLine = result.ExtraLine;
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(runRoot)) Directory.Delete(runRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (negativeControlLine is null && onlyCase is null)
        {
            Console.WriteLine("negative-control          MISSING (spy liveness was not established) FAIL");
            failures++;
            harnessErrors++;
        }

        if (failures == 0)
        {
            Console.WriteLine("RESULT: PASS " + cases.Length.ToString(CultureInfo.InvariantCulture) + "/" +
                              cases.Length.ToString(CultureInfo.InvariantCulture) + " cases");
            return 0;
        }

        // Every failure was infrastructure, not a policy assertion: the runner
        // could not focus a synthetic UIA window. That is unverified, not unsafe.
        if (allowHarnessSkip && harnessErrors == failures)
        {
            Console.WriteLine("RESULT: SKIP " + failures.ToString(CultureInfo.InvariantCulture) + "/" +
                              cases.Length.ToString(CultureInfo.InvariantCulture) +
                              " cases — Windows UIA fixture focus unavailable on this host; " +
                              "credential guards NOT verified here");
            return HarnessSkipExitCode;
        }

        Console.WriteLine("RESULT: FAIL " + failures.ToString(CultureInfo.InvariantCulture) + "/" +
                          cases.Length.ToString(CultureInfo.InvariantCulture) + " cases, harness errors=" +
                          harnessErrors.ToString(CultureInfo.InvariantCulture));
        return harnessErrors > 0 ? HarnessExitCode : 1;
    }

    private static CaseResult RunCase(FixtureCase fixtureCase, string runDir)
    {
        var readyPath = Path.Combine(runDir, FixtureProtocol.ReadyFile);
        var statsPath = Path.Combine(runDir, FixtureProtocol.StatsFile);
        var resetPath = Path.Combine(runDir, FixtureProtocol.ResetFile);
        var stopPath = Path.Combine(runDir, FixtureProtocol.StopFile);

        Process? child = null;
        try
        {
            Directory.CreateDirectory(runDir);
            File.WriteAllText(statsPath, string.Empty);
            DeleteIfExists(readyPath);

            child = LaunchFixture(fixtureCase, runDir);

            var ready = WaitForReadyFile(readyPath, child, FixtureProtocol.ReadyTimeout);
            if (ready is null)
            {
                return Harness(fixtureCase, child, "fixture did not report readiness (missing fixture / early exit)");
            }

            if (ready.Pid != child.Id)
            {
                return Harness(fixtureCase, child, "fixture ready file process id mismatch");
            }

            if (!string.Equals(ready.AutomationId, fixtureCase.AutomationId, StringComparison.Ordinal) ||
                !string.Equals(ready.ControlType, fixtureCase.ControlType, StringComparison.Ordinal) ||
                !string.Equals(ready.Name, fixtureCase.Name ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(ready.HelpText, fixtureCase.HelpText ?? string.Empty, StringComparison.Ordinal) ||
                ready.IsPassword != fixtureCase.IsPassword)
            {
                return Harness(fixtureCase, child, "fixture declared metadata does not match the case table");
            }

            var window = ResolveFixtureWindow(ready, child, FixtureProtocol.WindowTimeout);
            if (window is null)
            {
                return Harness(fixtureCase, child, "fixture window not found for the child process");
            }

            var field = WaitForField(window, fixtureCase.AutomationId, FixtureProtocol.ElementTimeout);
            if (field is null)
            {
                return Harness(fixtureCase, child, "fixture control not found in the fixture window");
            }

            string metadataReason;
            if (!MetadataMatches(field, fixtureCase, out metadataReason))
            {
                return Harness(fixtureCase, child, metadataReason);
            }

            if (!TryFocusFixture(window, field, fixtureCase, child, ready.Hwnd, FixtureProtocol.FocusTimeout, out var focusReason))
            {
                return Harness(fixtureCase, child, focusReason);
            }

            if (!TryReadCounters(statsPath, out var readsBefore, out var writesBefore) || readsBefore != 0 || writesBefore != 0)
            {
                return Harness(fixtureCase, child, "fixture payload-read spy was not zero before the privacy check");
            }

            // Negative control: read the ordinary fixture payload through real
            // ValuePattern to prove the spy observes reads, then reset it.
            string? negativeControlLine = null;
            if (fixtureCase.RunNegativeControl)
            {
                var probe = TryReadValuePattern(field);
                if (probe is null || !string.Equals(probe, fixtureCase.Payload, StringComparison.Ordinal))
                {
                    return Harness(fixtureCase, child, "negative control could not read the fixture payload");
                }

                if (!TryReadCounters(statsPath, out var readsAfterProbe, out _) || readsAfterProbe < 1)
                {
                    return Harness(fixtureCase, child, "negative control: payload-read spy did not increment");
                }

                if (!TryResetCounters(resetPath, statsPath, FixtureProtocol.ResetTimeout))
                {
                    return Harness(fixtureCase, child, "negative control: payload-read spy counters were not reset");
                }

                negativeControlLine = "negative-control          ordinary fixture ValuePattern read=1, spy incremented, counters reset PASS";
            }

            // Privacy check through the production tracker: the only code under
            // test here is UiaFocusTracker.Refresh().
            using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
            var snapshot = tracker.Refresh();

            if (!TryReadCounters(statsPath, out var readsAfterRefresh, out var writesAfterRefresh))
            {
                return Harness(fixtureCase, child, "fixture payload-read spy could not be read after Refresh");
            }

            if (writesAfterRefresh != 0)
            {
                return Fail(fixtureCase, "wrote-field", readsAfterRefresh, "tracker wrote into the fixture control");
            }

            switch (fixtureCase.Expectation)
            {
                case FixtureExpectation.Allow:
                {
                    if (snapshot is null)
                    {
                        return Fail(fixtureCase, "blocked", readsAfterRefresh, "expected an available snapshot for an ordinary field");
                    }

                    if (snapshot.ProcessId != child.Id)
                    {
                        return Fail(fixtureCase, "foreign", readsAfterRefresh, "snapshot did not come from the fixture process");
                    }

                    if (!string.Equals(snapshot.Text, fixtureCase.Payload, StringComparison.Ordinal))
                    {
                        return Fail(fixtureCase, "text-mismatch", readsAfterRefresh, "ordinary field text was not readable through the tracker");
                    }

                    if (snapshot.IsSecure)
                    {
                        return Fail(fixtureCase, "marked-secure", readsAfterRefresh, "ordinary field was marked secure");
                    }

                    if (readsAfterRefresh < 1)
                    {
                        return Fail(fixtureCase, "no-read", readsAfterRefresh, "tracker reported text without reading the fixture control");
                    }

                    return Pass(fixtureCase, "available", readsAfterRefresh, negativeControlLine);
                }

                case FixtureExpectation.Reject:
                case FixtureExpectation.RejectAfterBoundedRead:
                {
                    var leaked = snapshot is not null && snapshot.Text.Length > 0;
                    if (leaked)
                    {
                        return Fail(fixtureCase, "text-returned", readsAfterRefresh, "credential-shaped field returned payload text downstream");
                    }

                    if (fixtureCase.Expectation == FixtureExpectation.Reject && readsAfterRefresh != 0)
                    {
                        return Fail(fixtureCase, "payload-read", readsAfterRefresh, "credential-shaped field payload was read before the guard");
                    }

                    if (fixtureCase.Expectation == FixtureExpectation.RejectAfterBoundedRead &&
                        readsAfterRefresh > FixtureProtocol.MaxBoundedReads)
                    {
                        return Fail(fixtureCase, "payload-read", readsAfterRefresh, "rejected field payload was read more than the bounded allowance");
                    }

                    // Eligibility control: the same fixture control is readable
                    // through real ValuePattern, so the rejection above is a
                    // policy decision, not an unusable fixture.
                    var probe = TryReadValuePattern(field);
                    if (probe is null || !string.Equals(probe, fixtureCase.Payload, StringComparison.Ordinal))
                    {
                        return Harness(fixtureCase, child, "eligibility control could not read the fixture payload");
                    }

                    if (!TryReadCounters(statsPath, out var readsAfterControl, out _) || readsAfterControl <= readsAfterRefresh)
                    {
                        return Harness(fixtureCase, child, "eligibility control did not increment the payload-read spy");
                    }

                    if (!TryResetCounters(resetPath, statsPath, FixtureProtocol.ResetTimeout))
                    {
                        return Harness(fixtureCase, child, "payload-read spy counters were not reset after the control read");
                    }

                    var decision = snapshot is null ? "blocked" : "blocked-blank";
                    if (fixtureCase.Expectation == FixtureExpectation.RejectAfterBoundedRead) decision += "-after-read";
                    return Pass(fixtureCase, decision, readsAfterRefresh, negativeControlLine);
                }

                default:
                    return Harness(fixtureCase, child, "unknown expectation in the case table");
            }
        }
        finally
        {
            ShutdownChild(child, stopPath);
            try { if (Directory.Exists(runDir)) Directory.Delete(runDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Line(FixtureCase fixtureCase, string decision, int reads, string status, string? reason)
    {
        var builder = new StringBuilder();
        builder.Append("case=").Append(fixtureCase.CaseId.PadRight(24));
        builder.Append(" expect=").Append(fixtureCase.ExpectationLabel.PadRight(12));
        builder.Append(" reads=").Append(reads.ToString(CultureInfo.InvariantCulture).PadRight(4));
        builder.Append(" decision=").Append(decision.PadRight(22));
        builder.Append(status);
        if (reason is not null) builder.Append(" (").Append(reason).Append(')');
        return builder.ToString();
    }

    private static CaseResult Pass(FixtureCase fixtureCase, string decision, int reads, string? extraLine) =>
        new(Line(fixtureCase, decision, reads, "PASS", null), true, false, extraLine);

    private static CaseResult Fail(FixtureCase fixtureCase, string decision, int reads, string reason) =>
        new(Line(fixtureCase, decision, reads, "FAIL", reason), false, false, null);

    private static CaseResult Harness(FixtureCase fixtureCase, Process? child, string reason) =>
        new(Line(fixtureCase, "harness-error", 0, "FAIL", reason), false, true, null);

    // -----------------------------------------------------------------------
    // Parent: fixture launch, discovery, focus
    // -----------------------------------------------------------------------

    private static Process LaunchFixture(FixtureCase fixtureCase, string runDir)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            throw new InvalidOperationException("process path unavailable");
        }

        startInfo.FileName = host;
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? Environment.GetCommandLineArgs()[0]);
        }

        startInfo.ArgumentList.Add(FixtureArgument);
        startInfo.ArgumentList.Add(fixtureCase.CaseId);
        startInfo.ArgumentList.Add(runDir);

        var child = Process.Start(startInfo);
        if (child is null)
        {
            throw new InvalidOperationException("fixture process did not start");
        }

        return child;
    }

    private static FixtureReady? WaitForReadyFile(string readyPath, Process child, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(readyPath))
            {
                var parsed = FixtureReady.TryParse(ReadAllTextOrNull(readyPath));
                if (parsed is not null) return parsed;
            }

            if (SafeHasExited(child)) return null;
            Thread.Sleep(50);
        }

        return null;
    }

    private static AutomationElement? ResolveFixtureWindow(FixtureReady ready, Process child, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (SafeHasExited(child)) return null;
            try
            {
                var window = AutomationElement.FromHandle(new IntPtr(ready.Hwnd));
                if (window is not null)
                {
                    var info = window.Current;
                    if (info.ProcessId == child.Id &&
                        info.NativeWindowHandle == ready.Hwnd &&
                        string.Equals(info.Name, ready.Title, StringComparison.Ordinal))
                    {
                        return window;
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }

            Thread.Sleep(100);
        }

        return null;
    }

    private static AutomationElement? WaitForField(AutomationElement window, string automationId, TimeSpan timeout)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var found = window.FindFirst(TreeScope.Descendants, condition);
                if (found is not null) return found;
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }

            Thread.Sleep(100);
        }

        return null;
    }

    private static bool MetadataMatches(AutomationElement field, FixtureCase fixtureCase, out string reason)
    {
        reason = string.Empty;
        AutomationElement.AutomationElementInformation info;
        try
        {
            info = field.Current;
        }
        catch (Exception)
        {
            reason = "fixture control metadata unavailable";
            return false;
        }

        if (!info.IsEnabled)
        {
            reason = "fixture control was disabled";
            return false;
        }

        var expectedControlType = string.Equals(fixtureCase.ControlType, "Document", StringComparison.Ordinal)
            ? ControlType.Document
            : ControlType.Edit;

        if (info.ControlType != expectedControlType)
        {
            reason = "fixture control type did not match the case table";
            return false;
        }

        if (!string.Equals(info.AutomationId, fixtureCase.AutomationId, StringComparison.Ordinal))
        {
            reason = "fixture AutomationId did not match the case table";
            return false;
        }

        if (!string.Equals(info.Name, fixtureCase.Name ?? string.Empty, StringComparison.Ordinal))
        {
            reason = "fixture accessible name channel was contaminated";
            return false;
        }

        if (!string.Equals(info.HelpText, fixtureCase.HelpText ?? string.Empty, StringComparison.Ordinal))
        {
            reason = "fixture help-text channel was contaminated";
            return false;
        }

        if (info.IsPassword != fixtureCase.IsPassword)
        {
            reason = "fixture password flag did not reach UIA";
            return false;
        }

        return true;
    }

    private static bool TryFocusFixture(
        AutomationElement window,
        AutomationElement field,
        FixtureCase fixtureCase,
        Process child,
        long hwnd,
        TimeSpan timeout,
        out string reason)
    {
        reason = string.Empty;
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            if (SafeHasExited(child))
            {
                reason = "fixture exited before UIA focus was established";
                return false;
            }

            try { field.SetFocus(); }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }

            var focused = TryGetFocusedElement();
            if (focused is not null)
            {
                try
                {
                    var info = focused.Current;
                    if (info.ProcessId == child.Id &&
                        string.Equals(info.AutomationId, fixtureCase.AutomationId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                catch (ElementNotAvailableException) { }
            }

            attempt++;
            if (attempt % 4 == 0)
            {
                try { SetForegroundWindow(new IntPtr(hwnd)); } catch (Exception) { }
                try { window.SetFocus(); } catch (Exception) { }
            }

            Thread.Sleep(250);
        }

        reason = "focused AutomationId was not the fixture control within the deadline";
        return false;
    }

    private static AutomationElement? TryGetFocusedElement()
    {
        try { return AutomationElement.FocusedElement; }
        catch (Exception) { return null; }
    }

    private static string? TryReadValuePattern(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern valuePattern)
            {
                return null;
            }

            return valuePattern.Current.Value;
        }
        catch (ElementNotAvailableException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // -----------------------------------------------------------------------
    // Parent: spy counters
    // -----------------------------------------------------------------------

    private static bool TryReadCounters(string statsPath, out int reads, out int writes)
    {
        reads = 0;
        writes = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                reads = 0;
                writes = 0;
                foreach (var line in File.ReadAllLines(statsPath))
                {
                    if (line.Length == 0) continue;
                    if (string.Equals(line, FixtureProtocol.ReadToken, StringComparison.Ordinal)) reads++;
                    else if (string.Equals(line, FixtureProtocol.WriteToken, StringComparison.Ordinal)) writes++;
                }

                return true;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }

        return false;
    }

    private static bool TryResetCounters(string resetPath, string statsPath, TimeSpan timeout)
    {
        try { File.WriteAllText(resetPath, "reset"); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(resetPath) &&
                TryReadCounters(statsPath, out var reads, out var writes) &&
                reads == 0 &&
                writes == 0)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static void ShutdownChild(Process? child, string stopPath)
    {
        if (child is null) return;

        try
        {
            if (!SafeHasExited(child))
            {
                try { File.WriteAllText(stopPath, "stop"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                if (!child.WaitForExit(FixtureProtocol.ChildStopMilliseconds))
                {
                    try { child.Kill(entireProcessTree: true); } catch (Exception) { }
                    child.WaitForExit(FixtureProtocol.ChildStopMilliseconds);
                }
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            child.Dispose();
        }
    }

    private static bool SafeHasExited(Process child)
    {
        try { return child.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static string? ReadAllTextOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // -----------------------------------------------------------------------
    // Child: synthetic fixture process
    // -----------------------------------------------------------------------

    private static int RunFixture(FixtureCase fixtureCase, string runDir)
    {
        Directory.CreateDirectory(runDir);

        var statsPath = Path.Combine(runDir, FixtureProtocol.StatsFile);
        var readyPath = Path.Combine(runDir, FixtureProtocol.ReadyFile);
        var resetPath = Path.Combine(runDir, FixtureProtocol.ResetFile);
        var stopPath = Path.Combine(runDir, FixtureProtocol.StopFile);

        if (!File.Exists(statsPath)) File.WriteAllText(statsPath, string.Empty);
        DeleteIfExists(readyPath);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var field = new FixtureField(fixtureCase, statsPath);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "Cotabby synthetic credential fixture (dummy data)",
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(field);

        var window = new Window
        {
            Title = FixtureProtocol.TitleFor(fixtureCase),
            Width = 420,
            Height = 150,
            Content = panel,
            WindowStyle = WindowStyle.ToolWindow,
            ShowInTaskbar = true,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(40),
            DispatcherPriority.Background,
            (_, _) =>
            {
                if (File.Exists(resetPath)) FixtureStats.Reset(statsPath, resetPath);

                if (File.Exists(stopPath))
                {
                    try { window.Close(); }
                    catch (InvalidOperationException) { }
                }

                // Keep the fixture window foreground while the parent works. The
                // fixture never re-focuses the control itself after the initial
                // activation, so the parent's UIA SetFocus path stays exercised.
                if (!window.IsActive && DateTime.UtcNow - started < FixtureProtocol.FocusHoldTime)
                {
                    try { window.Activate(); }
                    catch (InvalidOperationException) { }
                }
            },
            Dispatcher.CurrentDispatcher);

        window.Loaded += (_, _) =>
        {
            window.Activate();
            field.Focus();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    window.Activate();
                    field.Focus();
                    WriteReadyFile(readyPath, window, fixtureCase);
                }));
            timer.Start();
        };

        app.Run(window);
        return 0;
    }

    private static void WriteReadyFile(string readyPath, Window window, FixtureCase fixtureCase)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var builder = new StringBuilder();
        builder.Append("pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("hwnd=").Append(handle.ToInt64().ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("title=").Append(window.Title).Append('\n');
        builder.Append("automationId=").Append(fixtureCase.AutomationId).Append('\n');
        builder.Append("controlType=").Append(fixtureCase.ControlType).Append('\n');
        builder.Append("name=").Append(fixtureCase.Name ?? string.Empty).Append('\n');
        builder.Append("helpText=").Append(fixtureCase.HelpText ?? string.Empty).Append('\n');
        builder.Append("isPassword=").Append(fixtureCase.IsPassword ? "true" : "false").Append('\n');

        var temporary = readyPath + ".tmp";
        File.WriteAllText(temporary, builder.ToString());
        File.Move(temporary, readyPath, overwrite: true);
    }
}

// ---------------------------------------------------------------------------
// Fixture control: custom WPF element + custom automation peer.
// ---------------------------------------------------------------------------

/// <summary>Synthetic editable field: ValuePattern only, no TextPattern.</summary>
internal sealed class FixtureField : FrameworkElement
{
    private readonly FixtureCase _case;
    private readonly string _statsPath;

    public FixtureField(FixtureCase fixtureCase, string statsPath)
    {
        _case = fixtureCase;
        _statsPath = statsPath;

        Focusable = true;
        FocusVisualStyle = null;
        Width = 300;
        Height = 30;
        HorizontalAlignment = HorizontalAlignment.Left;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FixtureFieldPeer(this, _case, _statsPath);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(0, 0, Math.Max(ActualWidth, 1), Math.Max(ActualHeight, 1));
        drawingContext.DrawRectangle(Brushes.White, new Pen(Brushes.SlateGray, 1), bounds);
    }
}

/// <summary>
/// Automation peer exposing fixed metadata (AutomationId, Name, HelpText,
/// password flag, control type) plus a counting IValueProvider.
/// </summary>
internal sealed class FixtureFieldPeer : FrameworkElementAutomationPeer, IValueProvider
{
    private readonly FixtureCase _case;
    private readonly string _statsPath;

    public FixtureFieldPeer(FrameworkElement owner, FixtureCase fixtureCase, string statsPath)
        : base(owner)
    {
        _case = fixtureCase;
        _statsPath = statsPath;
    }

    protected override string GetClassNameCore() => "Edit";

    protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
        string.Equals(_case.ControlType, "Document", StringComparison.Ordinal)
            ? System.Windows.Automation.Peers.AutomationControlType.Document
            : System.Windows.Automation.Peers.AutomationControlType.Edit;

    protected override string GetAutomationIdCore() => _case.AutomationId;

    protected override string GetNameCore() => _case.Name ?? string.Empty;

    protected override string GetHelpTextCore() => _case.HelpText ?? string.Empty;

    protected override bool IsPasswordCore() => _case.IsPassword;

    protected override bool IsEnabledCore() => true;

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool HasKeyboardFocusCore() => Owner is UIElement element && element.IsKeyboardFocused;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Value ? this : null;

    bool IValueProvider.IsReadOnly => false;

    string IValueProvider.Value
    {
        get
        {
            FixtureStats.Record(_statsPath, FixtureProtocol.ReadToken);
            return _case.Payload;
        }
    }

    void IValueProvider.SetValue(string value) => FixtureStats.Record(_statsPath, FixtureProtocol.WriteToken);
}

/// <summary>Append-only payload-read spy, one file per fixture run.</summary>
internal static class FixtureStats
{
    private static readonly object Gate = new();

    public static void Record(string statsPath, string token)
    {
        lock (Gate)
        {
            try { File.AppendAllText(statsPath, token + "\n"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void Reset(string statsPath, string resetPath)
    {
        lock (Gate)
        {
            try
            {
                File.WriteAllText(statsPath, string.Empty);
                File.Delete(resetPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

// ---------------------------------------------------------------------------
// Fixture protocol constants and helpers.
// ---------------------------------------------------------------------------

internal static class FixtureProtocol
{
    public const string StatsFile = "stats.txt";
    public const string ReadyFile = "ready.txt";
    public const string ResetFile = "reset.marker";
    public const string StopFile = "stop.marker";
    public const string ReadToken = "read";
    public const string WriteToken = "set";

    /// <summary>Bounded allowance for a rejected field whose text may be read
    /// once for the address-shape fallback (plus one possible snapshot re-read).</summary>
    public const int MaxBoundedReads = 2;

    public const int ChildStopMilliseconds = 3000;

    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ResetTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FocusHoldTime = TimeSpan.FromSeconds(25);

    public static string TitleFor(FixtureCase fixtureCase) => "Cotabby Credential Fixture " + fixtureCase.CaseId;
}

internal sealed record FixtureReady(
    int Pid,
    long Hwnd,
    string Title,
    string AutomationId,
    string ControlType,
    string Name,
    string HelpText,
    bool IsPassword)
{
    public static FixtureReady? TryParse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            values[line[..separator]] = line[(separator + 1)..];
        }

        if (!values.TryGetValue("pid", out var pidText) ||
            !values.TryGetValue("hwnd", out var hwndText) ||
            !values.TryGetValue("title", out var title) ||
            !values.TryGetValue("automationId", out var automationId) ||
            !values.TryGetValue("controlType", out var controlType) ||
            !values.TryGetValue("name", out var name) ||
            !values.TryGetValue("helpText", out var helpText) ||
            !values.TryGetValue("isPassword", out var isPasswordText))
        {
            return null;
        }

        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
        if (!long.TryParse(hwndText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hwnd)) return null;
        if (hwnd == 0) return null;

        return new FixtureReady(
            pid,
            hwnd,
            title,
            automationId,
            controlType,
            name,
            helpText,
            string.Equals(isPasswordText, "true", StringComparison.Ordinal));
    }
}

internal enum FixtureExpectation
{
    Allow,
    Reject,
    RejectAfterBoundedRead,
}

internal sealed record FixtureCase(
    string CaseId,
    string AutomationId,
    string ControlType,
    string? Name,
    string? HelpText,
    bool IsPassword,
    string Payload,
    FixtureExpectation Expectation,
    bool RunNegativeControl = false)
{
    public string ExpectationLabel => Expectation switch
    {
        FixtureExpectation.Allow => "allow",
        FixtureExpectation.Reject => "reject",
        _ => "bounded-reject",
    };
}

internal sealed record CaseResult(string Line, bool Passed, bool HarnessError, string? ExtraLine);

// ---------------------------------------------------------------------------
// Case table. Dummy payloads only; the fixture and the parent share this table.
// ---------------------------------------------------------------------------

internal static class FixtureCases
{
    public const string NegativeControlCaseId = "ordinary-notes";

    public static readonly FixtureCase[] All =
    {
        // Ordinary fields stay available for suggestions (spec: ordinary
        // writing, email subject, search email, document email body).
        new("ordinary-notes", "fixture-notes-field", "Edit", "Notes", null, false,
            "Dear team, the draft plan is ready for review.",
            FixtureExpectation.Allow, RunNegativeControl: true),

        new("email-subject", "email-subject", "Edit", "Email subject", null, false,
            "Draft plan for Q3 review",
            FixtureExpectation.Allow),

        new("search-email", "emailSearch", "Edit", "Search email", null, false,
            "quarterly report attachments",
            FixtureExpectation.Allow),

        // Multiline document: an email body, never a credential field.
        new("document-email-body", "fixture-email-body", "Document", "Email", null, false,
            "Hi team, attaching the roadmap draft and next steps.",
            FixtureExpectation.Allow),

        // Credential fields: rejected with zero payload reads, each isolated on
        // a single metadata channel so the guard cannot pass by accident.
        new("username-identifier", "user_email", "Edit", null, null, false,
            "dummyuser42",
            FixtureExpectation.Reject),

        new("verification-code-name", "fixture-verify-field", "Edit", "Verification code", null, false,
            "482913",
            FixtureExpectation.Reject),

        new("card-number-help-text", "fixture-card-entry", "Edit", null, "Card number", false,
            "4242 4242 4242 4242",
            FixtureExpectation.Reject),

        new("password-flag", "fixture-secret-entry", "Edit", null, null, true,
            "dummy-password-value",
            FixtureExpectation.Reject),

        // Neutral metadata, address-shaped text: may be read once for the
        // fallback, must still be rejected with no text downstream.
        new("neutral-partial-email", "fixture-neutral-value", "Edit", "Value", null, false,
            "dummy.user@exa",
            FixtureExpectation.RejectAfterBoundedRead),
    };

    public static FixtureCase? ById(string caseId) =>
        All.FirstOrDefault(c => string.Equals(c.CaseId, caseId, StringComparison.Ordinal));
}
