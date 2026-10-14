using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;
using Cotabby.Win32.Focus;
using Cotabby.Win32.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Tools.DoubleTapSmoke;

internal static class Program
{
    internal const string Suggestion = " alpha beta gamma delta";
    internal const string BaseText = "seedx";
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(6);

    [STAThread]
    private static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("COTABBY_ALLOW_INJECTED", "1");
        if (args.Length == 3 && args[0] == "--fixture")
            return FixtureWindow.Run(args[1], args[2]);

        bool allowSkip = args.Length == 1 && args[0] == "--allow-harness-skip";
        if (args.Length != 0 && !allowSkip)
        {
            Console.Error.WriteLine("Usage: Cotabby.Tools.DoubleTapSmoke [--allow-harness-skip]");
            return 2;
        }

        var tests = new (string Id, Action Run)[]
        {
            ("DT-DOUBLE", DoubleTap),
            ("DT-REPEAT", RepeatDoesNotPromote),
            ("DT-UNOWNED", UnownedTabPasses),
            ("DT-FOCUS-CHANGE", FocusChangePassesThrough),
            ("DT-DEFAULT", DefaultTabAndBacktick),
            ("DT-DELAYED-DISPATCH", DelayedDispatcherDoesNotPromote),
            ("DT-STALE-SNAPSHOT", StaleSnapshotIsRejected),
            ("DT-SAME-PROCESS", SameProcessControlChangePassesThrough),
        };
        var results = new List<CaseResult>();
        string? setupIssue = GetDesktopSetupIssue();
        foreach (var test in tests)
        {
            if (setupIssue is not null)
            {
                results.Add(new(test.Id, CaseStatus.Infrastructure, setupIssue));
                continue;
            }
            try
            {
                test.Run();
                results.Add(new(test.Id, CaseStatus.Pass, ""));
            }
            catch (ProofException ex)
            {
                results.Add(new(test.Id, ex.IsInfrastructure ? CaseStatus.Infrastructure : CaseStatus.Fail, ex.Message));
            }
            catch (Exception ex)
            {
                results.Add(new(test.Id, CaseStatus.Fail, "unexpected exception: " + ex.GetType().Name));
            }
        }

        foreach (var result in results)
            Console.WriteLine($"{result.Id} {result.Status.ToString().ToUpperInvariant()}" +
                              (result.Detail.Length == 0 ? "" : " — " + result.Detail));

        int passed = results.Count(r => r.Status == CaseStatus.Pass);
        int infra = results.Count(r => r.Status == CaseStatus.Infrastructure);
        int failed = results.Count(r => r.Status == CaseStatus.Fail);
        if (passed == results.Count)
        {
            Console.WriteLine($"RESULT PASS {passed}/{results.Count}; fixture-directed insertion adapter with host text/caret readback; not production SendInputTextInserter or physical-repeat proof");
            return 0;
        }
        if (allowSkip && passed == 0 && failed == 0 && infra == results.Count && results.All(r => !string.IsNullOrWhiteSpace(r.Detail)))
        {
            Console.WriteLine($"RESULT SKIP 0/{results.Count}; every case has an explicit infrastructure reason above; semantic assertions were not run");
            return 4;
        }
        if (passed == 0 && failed == 0 && infra == results.Count)
        {
            Console.WriteLine($"RESULT INFRASTRUCTURE 0/{results.Count}; rerun with --allow-harness-skip only if this setup-only result is acceptable");
            return 2;
        }

