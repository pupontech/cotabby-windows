using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Cotabby.App.Emoji;
using Cotabby.App.Hosting;
using Cotabby.App.Overlay;
using Cotabby.App.UI;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Win32.Insertion;
using Cotabby.Win32.Focus;
using Microsoft.Extensions.Logging.Abstractions;
using static Cotabby.Win32.Interop.NativeMethods;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            AppHost? host = null;
            try
            {
                var overlay = new GhostOverlayWindow();
                host = new AppHost(new DispatcherSynchronizationContext(app.Dispatcher), overlay);
                var settings = new SettingsWindow(host);
                settings.Show();
                ((ListBox)settings.FindName("NavList")).SelectedIndex = 3;
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var combo = (ComboBox)settings.FindName("AcceptKeyCombo");
                Require(combo.IsVisible && combo.ActualWidth > 0, "accept dropdown is not visible");
                var choice = combo.Items.Cast<object>().Single(item => item.ToString() == "Double-tap Tab");
                combo.IsDropDownOpen = true;
                await Task.Delay(200);
                var container = combo.ItemContainerGenerator.ContainerFromItem(choice) as ComboBoxItem;
                Require(container is { IsVisible: true } && container.ActualHeight > 0,
                    "Double-tap Tab item is not visibly rendered");
                Require(PresentationSource.FromVisual(container!) is not null, "choice has no live presentation source");
                combo.SelectedItem = choice;
                combo.IsDropDownOpen = false;
                Require(host.Settings.AcceptSuggestionKey == "DoubleTap", "selection did not update real settings");
                Require(host.Coordinator.DoubleTapFullAcceptanceEnabled, "selection did not enable coordinator");
                Require(host.SettingsStore.Load().AcceptSuggestionKey == "DoubleTap", "choice did not persist");
                Console.WriteLine("SETTINGS_REAL_WINDOW_CHOICE_VISIBLE_SELECTED_PERSISTED=PASS");
                ((ListBox)settings.FindName("NavList")).SelectedIndex = 5;
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var logCombo = (ComboBox)settings.FindName("LogVerbosityCombo");
                Require(logCombo.IsVisible, "log verbosity control not visible");
                logCombo.SelectedIndex = 2;
                Require(host.Settings.LogVerbosity == "warning" && host.SettingsStore.Load().LogVerbosity == "warning",
                    "verbosity did not persist through actual UI");
                logCombo.SelectedIndex = 0;
                var batteryToggle = (CheckBox)settings.FindName("BatteryAwareToggle");
                batteryToggle.IsChecked = true;
                Require(host.Coordinator.BatteryAwareInference && host.SettingsStore.Load().BatteryAwareInference,
                    "battery policy not wired/persisted");
                ((Slider)settings.FindName("BatteryThresholdSlider")).Value = 50;
                Require(host.Coordinator.BatteryThresholdPercent == 50 && host.SettingsStore.Load().BatteryThresholdPercent == 50,
                    "battery threshold did not reach coordinator/store");
                batteryToggle.IsChecked = false;
                ((ListBox)settings.FindName("NavList")).SelectedIndex = 4;
                var fieldToggle = (CheckBox)settings.FindName("SmarterFieldsToggle");
                fieldToggle.IsChecked = true;
                Require(host.SettingsStore.Load().SmarterFieldSuppression, "extra field protection not persisted");
                fieldToggle.IsChecked = false;
                ((ListBox)settings.FindName("NavList")).SelectedIndex = 6;
                var statsToggle = (CheckBox)settings.FindName("SuggestionStatsToggle");
                statsToggle.IsChecked = true;
                Require(host.Statistics.Enabled && host.SettingsStore.Load().EnableSuggestionStatistics,
                    "statistics toggle not wired/persisted");
                host.Statistics.Record(Cotabby.Core.Suggestions.SuggestionStatistic.Triggered);
                var clearButton = (Button?)settings.FindName("SuggestionStatsClearButton")
                    ?? throw new InvalidOperationException("statistics clear button is missing");
                clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(host.Statistics.Snapshot().Values.All(count => count == 0), "statistics UI clear failed");
                statsToggle.IsChecked = false;
                Console.WriteLine("TABTYPE_PHASE1_REAL_SETTINGS_WIRING_PERSISTENCE_AND_CLEAR=PASS");
                settings.Close();

                var box = new TextBox { AcceptsReturn = true, Text = "seed ", FontSize = 18 };
                var fixture = new Window { Content = box, Width = 700, Height = 300, Title = "Cotabby production insertion proof" };
                fixture.Show();
                fixture.Activate();
                box.Focus();
                box.CaretIndex = box.Text.Length;
                await Task.Delay(200);
                Require(box.IsKeyboardFocused, "fixture did not receive keyboard focus");
                using var tracker = new UiaFocusTracker(NullLogger<UiaFocusTracker>.Instance);
                var target = await Task.Run(() => tracker.Refresh());
                Require(target is { IsCredentialField: false, StableFieldIdentity: not null, CaretOffsetIsProven: true },
                    "fixture has no proven UIA identity/caret");
                var inserter = new SendInputTextInserter(NullLogger<SendInputTextInserter>.Instance);
                // A stale target must not inject into the new foreground field.
                var otherBox = new TextBox { Text = "other " };
                var otherWindow = new Window { Content = otherBox, Width = 400, Height = 200 };
                otherWindow.Show();
                otherWindow.Activate();
                otherBox.Focus();
                otherBox.CaretIndex = otherBox.Text.Length;
                await Task.Delay(200);
                Require(otherBox.IsKeyboardFocused, "second fixture did not receive focus");
                bool staleInserted = await Task.Run(() => inserter.InsertAsync(target!, "must not land", CancellationToken.None));
                await Task.Delay(180);
                Require(!staleInserted && otherBox.Text == "other ", "stale target inserted into a different field");
                Console.WriteLine("PRODUCTION_INSERTER_STALE_TARGET_REFUSED=PASS");
                otherWindow.Close();
                fixture.Activate();
                box.Focus();
                await Task.Delay(200);
                string expected = box.Text;
                foreach (var chunk in new[] { "hello ", "world ", "bookkeeper ", "cool ", "... ", "this is a long clipboard-pasted completion with many words. " })
                {
                    bool inserted = await Task.Run(() => inserter.InsertAsync(target!, chunk, CancellationToken.None));
                    Require(inserted, "production insertion refused a fixture chunk");
                    expected += chunk;
                    await Task.Delay(180);
                    Require(box.Text == expected, $"production host readback mismatch; expected length={expected.Length}, actual length={box.Text.Length}");
                }
                Console.WriteLine("PRODUCTION_INSERTER_REAL_HOST_UNICODE_AND_CLIPBOARD_READBACK=PASS");
                var originalClipboard = new DataObject();
                originalClipboard.SetText("original clipboard text");
                originalClipboard.SetData("Cotabby.Regression.Custom", "custom clipboard payload");
                Clipboard.SetDataObject(originalClipboard, copy: true);
                const string preservedChunk = "another long completion to exercise clipboard preservation. ";
                Require(await Task.Run(() => inserter.InsertAsync(target!, preservedChunk, CancellationToken.None)), "clipboard preservation insert refused");
                expected += preservedChunk;
                await Task.Delay(180);
                Require(box.Text == expected, "clipboard preservation case did not paste exact text");
                Require(Clipboard.GetText() == "original clipboard text"
                    && (string?)Clipboard.GetData("Cotabby.Regression.Custom") == "custom clipboard payload",
                    "production paste destroyed a non-text clipboard format");
                Console.WriteLine("PRODUCTION_INSERTER_RICH_CLIPBOARD_PRESERVED=PASS");
                Clipboard.Clear();
                const string emptyChunk = "long completion with an originally empty clipboard. ";
                Require(await Task.Run(() => inserter.InsertAsync(target!, emptyChunk, CancellationToken.None)), "empty clipboard insert refused");
                expected += emptyChunk;
                await Task.Delay(180);
                Require(box.Text == expected, "empty clipboard case did not paste exact text");
                Require(Clipboard.GetDataObject() is not { } empty || empty.GetFormats().Length == 0,
                    "originally empty clipboard was not restored");
                Console.WriteLine("PRODUCTION_INSERTER_EMPTY_CLIPBOARD_RESTORED=PASS");
                using var cancellation = new CancellationTokenSource();
                box.TextChanged += (_, _) => cancellation.Cancel();
                const string cancelledChunk = "long paste whose cancellation arrives after dispatch. ";
                Require(await Task.Run(() => inserter.InsertAsync(target!, cancelledChunk, cancellation.Token)), "dispatched paste was reported as cancelled");
                expected += cancelledChunk;
                await Task.Delay(180);
                Require(cancellation.IsCancellationRequested && box.Text == expected,
                    "cancellation case did not observe dispatched host text");
                Console.WriteLine("PRODUCTION_INSERTER_POST_DISPATCH_CANCELLATION_ACCOUNTED=PASS");
                fixture.Close();

                // Exercise the production emoji controller against dedicated
                // fixture controls only. No user-owned surface receives input.
                var writingBox = new TextBox { Text = "ordinary notes", FontSize = 18 };
                System.Windows.Automation.AutomationProperties.SetName(writingBox, "Writing notes");
                System.Windows.Automation.AutomationProperties.SetAutomationId(writingBox, "writing-notes");
                var searchBox = new TextBox { Text = "dummy query", FontSize = 18 };
                System.Windows.Automation.AutomationProperties.SetName(searchBox, "Search mail");
                System.Windows.Automation.AutomationProperties.SetAutomationId(searchBox, "ms-searchux-input");
                var credentialBox = new TextBox { Text = "user@example.invalid", FontSize = 18 };
                System.Windows.Automation.AutomationProperties.SetName(credentialBox, "Email");
                System.Windows.Automation.AutomationProperties.SetAutomationId(credentialBox, "email-address");
                var raceBox = new TextBox { Text = "race fixture", FontSize = 18 };
                System.Windows.Automation.AutomationProperties.SetName(raceBox, "Writing race fixture");
                System.Windows.Automation.AutomationProperties.SetAutomationId(raceBox, "writing-race-fixture");
                var emojiFixture = new Window
                {
                    Content = new StackPanel { Children = { writingBox, searchBox, credentialBox, raceBox } },
                    Width = 500,
                    Height = 300,
                    Title = "Cotabby emoji focus regression fixture",
                };
                emojiFixture.Show();
                emojiFixture.Activate();
                writingBox.Focus();
                writingBox.CaretIndex = writingBox.Text.Length;
                await Task.Delay(200);
                Require(writingBox.IsKeyboardFocused, "emoji writing fixture did not receive focus");
                var writingTarget = await Task.Run(() => tracker.Refresh());
                Require(writingTarget is
                {
                    IsCredentialField: false,
                    IsSecure: false,
                    ProcessId: > 0,
                    StableFieldIdentity: not null,
                    HasRuntimeIdIdentity: true,
                    CaretOffsetIsProven: true,
                },
                    "ordinary writing fixture has no strong UIA RuntimeId identity/caret");
                Require(writingTarget!.ProcessId == Environment.ProcessId,
                    "ordinary writing fixture identity was not proven for this process");

                // Identical descriptor identities are deliberately insufficient for emoji.
                tracker.SetFakeFieldForTesting(writingTarget with
                {
                    StableFieldIdentity = "ct=Edit|aid=|nm=|hwnd=123|pid=42|gen=0",
                    HasRuntimeIdIdentity = false,
                });
                var fallbackHook = new RegressionKeyboardHook();
                var fallbackInserter = new RecordingTextInserter();
                using (var fallbackPicker = new EmojiPickerController(
                    fallbackHook, tracker, fallbackInserter, new EmojiPopupWindow(), app.Dispatcher,
                    NullLogger<EmojiPickerController>.Instance))
                {
                    await Task.Run(() => fallbackHook.Raise(':'));
                    foreach (char c in "smile") await Task.Run(() => fallbackHook.Raise(c));
                    var fallbackCommit = await Task.Run(() => fallbackHook.Raise(':'));
                    await Task.Delay(150);
                    Require(!fallbackCommit.Suppress && fallbackInserter.ReplaceCallCount == 0,
                        "descriptor-only fallback target reached emoji replacement");
                }
                tracker.SetFakeFieldForTesting(null);
                Console.WriteLine("EMOJI_DESCRIPTOR_ONLY_IDENTITY_REFUSED=PASS");

                // Key-up and non-character hook notifications must not synchronously refresh UIA.
                var callbackFocus = new CountingFocusTracker(tracker);
                var callbackHook = new RegressionKeyboardHook();
                var callbackInserter = new RecordingTextInserter();
                using (var callbackPicker = new EmojiPickerController(
                    callbackHook, callbackFocus, callbackInserter, new EmojiPopupWindow(), app.Dispatcher,
                    NullLogger<EmojiPickerController>.Instance))
                {
                    await Task.Run(() => callbackHook.Raise(':'));
                    await Task.Run(() => callbackHook.Raise('s'));
                    Require(callbackFocus.RefreshCount == 0,
                        "active emoji hook callback synchronously refreshed UIA");
                    int characterReadCount = callbackFocus.RefreshCount;
                    await Task.Run(() => callbackHook.Raise(new KeyboardEvent
                    {
                        Kind = KeyKind.Character, Character = 's', HasNonShiftModifier = false, IsKeyDown = false,
                    }));
                    await Task.Run(() => callbackHook.Raise(new KeyboardEvent
                    {
                        Kind = KeyKind.Other, Character = '\0', HasNonShiftModifier = false, IsKeyDown = true,
                    }));
                    Require(callbackFocus.RefreshCount == characterReadCount,
                        "active emoji query refreshed UIA for key-up or non-character events");

                    searchBox.Focus();
                    searchBox.CaretIndex = searchBox.Text.Length;
                    await Task.Delay(150);
                    string searchBeforeTrailingColon = searchBox.Text;
                    var trailingColon = await Task.Run(() => callbackHook.Raise(':'));
                    await Task.Delay(200);
                    Require(!trailingColon.Suppress,
                        "trailing colon was suppressed after emoji query focus moved to search");
                    Require(searchBox.Text == searchBeforeTrailingColon,
                        "stale emoji commit modified the focused search fixture");
                    Require(callbackInserter.ReplaceCallCount == 0,
                        "stale emoji commit attempted fixture replacement after focus loss");
                }

                var slowFocus = new DelayedRefreshFocusTracker(writingTarget!, TimeSpan.FromMilliseconds(500));
                var slowHook = new RegressionKeyboardHook();
                var slowInserter = new RecordingTextInserter();
                using (var slowPicker = new EmojiPickerController(
                    slowHook, slowFocus, slowInserter, new EmojiPopupWindow(), app.Dispatcher,
                    NullLogger<EmojiPickerController>.Instance))
                {
                    await Task.Run(() => slowHook.Raise(':'));
                    foreach (char c in "smile") await Task.Run(() => slowHook.Raise(c));
                    var latency = Stopwatch.StartNew();
                    await Task.Run(() => slowHook.Raise(':')).WaitAsync(TimeSpan.FromMilliseconds(150));
                    latency.Stop();
                    Require(latency.Elapsed < TimeSpan.FromMilliseconds(150),
                        "commit hook callback waited for a deliberately slow UIA refresh");
                }

                var movedCaret = writingTarget!.CaretOffset;
                var movedText = writingTarget.Text.Insert(movedCaret, " :smile:");
                var shiftedField = writingTarget with
                {
                    Text = movedText,
                    CaretOffset = movedCaret + 1 + ":smile:".Length,
                };
                var shiftedFocus = new CommitRefreshFocusTracker(writingTarget, shiftedField);
                var shiftedHook = new RegressionKeyboardHook();
                var shiftedInserter = new RecordingTextInserter();
                using (var shiftedPicker = new EmojiPickerController(
                    shiftedHook, shiftedFocus, shiftedInserter, new EmojiPopupWindow(), app.Dispatcher,
                    NullLogger<EmojiPickerController>.Instance))
                {
                    await Task.Run(() => shiftedHook.Raise(':'));
                    foreach (char c in "smile") await Task.Run(() => shiftedHook.Raise(c));
                    await Task.Run(() => shiftedHook.Raise(':'));
                    await shiftedFocus.RefreshCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Delay(50);
                    Require(shiftedInserter.ReplaceCallCount == 0,
                        "emoji replacement accepted the same tail after the original caret moved");
                }

                // Credential metadata must prevent the trigger before it can open
                // the popup or reach any replacement path; fixture text stays local.
                credentialBox.Focus();
                credentialBox.CaretIndex = credentialBox.Text.Length;
                await Task.Delay(150);
                var credentialSnapshot = await Task.Run(() => tracker.Refresh());
                Require(credentialSnapshot is { IsCredentialField: true, Text.Length: 0 },
                    "credential fixture value was read into a focus snapshot");
                var credentialHook = new RegressionKeyboardHook();
                var credentialInserter = new RecordingTextInserter();
                var credentialPopup = new EmojiPopupWindow();
                string credentialTextBefore = credentialBox.Text;
                using (var credentialPicker = new EmojiPickerController(
                    credentialHook, tracker, credentialInserter, credentialPopup, app.Dispatcher,
                    NullLogger<EmojiPickerController>.Instance))
                {
                    await Task.Run(() => credentialHook.Raise(':'));
                    await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Require(!credentialPopup.IsVisible && credentialInserter.ReplaceCallCount == 0,
                        "credential field reached the emoji popup or replacement path");
                    Require(credentialBox.Text == credentialTextBefore,
                        "credential fixture value changed during emoji trigger handling");
                }

                // A query character after focus loss is also rejected before popup update.
                writingBox.Focus();
                writingBox.CaretIndex = writingBox.Text.Length;
                await Task.Delay(150);
                var queryFocus = new CountingFocusTracker(tracker);
                var queryHook = new RegressionKeyboardHook();
                var queryInserter = new RecordingTextInserter();
                var queryPopup = new EmojiPopupWindow();
                using (var queryPicker = new EmojiPickerController(
                    queryHook, queryFocus, queryInserter, queryPopup, app.Dispatcher,
                    NullLogger<EmojiPickerController>.Instance))
                {
                    await Task.Run(() => queryHook.Raise(':'));
                    await Task.Run(() => queryHook.Raise('s'));
                    await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    searchBox.Focus();
                    searchBox.CaretIndex = searchBox.Text.Length;
                    await Task.Delay(150);
                    int readsBeforeLostCharacter = queryFocus.RefreshCount;
                    var lostCharacter = await Task.Run(() => queryHook.Raise('m'));
                    await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Require(!lostCharacter.Suppress && queryFocus.RefreshCount == readsBeforeLostCharacter,
                        "active query hook callback synchronously refreshed UIA after focus loss");
                    Require(!queryPopup.IsVisible && queryInserter.ReplaceCallCount == 0,
                        "stale query character updated the emoji popup or replaced text");
                }

                // Exact same-field edit race: the caller waits behind the inserter
                // semaphore while the user changes the query tail and types "abc".
                writingBox.Focus();
                writingBox.Text = "prefix";
                writingBox.CaretIndex = writingBox.Text.Length;
                await Task.Delay(150);
                var delayedTarget = await Task.Run(() => tracker.Refresh());
                Require(delayedTarget is { HasRuntimeIdIdentity: true, IsCredentialField: false, IsSecure: false },
                    "same-field race fixture did not expose a safe strong-identity target");
                int blockedCalls = 0;
                int destructiveInputCalls = 0;
                var delayedInserter = new SendInputTextInserter(
                    NullLogger<SendInputTextInserter>.Instance,
                    () => new BoundedUiaReplacementFocusValidator(),
                    block =>
                    {
                        Interlocked.Increment(ref blockedCalls);
                        return true;
                    },
                    (count, _, _) =>
                    {
                        Interlocked.Increment(ref destructiveInputCalls);
                        return count;
                    });
                var inserterGate = (SemaphoreSlim)typeof(SendInputTextInserter)
                    .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(delayedInserter)!;
                inserterGate.Wait();
                Task<TextReplacementResult> delayedReplacement = delayedInserter.ReplaceTextAsync(
                    delayedTarget!, 7, "X", ":smile:", 13, CancellationToken.None);
                await app.Dispatcher.InvokeAsync(() =>
                {
                    writingBox.Text = "prefix:smile:abc";
                    writingBox.CaretIndex = writingBox.Text.Length;
                });
                inserterGate.Release();
                var delayedResult = await delayedReplacement.WaitAsync(TimeSpan.FromSeconds(5));
                Require(delayedResult.Status == TextReplacementStatus.Rejected
                    && writingBox.Text == "prefix:smile:abc"
                    && blockedCalls == 0 && destructiveInputCalls == 0,
                    "delayed same-field :smile:abc edit dispatched destructive input");

                // Programmatic focus can change while the blocked validator reads
                // TextPattern content. Reacquire focus and RuntimeId after that read
                // before SendInput; the host fixture remains otherwise untouched.
                writingBox.Focus();
                writingBox.Text = string.Empty;
                writingBox.CaretIndex = 0;
                raceBox.Text = "race fixture";
                await Task.Delay(150);
                var contentRaceTarget = await Task.Run(() => tracker.Refresh());
                Require(contentRaceTarget is
                {
                    Text.Length: 0,
                    CaretOffset: 0,
                    HasRuntimeIdIdentity: true,
                    CaretOffsetIsProven: true,
                }, "content-read race did not capture a valid empty pre-trigger target");
                writingBox.Text = ":smile:";
                writingBox.CaretIndex = writingBox.Text.Length;
                int contentReads = 0;
                int contentRaceDispatches = 0;
                var contentRaceBlockCalls = new List<(bool Block, bool Result, int ThreadId)>();
                bool BlockAndRecord(bool block)
                {
                    bool result = BlockInput(block);
                    contentRaceBlockCalls.Add((block, result, Environment.CurrentManagedThreadId));
                    return result;
                }
                var contentRaceInserter = new SendInputTextInserter(
                    NullLogger<SendInputTextInserter>.Instance,
                    () => new BoundedUiaReplacementFocusValidator(() =>
                    {
                        if (Interlocked.Increment(ref contentReads) == 2)
                            app.Dispatcher.Invoke(() =>
                            {
                                raceBox.Focus();
                                raceBox.CaretIndex = raceBox.Text.Length;
                            });
                    }),
                    BlockAndRecord,
                    (count, _, _) =>
                    {
                        Interlocked.Increment(ref contentRaceDispatches);
                        return count;
                    });
                const string contentRaceTail = ":smile:";
                int contentRaceExpectedCaretOffset = contentRaceTarget!.CaretOffset + contentRaceTail.Length;
                var contentRaceResult = await contentRaceInserter.ReplaceTextAsync(
                    contentRaceTarget, contentRaceTail.Length, "😄", contentRaceTail,
                    contentRaceExpectedCaretOffset, CancellationToken.None);
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(contentRaceResult.Status == TextReplacementStatus.Rejected
                    && Volatile.Read(ref contentReads) == 2
                    && raceBox.IsKeyboardFocused
                    && contentRaceDispatches == 0
                    && contentRaceBlockCalls.Select(call => call.Block).SequenceEqual(new[] { true, false })
                    && contentRaceBlockCalls.All(call => call.Result)
                    && contentRaceBlockCalls.All(call => call.ThreadId == contentRaceBlockCalls[0].ThreadId)
                    && writingBox.Text == contentRaceTail && raceBox.Text == "race fixture",
                    "focus change during the second post-block content read was not refused before SendInput");
                Console.WriteLine("PRODUCTION_EMOJI_POST_CONTENT_READ_FOCUS_REVALIDATION=PASS");

                // These are deliberately expected to fail on Windows until the
                // production post-content recheck rereads search/credential metadata.
                bool metadataMutationsRefused = true;
                foreach (string changedName in new[] { "Search mail", "Email" })
                {
                    writingBox.Focus();
                    System.Windows.Automation.AutomationProperties.SetName(writingBox, "Writing notes");
                    writingBox.Text = string.Empty;
                    writingBox.CaretIndex = 0;
                    await Task.Delay(150);
                    var metadataTarget = await Task.Run(() => tracker.Refresh());
                    Require(metadataTarget is
                    {
                        Text.Length: 0,
                        CaretOffset: 0,
                        HasRuntimeIdIdentity: true,
                        CaretOffsetIsProven: true,
                        IsCredentialField: false,
                    }, "metadata-race fixture did not capture a safe empty pre-trigger target");
                    writingBox.Text = contentRaceTail;
                    writingBox.CaretIndex = writingBox.Text.Length;
                    string metadataTextBefore = writingBox.Text;
                    int metadataContentReads = 0;
                    int metadataDispatches = 0;
                    var metadataRaceInserter = new SendInputTextInserter(
                        NullLogger<SendInputTextInserter>.Instance,
                        () => new BoundedUiaReplacementFocusValidator(() =>
                        {
                            if (Interlocked.Increment(ref metadataContentReads) == 2)
                                app.Dispatcher.Invoke(() =>
                                    System.Windows.Automation.AutomationProperties.SetName(writingBox, changedName));
                        }),
                        _ => true,
                        (count, _, _) =>
                        {
                            Interlocked.Increment(ref metadataDispatches);
                            return count;
                        });
                    int metadataExpectedCaretOffset = metadataTarget!.CaretOffset + contentRaceTail.Length;
                    var metadataResult = await metadataRaceInserter.ReplaceTextAsync(
                        metadataTarget, contentRaceTail.Length, "😄", contentRaceTail,
                        metadataExpectedCaretOffset, CancellationToken.None);
                    await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    metadataMutationsRefused &= metadataResult.Status == TextReplacementStatus.Rejected
                        && Volatile.Read(ref metadataContentReads) == 2
                        && metadataDispatches == 0
                        && writingBox.Text == metadataTextBefore
                        && writingBox.IsKeyboardFocused
                        && System.Windows.Automation.AutomationProperties.GetName(writingBox) == changedName;
                    System.Windows.Automation.AutomationProperties.SetName(writingBox, "Writing notes");
                }
                Require(metadataMutationsRefused,
                    "post-content validation did not reject Search mail/Email metadata changes after exactly two content reads");
                Console.WriteLine("PRODUCTION_EMOJI_POST_CONTENT_METADATA_REVALIDATION=PASS");

                // Deterministic focus race: ownership is checked, BlockInput is acquired,
                // then the final check sees a different fixture RuntimeId.
                writingBox.Focus();
                writingBox.Text = string.Empty;
                writingBox.CaretIndex = 0;
                await Task.Delay(150);
                var raceTriggerTarget = await Task.Run(() => tracker.Refresh());
                Require(raceTriggerTarget is
                {
                    Text.Length: 0,
                    CaretOffset: 0,
                    HasRuntimeIdIdentity: true,
                    CaretOffsetIsProven: true,
                }, "controller focus race did not capture an empty pre-trigger target");
                await Task.Delay(150);
                using var blockedBeforeFinalCheck = new ManualResetEventSlim(false);
                var atomicInserter = new SendInputTextInserter(
                    NullLogger<SendInputTextInserter>.Instance,
                    () =>
                    {
                        app.Dispatcher.Invoke(() =>
                        {
                            raceBox.Focus();
                            raceBox.CaretIndex = raceBox.Text.Length;
                        });
                        blockedBeforeFinalCheck.Set();
                    });
                var capturedInserter = new CapturingTextInserter(atomicInserter);
                var raceHook = new RegressionKeyboardHook();
                using (var racePicker = new EmojiPickerController(
                    raceHook, tracker, capturedInserter, new EmojiPopupWindow(), app.Dispatcher,
                    NullLogger<EmojiPickerController>.Instance))
                {
                    await TypeCharacterAsync(raceHook, writingBox, ':');
                    foreach (char c in "smile") await TypeCharacterAsync(raceHook, writingBox, c);
                    var trailingColon = await TypeCharacterAsync(raceHook, writingBox, ':');
                    Require(!trailingColon.Suppress, "emoji commit suppressed its trailing colon");
                    var replacementTask = await capturedInserter.ReplacementTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var replacement = await replacementTask.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(blockedBeforeFinalCheck.IsSet && raceBox.IsKeyboardFocused,
                        "race did not move keyboard focus inside the blocked transaction");
                    await Task.Delay(150);
                    Require(replacement.Status == TextReplacementStatus.Rejected
                        && writingBox.Text == ":smile:" && raceBox.Text == "race fixture",
                        "emoji replacement mutated text after focus moved to another fixture");
                }

                // Positive fixture-only path proves a successful transaction removes
                // the trigger and inserts the glyph in one SendInput batch.
                writingBox.Focus();
                writingBox.Text = string.Empty;
                writingBox.CaretIndex = 0;
                await Task.Delay(150);
                var successfulTarget = await Task.Run(() => tracker.Refresh());
                Require(successfulTarget is
                {
                    Text.Length: 0,
                    CaretOffset: 0,
                    HasRuntimeIdIdentity: true,
                    CaretOffsetIsProven: true,
                }, "positive controller fixture did not capture an empty pre-trigger target");
                await Task.Delay(150);
                var successfulInserter = new CapturingTextInserter(
                    new SendInputTextInserter(NullLogger<SendInputTextInserter>.Instance));
                var successfulHook = new RegressionKeyboardHook();
                using (var successfulPicker = new EmojiPickerController(
                    successfulHook, tracker, successfulInserter, new EmojiPopupWindow(), app.Dispatcher,
                    NullLogger<EmojiPickerController>.Instance))
                {
                    await TypeCharacterAsync(successfulHook, writingBox, ':');
                    foreach (char c in "smile") await TypeCharacterAsync(successfulHook, writingBox, c);
                    var trailingColon = await TypeCharacterAsync(successfulHook, writingBox, ':');
                    Require(!trailingColon.Suppress, "positive emoji commit suppressed its trailing colon");
                    var replacementTask = await successfulInserter.ReplacementTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var result = await replacementTask.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(result.IsComplete,
                        "focused fixture emoji replacement transaction was refused");
                    await Task.Delay(150);
                    Require(writingBox.Text == "😄",
                        "atomic fixture replacement did not leave exactly the emoji glyph");
                }
                emojiFixture.Close();
                Console.WriteLine("EMOJI_ATOMIC_REPLACEMENT_FOCUS_RACE_AND_FIXTURE_COMMIT=PASS");
                overlay.Close();
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                if (host is not null) await host.DisposeAsync();
                app.Shutdown(result);
            }
        };
        app.Run();
        return result;
    }

    private static async Task<KeyEventArgs> TypeCharacterAsync(
        RegressionKeyboardHook hook,
        TextBox textBox,
        char character)
    {
        var args = await Task.Run(() => hook.Raise(character));
        if (!args.Suppress)
        {
            await textBox.Dispatcher.InvokeAsync(() =>
            {
                int caret = textBox.CaretIndex;
                if (caret < 0 || caret > textBox.Text.Length) caret = textBox.Text.Length;
                textBox.Text = textBox.Text.Insert(caret, character.ToString());
                textBox.CaretIndex = caret + 1;
            });
        }
        return args;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RegressionKeyboardHook : IKeyboardHook
    {
        public event EventHandler<KeyEventArgs>? KeyEvent;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }

        public KeyEventArgs Raise(char character) => Raise(new KeyboardEvent
        {
            Kind = KeyKind.Character,
            Character = character,
            HasNonShiftModifier = false,
            IsKeyDown = true,
        });

        public KeyEventArgs Raise(KeyboardEvent input)
        {
            var args = new KeyEventArgs(input);
            KeyEvent?.Invoke(this, args);
            return args;
        }
    }

    private sealed class CountingFocusTracker(IFocusTracker inner) : IFocusTracker
    {
        private int _refreshCount;
        public int RefreshCount => Volatile.Read(ref _refreshCount);
        public FocusedField? Current => inner.Current;
        public event EventHandler<FocusedField?>? FocusChanged
        {
            add => inner.FocusChanged += value;
            remove => inner.FocusChanged -= value;
        }
        public FocusedField? Refresh()
        {
            Interlocked.Increment(ref _refreshCount);
            return inner.Refresh();
        }
        public void Start() => inner.Start();
        public void Stop() => inner.Stop();
        public void Dispose() => inner.Dispose();
    }

    private sealed class DelayedRefreshFocusTracker(FocusedField current, TimeSpan delay) : IFocusTracker
    {
        private int _refreshCount;
        public int RefreshCount => Volatile.Read(ref _refreshCount);
        public FocusedField? Current => current;
        public event EventHandler<FocusedField?>? FocusChanged
        {
            add { }
            remove { }
        }
        public FocusedField? Refresh()
        {
            Interlocked.Increment(ref _refreshCount);
            Thread.Sleep(delay);
            return current;
        }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class CommitRefreshFocusTracker(FocusedField initial, FocusedField refreshed) : IFocusTracker
    {
        public TaskCompletionSource<bool> RefreshCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FocusedField? Current => initial;
        public event EventHandler<FocusedField?>? FocusChanged
        {
            add { }
            remove { }
        }
        public FocusedField? Refresh()
        {
            RefreshCompleted.TrySetResult(true);
            return refreshed;
        }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class RecordingTextInserter : ITextInserter
    {
        private int _replaceCallCount;
        public int ReplaceCallCount => Volatile.Read(ref _replaceCallCount);

        public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<TextReplacementResult> ReplaceTextAsync(
            FocusedField target, int charactersToReplace, string text, string expectedTail, int expectedCaretOffset, CancellationToken ct)
        {
            Interlocked.Increment(ref _replaceCallCount);
            return Task.FromResult(TextReplacementResult.Rejected);
        }
    }

    private sealed class CapturingTextInserter(ITextInserter inner) : ITextInserter
    {
        public TaskCompletionSource<Task<TextReplacementResult>> ReplacementTask { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct) =>
            inner.InsertAsync(target, text, ct);

        public Task<TextReplacementResult> ReplaceTextAsync(
            FocusedField target, int charactersToReplace, string text, string expectedTail, int expectedCaretOffset, CancellationToken ct)
        {
            var operation = inner.ReplaceTextAsync(
                target, charactersToReplace, text, expectedTail, expectedCaretOffset, ct);
            ReplacementTask.TrySetResult(operation);
            return operation;
        }
    }
}
