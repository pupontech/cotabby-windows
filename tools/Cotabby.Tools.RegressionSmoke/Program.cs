using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Cotabby.App.Hosting;
using Cotabby.App.Overlay;
using Cotabby.App.UI;
using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using Cotabby.Win32.Insertion;
using Cotabby.Win32.Focus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
            string? originalInsertionStrategy = null;
            bool insertionStrategyCaptured = false;
            try
            {
                var overlay = new GhostOverlayWindow();
                host = new AppHost(new DispatcherSynchronizationContext(app.Dispatcher), overlay);
                originalInsertionStrategy = host.Settings.InsertionStrategy;
                insertionStrategyCaptured = true;
                host.Settings.InsertionStrategy = "uia";
                host.PersistSettings();
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
                var strategyCombo = (ComboBox)settings.FindName("InsertionStrategyCombo");
                strategyCombo.BringIntoView();
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(strategyCombo.IsVisible && strategyCombo.ActualWidth > 0,
                    "insertion strategy control is not visible in Advanced");
                var interfaceInserter = host.Services.GetRequiredService<Cotabby.Core.Insertion.ITextInserter>();
                var productionInserter = host.Services.GetRequiredService<SendInputTextInserter>();
                Require(ReferenceEquals(interfaceInserter, productionInserter),
                    "ITextInserter and concrete service did not resolve to the same instance");
                var strategyExplanation = (TextBlock)settings.FindName("InsertionStrategyExplanation");
                Require(strategyCombo.SelectedIndex == -1
                    && strategyExplanation.Text.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                    && host.SettingsStore.Load().InsertionStrategy == "uia"
                    && productionInserter.InsertionStrategy == "uia",
                    "manually stored UIA strategy was not visibly refused without a selectable UIA mode");
                strategyCombo.SelectedItem = strategyCombo.Items.Cast<ComboBoxItem>()
                    .Single(item => (string)item.Tag == "clipboard");
                Require(host.Settings.InsertionStrategy == "clipboard"
                    && host.SettingsStore.Load().InsertionStrategy == "clipboard"
                    && productionInserter.InsertionStrategy == "clipboard",
                    "Advanced insertion strategy did not update runtime and persisted settings");
                strategyCombo.SelectedItem = strategyCombo.Items.Cast<ComboBoxItem>()
                    .Single(item => (string)item.Tag == "auto");
                Require(host.Settings.InsertionStrategy == "auto"
                    && host.SettingsStore.Load().InsertionStrategy == "auto"
                    && productionInserter.InsertionStrategy == "auto",
                    "Advanced insertion strategy did not restore automatic mode");
                Console.WriteLine("INSERTION_STRATEGY_ADVANCED_UI_PERSISTENCE_LIVE_SAME_INSTANCE=PASS");
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
                ((ListBox)settings.FindName("NavList")).SelectedIndex = 4;
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                T Control<T>(string name) where T : FrameworkElement => (T)settings.FindName(name);
                void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                void Choose(string name, string tag)
                {
                    var control = Control<ComboBox>(name);
                    control.SelectedItem = control.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == tag);
                }
                var processBox = Control<TextBox>("ProfileProcessBox");
                processBox.BringIntoView();
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(processBox.IsVisible && processBox.ActualWidth > 0, "profile editor is not rendered");
                Click("ProfileNewButton");
                processBox.Text = " profile-fixture.EXE ";
                Choose("ProfileEnabledCombo", "true");
                Choose("ProfileAcceptanceCombo", "phrase");
                Choose("ProfileLengthCombo", "short");
                Choose("ProfileDisplayCombo", "popup");
                Choose("ProfileMultilineCombo", "false");
                Control<TextBox>("ProfileDebounceBox").Text = "120";
                Control<TextBox>("ProfileSingleTokensBox").Text = "12";
                Control<TextBox>("ProfileMultiTokensBox").Text = "24";
                Click("ProfileSaveButton");
                var saved = host.SettingsStore.Load().AppProfiles!.Single(p => p?.ProcessName == "profile-fixture")!;
                Require(saved.AcceptanceMode == "phrase" && saved.CompletionLengthPreset == "short"
                    && saved.DisplayMode == "popup" && saved.Enabled == true && saved.AllowMultiLine == false
                    && saved.DebounceMs == 120 && saved.MaxTokensSingleLine == 12 && saved.MaxTokensMultiLine == 24,
                    "profile controls did not persist every override");
                var mapped = Cotabby.App.Settings.AppProfileSettingsMapper.Map(host.SettingsStore.Load());
                var effective = Cotabby.Core.Settings.AppProfileResolver.Resolve(mapped.Global, mapped.Profiles, "PROFILE-FIXTURE", "Cotabby");
                Require(effective.AcceptanceMode == "phrase" && effective.MaxTokensSingleLine == 12 && effective.DebounceMs == 120,
                    "saved profile did not resolve to effective behavior");
                Click("ProfileNewButton");
                processBox.Text = "PROFILE-FIXTURE";
                Click("ProfileSaveButton");
                Require(Control<TextBlock>("ProfileValidationText").Text.Length > 0
                    && host.Settings.AppProfiles!.Count(p => p is not null) == 1, "duplicate key was accepted");
                processBox.Text = "C:\\bad\\process";
                Click("ProfileSaveButton");
                Require(Control<TextBlock>("ProfileValidationText").Text.Length > 0, "path key was accepted");
                processBox.Text = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                Click("ProfileSaveButton");
                Require(Control<TextBlock>("ProfileValidationText").Text.Length > 0, "self profile was accepted");
                var profileList = Control<ListBox>("AppProfileList");
                profileList.SelectedIndex = 0;
                Require(processBox.Text == "profile-fixture" && Control<TextBox>("ProfileDebounceBox").Text == "120",
                    "selecting profile did not load editor");
                Choose("ProfileEnabledCombo", "false");
                Click("ProfileSaveButton");
                Require(host.SettingsStore.Load().AppProfiles!.Single()!.Enabled == false, "profile edit was not saved");
                Choose("ProfileEnabledCombo", "");
                Choose("ProfileAcceptanceCombo", "");
                Choose("ProfileLengthCombo", "");
                Choose("ProfileDisplayCombo", "");
                Choose("ProfileMultilineCombo", "");
                Control<TextBox>("ProfileDebounceBox").Text = "301";
                Click("ProfileSaveButton");
                Require(Control<TextBlock>("ProfileValidationText").Text.Length > 0
                    && host.SettingsStore.Load().AppProfiles!.Single()!.DebounceMs == 120,
                    "invalid range changed persisted profile");
                Control<TextBox>("ProfileDebounceBox").Text = "";
                Control<TextBox>("ProfileSingleTokensBox").Text = "";
                Control<TextBox>("ProfileMultiTokensBox").Text = "";
                Click("ProfileSaveButton");
                saved = host.SettingsStore.Load().AppProfiles!.Single()!;
                Require(saved.Enabled is null && saved.AcceptanceMode is null && saved.CompletionLengthPreset is null
                    && saved.DisplayMode is null && saved.AllowMultiLine is null && saved.DebounceMs is null
                    && saved.MaxTokensSingleLine is null && saved.MaxTokensMultiLine is null, "inherit reset did not persist nulls");
                Click("ProfileRemoveButton");
                Require(host.SettingsStore.Load().AppProfiles!.Count == 0 && profileList.Items.Count == 0,
                    "remove did not persist or refresh profile rows");
                Console.WriteLine("TABTYPE_PROFILE_REAL_SETTINGS_ADD_EDIT_REMOVE_VALIDATION_INHERIT_PERSISTENCE=PASS");
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
                Require(target is { IsCredentialField: false, StableFieldIdentity: not null }, "fixture has no proven UIA identity");
                var inserter = host.Services.GetRequiredService<SendInputTextInserter>();
                var staleModes = new[]
                {
                    ("auto", "stale short"),
                    ("unicode", new string('u', 80)),
                    ("clipboard", "stale clipboard"),
                    ("uia", "stale unsupported"),
                };
                Clipboard.SetText("stale-target clipboard sentinel");
                uint staleSequence = GetClipboardSequenceNumber();
                var otherBox = new TextBox { Text = "other " };
                var otherWindow = new Window { Content = otherBox, Width = 400, Height = 200 };
                otherWindow.Show();
                otherWindow.Activate();
                otherBox.Focus();
                otherBox.CaretIndex = otherBox.Text.Length;
                await Task.Delay(200);
                Require(otherBox.IsKeyboardFocused, "second fixture did not receive focus");
                foreach (var (strategy, payload) in staleModes)
                {
                    inserter.InsertionStrategy = strategy;
                    bool staleInserted = await Task.Run(() => inserter.InsertAsync(target!, payload, CancellationToken.None));
                    await Task.Delay(40);
                    Require(!staleInserted && otherBox.Text == "other ", $"stale target was not refused for {strategy}");
                    Require(GetClipboardSequenceNumber() == staleSequence
                        && Clipboard.GetText() == "stale-target clipboard sentinel",
                        $"stale {strategy} attempt touched the clipboard");
                }
                Console.WriteLine("PRODUCTION_INSERTER_STALE_TARGET_REFUSED_AUTO_UNICODE_CLIPBOARD_UIA=PASS");
                otherWindow.Close();
                fixture.Activate();
                box.Focus();
                await Task.Delay(200);
                string expected = box.Text;
                string protectedText = box.Text;
                Clipboard.SetText("protected-field clipboard sentinel");
                uint protectedSequence = GetClipboardSequenceNumber();
                var protectedModes = new[]
                {
                    ("auto", "protected short"),
                    ("unicode", new string('p', 80)),
                    ("clipboard", "protected clipboard"),
                    ("uia", "protected unsupported"),
                };
                foreach (var (strategy, payload) in protectedModes)
                {
                    inserter.InsertionStrategy = strategy;
                    var credentialTarget = target! with { IsCredentialField = true };
                    var secureTarget = target! with { IsSecure = true };
                    bool credentialInserted = await Task.Run(() => inserter.InsertAsync(credentialTarget, payload, CancellationToken.None));
                    bool secureInserted = await Task.Run(() => inserter.InsertAsync(secureTarget, payload, CancellationToken.None));
                    Require(!credentialInserted && !secureInserted && box.Text == protectedText,
                        $"credential/secure target was not refused for {strategy}");
                    Require(GetClipboardSequenceNumber() == protectedSequence
                        && Clipboard.GetText() == "protected-field clipboard sentinel",
                        $"protected {strategy} attempt touched the clipboard");
                }
                Console.WriteLine("PRODUCTION_INSERTER_CREDENTIAL_AND_SECURE_REFUSAL_ALL_MODES=PASS");
                inserter.InsertionStrategy = "auto";
                foreach (var chunk in new[] { "hello ", "world ", "bookkeeper ", "cool ", "... ", "this is a long clipboard-pasted completion with many words. " })
                {
                    bool inserted = await Task.Run(() => inserter.InsertAsync(target!, chunk, CancellationToken.None));
                    Require(inserted, "production insertion refused a fixture chunk");
                    expected += chunk;
                    await Task.Delay(180);
                    Require(box.Text == expected, $"production host readback mismatch; expected length={expected.Length}, actual length={box.Text.Length}");
                }
                Console.WriteLine("PRODUCTION_INSERTER_REAL_HOST_UNICODE_AND_CLIPBOARD_READBACK=PASS");

                Clipboard.SetText("forced Unicode clipboard sentinel");
                uint unicodeSequence = GetClipboardSequenceNumber();
                string forcedUnicodeChunk = new string('u', 48) + "😀" + new string('v', 48);
                Require(forcedUnicodeChunk.Length is >= 25 and <= 128, "forced Unicode fixture length is not bounded");
                inserter.InsertionStrategy = "unicode";
                Require(await Task.Run(() => inserter.InsertAsync(target!, forcedUnicodeChunk, CancellationToken.None)),
                    "forced long Unicode insertion was refused");
                expected += forcedUnicodeChunk;
                await Task.Delay(180);
                Require(box.Text == expected, $"forced long Unicode host readback mismatch; expected length={expected.Length}, actual length={box.Text.Length}");
                Require(GetClipboardSequenceNumber() == unicodeSequence
                    && Clipboard.GetText() == "forced Unicode clipboard sentinel",
                    "forced Unicode insertion touched the clipboard");
                Console.WriteLine("PRODUCTION_INSERTER_FORCED_LONG_UNICODE_EXACT_READBACK_CLIPBOARD_UNTOUCHED=PASS");

                Clipboard.SetText("forced clipboard sentinel");
                uint clipboardSequence = GetClipboardSequenceNumber();
                inserter.InsertionStrategy = "clipboard";
                const string forcedClipboardShortChunk = "x";
                Require(await Task.Run(() => inserter.InsertAsync(target!, forcedClipboardShortChunk, CancellationToken.None)),
                    "forced short clipboard insertion was refused");
                expected += forcedClipboardShortChunk;
                await Task.Delay(180);
                Require(box.Text == expected, $"forced short clipboard host readback mismatch; expected length={expected.Length}, actual length={box.Text.Length}");
                Require(GetClipboardSequenceNumber() != clipboardSequence
                    && Clipboard.GetText() == "forced clipboard sentinel",
                    "forced short clipboard insertion did not restore the sentinel after clipboard use");
                Console.WriteLine("PRODUCTION_INSERTER_FORCED_SHORT_CLIPBOARD_EXACT_READBACK_SENTINEL_RESTORED=PASS");

                Clipboard.SetText("queued strategy snapshot sentinel");
                uint sequenceBeforeQueuedInsertion = GetClipboardSequenceNumber();
                // Hold the actual serialization gate without a clipboard-writing
                // predecessor: sampling after another paste finished could miss
                // a wrongly queued clipboard write that already completed.
                var gateField = typeof(SendInputTextInserter).GetField("_gate",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var insertionGate = gateField?.GetValue(inserter) as SemaphoreSlim
                    ?? throw new InvalidOperationException("inserter serialization gate is unavailable for fixture");
                await insertionGate.WaitAsync();
                const string queuedUnicodeChunk = "queued short unicode";
                Task<bool> queuedInsertion;
                try
                {
                    inserter.InsertionStrategy = "unicode";
                    queuedInsertion = inserter.InsertAsync(target!, queuedUnicodeChunk, CancellationToken.None);
                    Require(!queuedInsertion.IsCompleted, "second insertion did not queue behind the held gate");
                    inserter.InsertionStrategy = "clipboard";
                }
                finally
                {
                    insertionGate.Release();
                }
                Require(await queuedInsertion, "queued Unicode insertion was refused");
                expected += queuedUnicodeChunk;
                await Task.Delay(180);
                Require(box.Text == expected,
                    $"queued Unicode host readback mismatch; expected length={expected.Length}, actual length={box.Text.Length}; " +
                    $"clipboard sequence before={sequenceBeforeQueuedInsertion}, after={GetClipboardSequenceNumber()}");
                Require(GetClipboardSequenceNumber() == sequenceBeforeQueuedInsertion
                    && Clipboard.GetText() == "queued strategy snapshot sentinel",
                    "queued Unicode insertion used the later clipboard strategy");
                Console.WriteLine("PRODUCTION_INSERTER_STRATEGY_SNAPSHOT_BEFORE_GATE=PASS");
                inserter.InsertionStrategy = "auto";

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
                var replacementBox = new TextBox { Text = "replacement " };
                var replacementWindow = new Window { Content = replacementBox, Width = 400, Height = 200 };
                replacementWindow.Show();
                fixture.Activate();
                box.Focus();
                await Task.Delay(200);
                Require(box.IsKeyboardFocused, "partial-dispatch fixture did not regain focus");
                string beforePartial = box.Text;
                const string partialChunk = "12345678 remainder must not reach the replacement field";
                bool focusSwitched = false;
                TextChangedEventHandler changeFocusAfterFirstBatch = (_, _) =>
                {
                    if (!focusSwitched && box.Text == beforePartial + partialChunk[..8])
                    {
                        focusSwitched = true;
                        replacementWindow.Activate();
                        replacementBox.Focus();
                    }
                };
                box.TextChanged += changeFocusAfterFirstBatch;
                try
                {
                    inserter.InsertionStrategy = "unicode";
                    bool indeterminate = false;
                    try { await inserter.InsertAsync(target!, partialChunk, CancellationToken.None); }
                    catch (IndeterminateTextInsertionException) { indeterminate = true; }
                    await Task.Delay(180);
                    Require(indeterminate && focusSwitched && replacementBox.IsKeyboardFocused,
                        "partial Unicode dispatch was not distinguished from pre-dispatch refusal");
                    Require(box.Text == beforePartial + partialChunk[..8] && replacementBox.Text == "replacement ",
                        "paced Unicode did not stop before writing to the replacement field");
                    Console.WriteLine("PRODUCTION_INSERTER_PARTIAL_UNICODE_FOCUS_LOSS_TYPED=PASS");
                }
                finally
                {
                    box.TextChanged -= changeFocusAfterFirstBatch;
                    replacementWindow.Close();
                }
                fixture.Close();
                overlay.Close();
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                if (host is not null)
                {
                    if (insertionStrategyCaptured)
                    {
                        try
                        {
                            host.Settings.InsertionStrategy = originalInsertionStrategy!;
                            host.PersistSettings();
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Failed to restore insertion strategy setting ({ex.GetType().Name}).");
                            result = 1;
                        }
                    }
                    await host.DisposeAsync();
                }
                app.Shutdown(result);
            }
        };
        app.Run();
        return result;
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