        Console.WriteLine($"RESULT FAIL passed={passed}/{results.Count} semantic-failures={failed} infrastructure={infra}; partial proof is not skippable");
        return failed > 0 ? 1 : 2;
    }

    private static string? GetDesktopSetupIssue()
    {
        if (!OperatingSystem.IsWindows()) return "Windows interactive desktop is required (current OS is not Windows)";
        if (!Environment.UserInteractive) return "Windows interactive desktop is unavailable (process is not user-interactive)";
        IntPtr desktop = OpenInputDesktop(0, inherit: false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
            return "Windows input desktop could not be opened (Win32 error " + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        CloseDesktop(desktop);
        return null;
    }

    private static void DoubleTap()
    {
        using var fixture = FixtureProcess.Start("double");
        var clock = new UptimeClock(10.0);
        using var rig = Rig.Start(fixture, [" alpha", " beta gamma delta"], uptime: clock, holdFirstInsertion: true);
        PrepareSuggestion(fixture, rig);
        int cursor = rig.EventCount;
        rig.TabPress();
        rig.WaitForFirstInsertionStarted();
        using var dispatcher = rig.BlockDispatcher();
        rig.TabPress(); // Queue Tab 2 while insertion 1 is still held in the fixture adapter.
        clock.Set(10.1);
        var queuedDowns = rig.EventsSince(cursor).Where(e => e.Kind == KeyKind.Tab && e.Down).ToArray();
        Require(queuedDowns.Length == 2 && queuedDowns.All(e => !e.Autorepeat && e.Suppressed), "two distinct queued Tab-downs were not accepted through the production hook path");
        Require(queuedDowns[1].At - queuedDowns[0].At is > 0 and <= DoubleTapAcceptanceState.WindowSeconds,
            "queued distinct Tab-downs were not within the physical 300ms event gap");
        rig.ReleaseFirstInsertion();
        rig.WaitForInsertions(1); // Only after Tab 2 has already reached the hook.
        dispatcher.Dispose();
        rig.WaitForInsertions(2);
        var all = rig.EventsSince(cursor).Where(e => e.Kind == KeyKind.Tab).ToArray();
        Require(all.Length == 4 && all[0].Down && !all[1].Down && all[2].Down && !all[3].Down, "expected down/up then a distinct down/up");
        rig.AssertInsertionChunks([" alpha", " beta gamma delta"]);
        AssertFixtureState(fixture, "seedx alpha beta gamma delta", "seedx alpha beta gamma delta".Length);
    }

    private static void RepeatDoesNotPromote()
    {
        using var fixture = FixtureProcess.Start("repeat");
        using var rig = Rig.Start(fixture, [" alpha", " beta"]);
        PrepareSuggestion(fixture, rig);
        int cursor = rig.EventCount;
        rig.TabDown();
        rig.WaitForInsertions(1);
        rig.TabDown(); // Deliberately no key-up between downs.
        rig.WaitForInsertions(2);
        rig.TabUp();
        var downs = rig.EventsSince(cursor).Where(e => e.Kind == KeyKind.Tab && e.Down).ToArray();
        var all = rig.EventsSince(cursor).Where(e => e.Kind == KeyKind.Tab).ToArray();
        Require(downs.Length == 2 && !downs[0].Autorepeat && downs[1].Autorepeat, "real hook did not classify down/down as first press plus repeat");
        Require(downs.All(e => e.Suppressed), "owned repeat Tab was not suppressed by the coordinator");
        Require(all.Length == 3 && all[0].Down && all[1].Down && !all[2].Down, "expected down/down/up repeat sequence");
        rig.AssertInsertionChunks([" alpha", " beta"]);
        AssertFixtureState(fixture, "seedx alpha beta", "seedx alpha beta".Length);
    }

    private static void UnownedTabPasses()
    {
        using var fixture = FixtureProcess.Start("unowned");
        using var rig = Rig.Start(fixture, []);
        fixture.FocusEditor();
        var focusedField = rig.RefreshForFixture(fixture);
        Require(focusedField.ProcessId == fixture.Pid && focusedField.ElementHandle is AutomationElement focusedElement &&
                focusedElement.Current.AutomationId == fixture.EditorAutomationId,
            "UIA focus is not owned by the fixture editor PID/AutomationId");
        int cursor = rig.EventCount;
        rig.TabPress();
        var down = rig.EventsSince(cursor).SingleOrDefault(e => e.Kind == KeyKind.Tab && e.Down);
        Require(down is not null && !down.Suppressed, "unowned Tab was consumed by Cotabby");
        Require(WaitFor(() => fixture.FocusedAutomationId() == fixture.NextAutomationId, TimeSpan.FromSeconds(3)), "host did not receive unowned Tab navigation");
        var focus = AutomationElement.FocusedElement;
        Require(focus is not null && focus.Current.ProcessId == fixture.Pid && focus.Current.AutomationId == fixture.NextAutomationId,
            "UIA did not read back the fixture's next control after host Tab navigation");
        rig.AssertInsertionChunks([]);
        AssertFixtureState(fixture, "seed", 4);
    }

    private static void FocusChangePassesThrough()
    {
        using var first = FixtureProcess.Start("focus-a");
        using var rig = Rig.Start(first, []);
        PrepareSuggestion(first, rig);
        using var second = FixtureProcess.Start("focus-b");
        second.FocusEditor();
        Require(WaitFor(() => rig.TryRefresh()?.ProcessId == second.Pid, TimeSpan.FromSeconds(3)), "production UIA tracker did not read back the second fixture PID");
        Require(WaitFor(() => rig.Overlay.LastText is null, TimeSpan.FromSeconds(3)), "process focus change did not retire the prior suggestion");
        AssertFixtureState(second, "seed", 4);
        int cursor = rig.EventCount;
        rig.TabPress();
        var down = rig.EventsSince(cursor).SingleOrDefault(e => e.Kind == KeyKind.Tab && e.Down);
        Require(down is not null && !down.Suppressed, "focus-change Tab was suppressed instead of being delivered to the second fixture");
        Require(WaitFor(() => second.FocusedAutomationId() == second.NextAutomationId, TimeSpan.FromSeconds(3)), "pass-through Tab was not read back on the second fixture's next control");
        var focus = AutomationElement.FocusedElement;
        Require(focus is not null && focus.Current.ProcessId == second.Pid && focus.Current.AutomationId == second.NextAutomationId,
            "UIA did not prove the focus-change key reached the correct fixture control");
        rig.AssertInsertionChunks([]);
        AssertFixtureState(second, "seed", 4);
    }

    private static void DefaultTabAndBacktick()
    {
        using var fixture = FixtureProcess.Start("default");
        using var rig = Rig.Start(fixture, [" alpha", " beta gamma delta"], doubleTap: false, alternateKey: '`');
        PrepareSuggestion(fixture, rig);
        int cursor = rig.EventCount;
        rig.TabPress();
        rig.WaitForInsertions(1);
        rig.BacktickPress();
        rig.WaitForInsertions(2);
        var events = rig.EventsSince(cursor);
        var tab = events.SingleOrDefault(e => e.Kind == KeyKind.Tab && e.Down);
        var tick = events.SingleOrDefault(e => e.Kind == KeyKind.Character && e.Character == '`' && e.Down);
        Require(tab is not null && tab.Suppressed && !tab.Autorepeat, "default Tab was not a suppressed one-word accept");
        Require(tick is not null && tick.Suppressed, "default Backtick was not recognized and accepted by the real hook");
        rig.AssertInsertionChunks([" alpha", " beta gamma delta"]);
        AssertFixtureState(fixture, "seedx alpha beta gamma delta", "seedx alpha beta gamma delta".Length);
    }

    private static void DelayedDispatcherDoesNotPromote()
    {
        using var fixture = FixtureProcess.Start("delayed");
        var clock = new UptimeClock(20.0);
        using var rig = Rig.Start(fixture, [" alpha", " beta"], uptime: clock, holdFirstInsertion: true);
        PrepareSuggestion(fixture, rig);
        int cursor = rig.EventCount;
        rig.TabPress();
        rig.WaitForFirstInsertionStarted();
        using var dispatcher = rig.BlockDispatcher();
        rig.TabPress();
        var downs = rig.EventsSince(cursor).Where(e => e.Kind == KeyKind.Tab && e.Down).ToArray();
        Require(downs.Length == 2 && downs.All(e => !e.Autorepeat && e.Suppressed), "delayed case did not queue two distinct accepted Tab-downs");
        Require(downs[1].At - downs[0].At is > 0 and <= DoubleTapAcceptanceState.WindowSeconds,
            "delayed case's physical Tab event gap was not within 300ms");
        var queuedDelay = Stopwatch.StartNew();
        Thread.Sleep(350);
        queuedDelay.Stop();
        Require(queuedDelay.Elapsed.TotalSeconds > DoubleTapAcceptanceState.WindowSeconds, "dispatcher queue was not held beyond the 300ms acceptance window");
        clock.Set(20.35);
        rig.ReleaseFirstInsertion();
        rig.WaitForInsertions(1);
        dispatcher.Dispose();
        rig.WaitForInsertions(2);
        rig.AssertInsertionChunks([" alpha", " beta"]);
        AssertFixtureState(fixture, "seedx alpha beta", "seedx alpha beta".Length);
    }

    private static void StaleSnapshotIsRejected()
    {
        using var fixture = FixtureProcess.Start("stale");
        using var rig = Rig.Start(fixture, [" alpha"], holdFirstInsertion: true, expectStaleSnapshot: true);
        PrepareSuggestion(fixture, rig);
        rig.TabPress();
        rig.WaitForFirstInsertionStarted();
        FixtureReply mutation = fixture.TrySetState(fixture.EditorAutomationId, BaseText, BaseText.Length, "seedxz", 6);
        Require(mutation.Success && mutation.ProcessId == fixture.Pid && mutation.AutomationId == fixture.EditorAutomationId,
            "controlled fixture mutation did not verify its focused PID/AutomationId");
        rig.ReleaseFirstInsertion();
        rig.WaitForSettledAttempts(1);
        Require(rig.StaleSnapshotRejections == 1, "fixture adapter did not reject the intentionally stale UIA text/caret snapshot");
        rig.AssertInsertionChunks([]);
        AssertFixtureState(fixture, "seedxz", 6);
    }

    private static void SameProcessControlChangePassesThrough()
    {
        using var fixture = FixtureProcess.Start("same-process");
        using var rig = Rig.Start(fixture, []);
        PrepareSuggestion(fixture, rig);
        fixture.FocusSecondaryEditor();
        Require(fixture.HasSecondaryEditor && fixture.IsFocused(fixture.SecondaryEditorAutomationId), "same-process secondary UIA control was not focused");
        Require(WaitFor(() => rig.Overlay.LastText is null, TimeSpan.FromSeconds(3)), "same-process AutomationId change did not retire the prior suggestion");
        var sameProcessSnapshot = rig.TryRefresh();
        Require(sameProcessSnapshot?.ProcessId == fixture.Pid &&
                sameProcessSnapshot.ElementHandle is AutomationElement secondary &&
                secondary.Current.ProcessId == fixture.Pid && secondary.Current.AutomationId == fixture.SecondaryEditorAutomationId,
            "same-process UIA snapshot did not identify the secondary AutomationId");
        AssertFixtureState(fixture, BaseText, BaseText.Length);
        AssertFixtureState(fixture, "seed", 4, secondary: true);
        int cursor = rig.EventCount;
        rig.TabPress();
        var down = rig.EventsSince(cursor).SingleOrDefault(e => e.Kind == KeyKind.Tab && e.Down);
        Require(down is not null && !down.Suppressed, "same-process focus-change Tab was suppressed rather than passed through");
        Require(WaitFor(() => fixture.FocusedAutomationId() == fixture.NextAutomationId, TimeSpan.FromSeconds(3)), "same-process pass-through did not reach the next fixture control");
        rig.AssertInsertionChunks([]);
        AssertFixtureState(fixture, BaseText, BaseText.Length);
        AssertFixtureState(fixture, "seed", 4, secondary: true);
    }

    private static void PrepareSuggestion(FixtureProcess fixture, Rig rig)
    {
        fixture.FocusEditor();
        var initial = rig.RefreshForFixture(fixture);
        Require(initial.ProcessId == fixture.Pid && initial.ElementHandle is AutomationElement element &&
                element.Current.AutomationId == fixture.EditorAutomationId,
            "initial UIA focus does not identify the fixture PID/AutomationId");
        rig.SendCharacter('x');
        Require(WaitFor(() => fixture.ReadState() is { Text: BaseText, Caret: 5 }, TimeSpan.FromSeconds(3)), "SendInput trigger was not read back from the WPF TextBox and caret");
        Require(WaitFor(() => rig.Overlay.LastText == Suggestion, CaseTimeout), "production coordinator did not present the deterministic suggestion");
        var snapshot = rig.RefreshForFixture(fixture);
        Require(snapshot.Text == BaseText && snapshot.CaretOffset == BaseText.Length, "production UIA snapshot did not read back exact fixture text/caret");
    }

    private static void AssertFixtureState(FixtureProcess fixture, string expectedText, int expectedCaret, bool secondary = false)
    {
        FixtureState state = secondary ? fixture.ReadSecondaryState() : fixture.ReadState();
        Require(state.Text == expectedText && state.Caret == expectedCaret, "fixture host text/caret readback differed from the expected state");
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { if (condition()) return true; }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(15);
        }
        return false;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw ProofException.Semantic(reason);
    }

    private enum CaseStatus { Pass, Fail, Infrastructure }
    private sealed record CaseResult(string Id, CaseStatus Status, string Detail);

    internal sealed class ProofException(bool infrastructure, string message) : Exception(message)
    {
        public bool IsInfrastructure { get; } = infrastructure;
        public static ProofException Harness(string message) => new(true, message);
        public static ProofException Semantic(string message) => new(false, message);
    }

    private const uint DesktopReadObjects = 0x0001;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    private sealed class UptimeClock(double value)
    {
        private double _value = value;
        public double Read() => Volatile.Read(ref _value);
        public void Set(double value) => Volatile.Write(ref _value, value);
    }

    private sealed class Rig : IDisposable
    {
        private readonly KeyboardHook _hook;
        private readonly UiaFocusTracker _focus;
        private readonly SuggestionCoordinator _coordinator;
        private readonly SingleThreadPump _pump;
        private readonly EventLog _events = new();
        private readonly FixtureInsertionAdapter _inserter;
        private readonly RecordingOverlay _overlay = new();

        private Rig(FixtureProcess fixture, IReadOnlyList<string> expected, bool doubleTap, char? alternateKey,
            UptimeClock? uptime, bool holdFirstInsertion, bool expectStaleSnapshot)
        {
            _pump = new SingleThreadPump();
            _pump.Start();
            _hook = new KeyboardHook(NullLogger<KeyboardHook>.Instance);
            _focus = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
            _inserter = new FixtureInsertionAdapter(fixture, expected, holdFirstInsertion, expectStaleSnapshot);
            _coordinator = new SuggestionCoordinator(
                _hook, _focus, new DeterministicEngine(), _overlay, _inserter,
                new SuggestionWorkController(TimeSpan.FromMilliseconds(60)),
                NullLogger<SuggestionCoordinator>.Instance, _pump.Context,
                uptime is null ? null : uptime.Read)
            {
                DoubleTapFullAcceptanceEnabled = doubleTap,
                AcceptanceGranularity = "word",
                AlternateAcceptKey = alternateKey,
            };
            try
            {
                _coordinator.Start();
                _hook.KeyEvent += _events.OnKey;
            }
            catch
            {
                _focus.Dispose();
                _hook.Dispose();
                _pump.Dispose();
                throw;
            }
        }

        public RecordingOverlay Overlay => _overlay;
        public int EventCount => _events.Count;
        public int StaleSnapshotRejections => _inserter.StaleSnapshotRejections;
        public void AssertPumpHealthy()
        {
            if (_pump.Failure is { } failure)
                throw ProofException.Semantic("dispatcher callback threw: " + failure.GetType().Name);
        }
        public static Rig Start(FixtureProcess fixture, IReadOnlyList<string> expected, bool doubleTap = true,
            char? alternateKey = null, UptimeClock? uptime = null, bool holdFirstInsertion = false,
            bool expectStaleSnapshot = false) =>
            new(fixture, expected, doubleTap, alternateKey, uptime, holdFirstInsertion, expectStaleSnapshot);
        public FocusedField RefreshForFixture(FixtureProcess fixture)
        {
            var field = _focus.Refresh();
            if (field is null) throw ProofException.Semantic("production UIA Refresh returned null instead of a fixture field");
            if (field.ProcessId != fixture.Pid) throw ProofException.Semantic("production UIA Refresh returned a foreign process");
            if (field.ElementHandle is not AutomationElement element || element.Current.ProcessId != fixture.Pid ||
                element.Current.AutomationId != fixture.EditorAutomationId)
                throw ProofException.Semantic("production UIA Refresh did not identify the fixture editor AutomationId");
            return field;
        }
        public FocusedField? TryRefresh() => _focus.Refresh();
        public IReadOnlyList<HookRecord> EventsSince(int cursor) => _events.Since(cursor);
        public void WaitForInsertions(int count) => Require(WaitFor(() => _inserter.SuccessCount >= count, TimeSpan.FromSeconds(4)), "fixture insertion adapter did not complete the expected host mutation");
        public void WaitForSettledAttempts(int count) => Require(WaitFor(() => _inserter.SettledAttempts >= count, TimeSpan.FromSeconds(4)), "fixture insertion adapter attempt did not settle");
        public void WaitForFirstInsertionStarted() => Require(_inserter.WaitForFirstStarted(TimeSpan.FromSeconds(3)), "production coordinator did not enter the held fixture insertion adapter");
        public void ReleaseFirstInsertion() => _inserter.ReleaseFirst();
        public DispatcherBlock BlockDispatcher() => _pump.BlockDispatcher();
        public void AssertInsertionChunks(IReadOnlyList<string> expected)
        {
            if (_inserter.Violation is { } violation) throw ProofException.Semantic(violation);
            Require(_inserter.Texts.SequenceEqual(expected), "fixture-directed insertion chunks did not exactly match the expected production chunks");
            AssertPumpHealthy();
        }
        public void TabPress()
        {
            int cursor = _events.Count;
            TabDown();
            Require(WaitFor(() => _events.Since(cursor).Any(e => e.Kind == KeyKind.Tab && e.Down), TimeSpan.FromSeconds(1)), "real hook did not observe Tab-down");
            Thread.Sleep(25);
            TabUp();
            Require(WaitFor(() => _events.Since(cursor).Any(e => e.Kind == KeyKind.Tab && !e.Down), TimeSpan.FromSeconds(1)), "real hook did not observe Tab-up");
        }
        public void TabDown() => NativeInput.Key(0x09, down: true);
        public void TabUp() => NativeInput.Key(0x09, down: false);
        public void SendCharacter(char c) => NativeInput.Character(c);
        public void BacktickPress()
        {
            int cursor = _events.Count;
            NativeInput.Character('`');
            Require(WaitFor(() => _events.Since(cursor).Any(e => e.Kind == KeyKind.Character && e.Character == '`' && e.Down), TimeSpan.FromSeconds(1)), "real hook did not observe Backtick-down");
            Thread.Sleep(25);
        }
        public void Dispose()
        {
            _inserter.ReleaseFirst();
            _hook.KeyEvent -= _events.OnKey;
            try { _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally
            {
                _focus.Dispose();
                _hook.Dispose();
                _pump.Dispose();
            }
            if (_pump.Failure is { } failure)
                throw ProofException.Semantic("dispatcher callback threw: " + failure.GetType().Name);
        }
    }

    private sealed record HookRecord(KeyKind Kind, char Character, bool Down, bool Autorepeat, bool Suppressed, double At);

    private sealed class EventLog
    {
        private readonly object _gate = new();
        private readonly List<HookRecord> _items = [];
        public int Count { get { lock (_gate) return _items.Count; } }
        public void OnKey(object? sender, KeyEventArgs args)
        {
            var e = args.Event;
            if (e.Kind is not (KeyKind.Tab or KeyKind.Character)) return;
            lock (_gate) _items.Add(new(e.Kind, e.Character, e.IsKeyDown, e.IsAutorepeat, args.Suppress,
                (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency));
        }
        public IReadOnlyList<HookRecord> Since(int cursor) { lock (_gate) return _items.Skip(Math.Clamp(cursor, 0, _items.Count)).ToArray(); }
    }

    private sealed class FixtureInsertionAdapter(
        FixtureProcess fixture, IReadOnlyList<string> expected, bool holdFirstInsertion, bool expectStaleSnapshot) : ITextInserter
    {
        private readonly object _gate = new();
        private readonly List<string> _texts = [];
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _violation;
        private int _attemptCount;
        private int _successCount;
        private int _settledAttempts;
        private int _staleSnapshotRejections;

        public int AttemptCount { get { lock (_gate) return _attemptCount; } }
        public int SuccessCount { get { lock (_gate) return _successCount; } }
        public int SettledAttempts => Volatile.Read(ref _settledAttempts);
        public int StaleSnapshotRejections => Volatile.Read(ref _staleSnapshotRejections);
        public IReadOnlyList<string> Texts { get { lock (_gate) return _texts.ToArray(); } }
        public string? Violation { get { lock (_gate) return _violation; } }
        public bool WaitForFirstStarted(TimeSpan timeout) => _firstStarted.Task.Wait(timeout);
        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public async Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
        {
            int attempt;
            lock (_gate) attempt = ++_attemptCount;
            try
            {
                if (attempt == 1)
                {
                    _firstStarted.TrySetResult();
                    if (holdFirstInsertion) await _releaseFirst.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                if (attempt > expected.Count)
                {
                    SetViolation("coordinator requested more fixture mutations than expected");
                    return false;
                }
                if (text != expected[attempt - 1])
                {
                    SetViolation("insertion chunk did not exactly match the expected production chunk");
                    return false;
                }
                if (target.ProcessId != fixture.Pid ||
                    target.ElementHandle is not AutomationElement element ||
                    element.Current.ProcessId != fixture.Pid || element.Current.AutomationId != fixture.EditorAutomationId)
                {
                    SetViolation("insertion snapshot did not identify the fixture PID/AutomationId");
                    return false;
                }
                if (!fixture.IsFocused(fixture.EditorAutomationId))
                {
                    SetViolation("fixture editor was not the real focused PID/AutomationId at write time");
                    return false;
                }

                FixtureState current = fixture.ReadState();
                if (target.Text != current.Text || target.CaretOffset != current.Caret)
                {
                    if (expectStaleSnapshot && attempt == 1)
                    {
                        Interlocked.Increment(ref _staleSnapshotRejections);
                        return false;
                    }
                    SetViolation("production coordinator supplied a stale fixture text/caret snapshot");
                    return false;
                }
                if (current.Caret < 0 || current.Caret > current.Text.Length)
                {
                    SetViolation("fixture caret was outside its text at write time");
                    return false;
                }

                string updatedText = current.Text.Insert(current.Caret, text);
                int updatedCaret = current.Caret + text.Length;
                FixtureReply reply = fixture.TrySetState(fixture.EditorAutomationId,
                    current.Text, current.Caret, updatedText, updatedCaret);
                if (!reply.Success)
                {
                    if (expectStaleSnapshot && attempt == 1 && reply.Reason == "StaleState")
                        Interlocked.Increment(ref _staleSnapshotRejections);
                    else
                        SetViolation("bounded fixture mutation was refused: " + reply.Reason);
                    return false;
                }
                if (reply.ProcessId != fixture.Pid || reply.AutomationId != fixture.EditorAutomationId ||
                    reply.Text != updatedText || reply.Caret != updatedCaret)
                {
                    SetViolation("fixture mutation response did not confirm exact PID/AutomationId/text/caret");
                    return false;
                }
                FixtureState final = fixture.ReadState();
                if (final.Text != updatedText || final.Caret != updatedCaret)
                {
                    SetViolation("fixture host text/caret readback did not confirm the mutation");
                    return false;
                }
                lock (_gate)
                {
                    _texts.Add(text);
                    _successCount++;
                }
                return true;
            }
            finally
            {
                Interlocked.Increment(ref _settledAttempts);
            }
        }

        private void SetViolation(string violation)
        {
            lock (_gate) _violation ??= violation;
        }
    }

    private sealed class RecordingOverlay : IOverlayPresenter
    {
        private readonly object _gate = new();
        private string? _lastText;
        public string? LastText { get { lock (_gate) return _lastText; } }
        public void Show(string text, ScreenRect anchor) { lock (_gate) _lastText = text; }
        public void Update(string text) { lock (_gate) _lastText = text; }
        public void Hide() { lock (_gate) _lastText = null; }
    }

    private sealed class DeterministicEngine : ISuggestionEngine
    {
        public bool IsReady => true;
        public async IAsyncEnumerable<SuggestionChunk> GenerateAsync(SuggestionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(5, ct).ConfigureAwait(false);
            yield return new SuggestionChunk(Suggestion, true);
        }
    }

    private sealed class SingleThreadPump : IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private Exception? _failure;
        public SingleThreadPump()
        {
            Context = new PumpContext(_queue);
            _thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(Context);
                _ready.Set();
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    try { item.Callback(item.State); }
                    catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); }
                }
            }) { IsBackground = true, Name = "DoubleTapProof.Pump" };
        }
        public SynchronizationContext Context { get; }
        public Exception? Failure => Volatile.Read(ref _failure);
        public void Start()
        {
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("dispatcher pump did not start");
        }
        public DispatcherBlock BlockDispatcher()
        {
            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var exited = new ManualResetEventSlim();
            Context.Post(_ =>
            {
                entered.Set();
                try { release.Wait(); }
                finally { exited.Set(); }
            }, null);
            if (!entered.Wait(TimeSpan.FromSeconds(2)))
            {
                release.Set();
                entered.Dispose();
                release.Dispose();
                exited.Dispose();
                throw ProofException.Semantic("dispatcher did not enter the controlled queue barrier");
            }
            entered.Dispose();
            return new DispatcherBlock(release, exited);
        }
        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
            _queue.Dispose();
            _ready.Dispose();
        }
        private sealed class PumpContext(BlockingCollection<(SendOrPostCallback Callback, object? State)> queue) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) { try { queue.Add((d, state)); } catch (InvalidOperationException) { } }
        }
    }

    private sealed class DispatcherBlock(ManualResetEventSlim release, ManualResetEventSlim exited) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            release.Set();
            exited.Wait(TimeSpan.FromSeconds(2));
            release.Dispose();
            exited.Dispose();
        }
    }
}

internal static class NativeInput
{
    private const uint InputKeyboard = 1, KeyUp = 2;
    private const ushort VkShift = 0x10;

    public static void Key(ushort vk, bool down)
    {
        var input = Make(vk, keyUp: !down);
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw Program.ProofException.Harness("SendInput virtual-key delivery failed (Win32 error " + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }

    public static void Character(char value)
    {
        short mapping = VkKeyScanEx(value, GetKeyboardLayout(0));
        if (mapping == -1) throw Program.ProofException.Harness("keyboard layout has no virtual-key mapping for test character");
        ushort vk = (ushort)(mapping & 0xff);
        int modifiers = (mapping >> 8) & 0xff;
        if ((modifiers & ~1) != 0) throw Program.ProofException.Harness("test character requires a non-shift modifier unsupported by the production classifier");
        var inputs = new List<Input>(4);
        bool shift = (modifiers & 1) != 0;
        if (shift) inputs.Add(Make(VkShift, false));
        inputs.Add(Make(vk, false));
        inputs.Add(Make(vk, true));
        if (shift) inputs.Add(Make(VkShift, true));
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) != (uint)inputs.Count)
            throw Program.ProofException.Harness("SendInput character delivery failed (Win32 error " + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }

    private static Input Make(ushort vk, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Flags = keyUp ? KeyUp : 0 } },
    };

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int Dx, Dy;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanEx(char ch, IntPtr keyboardLayout);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
}
