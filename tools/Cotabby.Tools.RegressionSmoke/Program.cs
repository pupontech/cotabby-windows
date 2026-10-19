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
using Cotabby.Core.Emoji;
using Cotabby.Core.Input;
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
                var strategyTags = strategyCombo.Items.Cast<ComboBoxItem>()
                    .Select(item => item.Tag as string).ToHashSet(StringComparer.Ordinal);
                Require(strategyCombo.SelectedIndex == -1
                    && strategyCombo.Items.Count == 2
                    && strategyTags.SetEquals(new[] { "auto", "clipboard" })
                    && strategyExplanation.Text.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                    && strategyExplanation.Text.Contains("deferred", StringComparison.OrdinalIgnoreCase)
                    && strategyExplanation.Text.Contains("uia", StringComparison.OrdinalIgnoreCase)
                    && host.SettingsStore.Load().InsertionStrategy == "uia"
                    && productionInserter.InsertionStrategy == "uia",
                    "manually stored UIA strategy was not visibly refused without a selectable UIA mode");
                strategyCombo.SelectedItem = strategyCombo.Items.Cast<ComboBoxItem>()
                    .Single(item => (string)item.Tag == "clipboard");
                Require(host.Settings.InsertionStrategy == "clipboard"
                    && host.SettingsStore.Load().InsertionStrategy == "clipboard"
                    && productionInserter.InsertionStrategy == "clipboard",
                    "Advanced insertion strategy did not update runtime and persisted settings");
                RequireInsertionStrategySurvivesReload(host, "clipboard");
                strategyCombo.SelectedItem = strategyCombo.Items.Cast<ComboBoxItem>()
                    .Single(item => (string)item.Tag == "auto");
                Require(host.Settings.InsertionStrategy == "auto"
                    && host.SettingsStore.Load().InsertionStrategy == "auto"
                    && productionInserter.InsertionStrategy == "auto",
                    "Advanced insertion strategy did not restore automatic mode");
                RequireInsertionStrategySurvivesReload(host, "auto");
                Console.WriteLine("INSERTION_STRATEGY_ADVANCED_UI_SUPPORTED_CHOICES_PERSIST_RUNTIME_RELOAD=PASS");
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
                processBox.Text = "";
                Click("ProfileSaveButton");
                Require(Control<TextBlock>("ProfileValidationText").Text == "Enter the app's executable name, for example notepad."
                    && host.SettingsStore.Load().AppProfiles!.Count(p => p is not null) == 1,
                    "blank process name did not receive actionable guidance or changed persisted profiles");
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

                host.Settings.InsertionStrategy = "UnIcOdE";
                host.PersistSettings();
                var unicodeSettings = new SettingsWindow(host);
                unicodeSettings.Show();
                ((ListBox)unicodeSettings.FindName("NavList")).SelectedIndex = 5;
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var unicodeStrategyCombo = (ComboBox)unicodeSettings.FindName("InsertionStrategyCombo");
                var unicodeStrategyExplanation = (TextBlock)unicodeSettings.FindName("InsertionStrategyExplanation");
                Require(unicodeStrategyCombo.SelectedIndex == -1
                    && unicodeStrategyCombo.Items.Count == 2
                    && host.SettingsStore.Load().InsertionStrategy == "UnIcOdE"
                    && productionInserter.InsertionStrategy == "UnIcOdE"
                    && unicodeStrategyExplanation.Text.Contains("'unicode'", StringComparison.OrdinalIgnoreCase)
                    && unicodeStrategyExplanation.Text.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                    && unicodeStrategyExplanation.Text.Contains("deferred", StringComparison.OrdinalIgnoreCase)
                    && unicodeStrategyExplanation.Text.Contains("without clipboard fallback", StringComparison.OrdinalIgnoreCase),
                    "manually stored Unicode strategy was not visibly refused without being coerced");
                var unrelatedLogCombo = (ComboBox)unicodeSettings.FindName("LogVerbosityCombo");
                unrelatedLogCombo.SelectedIndex = 2;
                Require(host.Settings.LogVerbosity == "warning"
                    && host.Settings.InsertionStrategy == "UnIcOdE"
                    && host.SettingsStore.Load().InsertionStrategy == "UnIcOdE"
                    && productionInserter.InsertionStrategy == "UnIcOdE",
                    "unrelated settings persistence coerced the stored Unicode strategy");
                unrelatedLogCombo.SelectedIndex = 0;
                unicodeSettings.Close();
                Console.WriteLine("INSERTION_STRATEGY_STORED_UNICODE_WARNING_NO_SELECTION_NO_COERCION=PASS");

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
                foreach (string unsupportedStrategy in new[] { "unicode", "UnIcOdE", "uia", "UIA" })
                {
                    host.Settings.InsertionStrategy = unsupportedStrategy;
                    host.PersistSettings();
                    var reloadedUnsupported = host.SettingsStore.Load();
                    inserter.InsertionStrategy = reloadedUnsupported.InsertionStrategy;
                    const string unsupportedClipboardSentinel = "unsupported strategy clipboard sentinel";
                    Clipboard.SetText(unsupportedClipboardSentinel);
                    uint unsupportedClipboardSequence = GetClipboardSequenceNumber();
                    string beforeUnsupported = box.Text;
                    bool unsupportedInserted = await Task.Run(() =>
                        inserter.InsertAsync(target!, "unsupported probe", CancellationToken.None));
                    await Task.Delay(40);
                    Require(!unsupportedInserted && box.Text == beforeUnsupported && box.IsKeyboardFocused
                        && reloadedUnsupported.InsertionStrategy == unsupportedStrategy
                        && inserter.InsertionStrategy == unsupportedStrategy
                        && GetClipboardSequenceNumber() == unsupportedClipboardSequence
                        && Clipboard.GetText() == unsupportedClipboardSentinel,
                        $"stored {unsupportedStrategy} strategy was not refused before touching the focused host or clipboard");
                }
                Console.WriteLine("PRODUCTION_INSERTER_STORED_UNICODE_UIA_REFUSED_PRE_DISPATCH_NO_CLIPBOARD=PASS");
                host.Settings.InsertionStrategy = "auto";
                host.PersistSettings();

                var staleModes = new[]
                {
                    ("auto", "stale short"),
                    ("clipboard", "stale clipboard"),
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
                Console.WriteLine("PRODUCTION_INSERTER_STALE_TARGET_REFUSED_AUTO_CLIPBOARD=PASS");
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
                    ("clipboard", "protected clipboard"),
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
                Console.WriteLine("PRODUCTION_INSERTER_CREDENTIAL_AND_SECURE_REFUSAL_AUTO_CLIPBOARD=PASS");
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
                Require(forcedUnicodeChunk.Length == 98, "forced Unicode fixture must remain the original 98 UTF-16-unit payload");
                string beforeForcedUnicode = box.Text;
                inserter.InsertionStrategy = "unicode";
                bool forcedUnicodeInserted = await Task.Run(() =>
                    inserter.InsertAsync(target!, forcedUnicodeChunk, CancellationToken.None));
                await Task.Delay(180);
                Require(!forcedUnicodeInserted && box.Text == beforeForcedUnicode,
                    $"unsupported forced Unicode insertion was not refused unchanged; before length={beforeForcedUnicode.Length}, after length={box.Text.Length}");
                Require(GetClipboardSequenceNumber() == unicodeSequence
                    && Clipboard.GetText() == "forced Unicode clipboard sentinel",
                    "unsupported forced Unicode insertion touched the clipboard");
                Console.WriteLine("PRODUCTION_INSERTER_FORCED_98_UTF16_UNICODE_REFUSED_HOST_AND_CLIPBOARD_UNCHANGED=PASS");

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
                const string queuedAutoChunk = "queue";
                Task<bool> queuedInsertion;
                try
                {
                    inserter.InsertionStrategy = "auto";
                    queuedInsertion = inserter.InsertAsync(target!, queuedAutoChunk, CancellationToken.None);
                    Require(!queuedInsertion.IsCompleted, "second insertion did not queue behind the held gate");
                    inserter.InsertionStrategy = "clipboard";
                }
                finally
                {
                    insertionGate.Release();
                }
                Require(await queuedInsertion, "queued Auto insertion was refused");
                expected += queuedAutoChunk;
                await Task.Delay(180);
                Require(box.Text == expected,
                    $"queued Auto host readback mismatch; expected length={expected.Length}, actual length={box.Text.Length}; " +
                    $"clipboard sequence before={sequenceBeforeQueuedInsertion}, after={GetClipboardSequenceNumber()}");
                Require(GetClipboardSequenceNumber() == sequenceBeforeQueuedInsertion
                    && Clipboard.GetText() == "queued strategy snapshot sentinel",
                    "queued Auto insertion used the later clipboard strategy");
                Console.WriteLine("PRODUCTION_INSERTER_AUTO_STRATEGY_SNAPSHOT_BEFORE_GATE=PASS");
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
                    Require(box.IsKeyboardFocused && !replacementBox.IsKeyboardFocused,
                        "different-window pre-dispatch fixture is not focused on the proven original target");
                    inserter.InsertionStrategy = "unicode";
                    bool inserted = await inserter.InsertAsync(target!, partialChunk, CancellationToken.None);
                    await Task.Delay(180);
                    Require(!inserted && !focusSwitched && box.IsKeyboardFocused && !replacementBox.IsKeyboardFocused,
                        "unsupported Unicode was not refused before the different-window focus-switch handler fired");
                    Require(box.Text == beforePartial && replacementBox.Text == "replacement ",
                        $"different-window pre-dispatch refusal changed host text; original delta={box.Text.Length - beforePartial.Length}; " +
                        $"replacement delta={replacementBox.Text.Length - "replacement ".Length}");
                    Console.WriteLine("PRODUCTION_INSERTER_UNICODE_DIFFERENT_WINDOW_SWITCH_REFUSED_BEFORE_DISPATCH=PASS");
                }
                finally
                {
                    box.TextChanged -= changeFocusAfterFirstBatch;
                    replacementWindow.Close();
                }
                // A foreground HWND guard alone cannot identify WPF sibling
                // controls, so exercise the same transition inside one window.
                fixture.Content = null;
                var siblingBox = new TextBox { Text = "sibling " };
                var panel = new StackPanel();
                panel.Children.Add(box);
                panel.Children.Add(siblingBox);
                fixture.Content = panel;
                fixture.Activate();
                box.Focus();
                box.CaretIndex = box.Text.Length;
                siblingBox.CaretIndex = siblingBox.Text.Length;
                await Task.Delay(200);
                var sameWindowTarget = await Task.Run(() => tracker.Refresh());
                Require(box.IsKeyboardFocused && sameWindowTarget is { IsCredentialField: false, StableFieldIdentity: not null },
                    "same-window partial-dispatch fixture has no proven identity");
                string beforeSibling = box.Text;
                bool siblingFocused = false;
                TextChangedEventHandler changeToSiblingAfterFirstBatch = (_, _) =>
                {
                    if (!siblingFocused && box.Text == beforeSibling + partialChunk[..8])
                    {
                        siblingFocused = true;
                        siblingBox.Focus();
                    }
                };
                box.TextChanged += changeToSiblingAfterFirstBatch;
                try
                {
                    Require(box.IsKeyboardFocused && !siblingBox.IsKeyboardFocused,
                        "same-window pre-dispatch fixture is not focused on the proven original target");
                    bool inserted = await inserter.InsertAsync(sameWindowTarget!, partialChunk, CancellationToken.None);
                    await Task.Delay(180);
                    Require(!inserted && !siblingFocused && box.IsKeyboardFocused && !siblingBox.IsKeyboardFocused,
                        "unsupported Unicode was not refused before the same-window focus-switch handler fired");
                    Require(box.Text == beforeSibling && siblingBox.Text == "sibling ",
                        $"same-window pre-dispatch refusal changed host text; original delta={box.Text.Length - beforeSibling.Length}; " +
                        $"sibling delta={siblingBox.Text.Length - "sibling ".Length}");
                    Console.WriteLine("PRODUCTION_INSERTER_UNICODE_SAME_WINDOW_SIBLING_SWITCH_REFUSED_BEFORE_DISPATCH=PASS");
                }
                finally { box.TextChanged -= changeToSiblingAfterFirstBatch; }
                var emojiPopup = new Cotabby.App.Emoji.EmojiPopupWindow();
                using var emojiHook = new FixtureKeyboardHook();
                var emojiPicker = new Cotabby.App.Emoji.EmojiPickerController(
                    emojiHook, tracker, inserter, emojiPopup, app.Dispatcher,
                    NullLogger<Cotabby.App.Emoji.EmojiPickerController>.Instance);
                bool enabledBeforeEmoji = host.Settings.Enabled;
                bool emojiEnabledBefore = host.Settings.EmojiPickerEnabled;
                try
                {
                    host.Settings.Enabled = true;
                    host.Settings.EmojiPickerEnabled = true;
                    foreach (string unsupported in new[] { "unwired", "uia", "unicode" })
                    {
                        if (unsupported != "unwired") host.RegisterEmojiPicker(emojiPicker);
                        host.Settings.InsertionStrategy = unsupported == "unwired" ? "auto" : unsupported;
                        host.ApplyRuntimeSettings();
                        const string queryText = ":smile:";
                        box.Text = queryText;
                        fixture.Activate();
                        box.Focus();
                        box.CaretIndex = box.Text.Length;
                        await Task.Delay(120);
                        Require(box.IsKeyboardFocused, "emoji admission fixture has no keyboard focus");
                        var emojiTarget = await Task.Run(() => tracker.Refresh());
                        Require(emojiTarget is { IsCredentialField: false, StableFieldIdentity: not null },
                            "emoji admission fixture has no proven noncredential identity");
                        await Task.Run(() =>
                        {
                            foreach (char c in ":smile") emojiHook.Emit(c);
                        });
                        await Task.Delay(80);
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var state = (EmojiTriggerStateMachine)typeof(Cotabby.App.Emoji.EmojiPickerController)
                            .GetField("_state", flags)!.GetValue(emojiPicker)!;
                        var matches = (List<EmojiEntry>)typeof(Cotabby.App.Emoji.EmojiPickerController)
                            .GetField("_matches", flags)!.GetValue(emojiPicker)!;
                        Require(state.IsActive && state.Query == "smile" && matches.Count > 0 && box.IsKeyboardFocused,
                            "emoji refusal proof did not arm a real nonempty query and match");
                        EmojiEntry selectedEmoji = matches[0];
                        var commitKey = await Task.Run(() => emojiHook.Emit(':'));
                        await Task.Delay(300);
                        Require(state.LastCommittedQuery == "smile" && state.QueryAtCommitLength == queryText.Length,
                            "emoji refusal proof did not reach a nonzero deletion-capable commit state");
                        Require(box.Text == queryText,
                            $"unsupported {unsupported} emoji commit erased or replaced its query; length={box.Text.Length}");
                        Require(!commitKey.Suppress, $"unsupported {unsupported} emoji commit suppressed the host key");
                        // Bypass the hook-side gate to prove admission is checked
                        // again immediately before the asynchronous destructive step.
                        var commitMethod = typeof(Cotabby.App.Emoji.EmojiPickerController).GetMethod("CommitAsync", flags)!;
                        await Task.Run(async () => await (Task)commitMethod.Invoke(emojiPicker, new object[] { selectedEmoji })!);
                        await Task.Delay(120);
                        Require(box.Text == queryText,
                            $"unsupported {unsupported} direct emoji commit bypassed deletion admission");
                    }
                    Console.WriteLine("EMOJI_UNSUPPORTED_STRATEGY_QUERY_AND_HOST_KEY_PRESERVED=PASS");
                    foreach (string supported in new[] { "auto", "clipboard" })
                    {
                        host.Settings.InsertionStrategy = supported;
                        host.ApplyRuntimeSettings();
                        const string queryText = ":smile:";
                        box.Text = queryText;
                        fixture.Activate();
                        box.Focus();
                        box.CaretIndex = box.Text.Length;
                        await Task.Delay(120);
                        Require(box.IsKeyboardFocused, "supported emoji fixture has no keyboard focus");
                        await Task.Run(() =>
                        {
                            foreach (char c in ":smile") emojiHook.Emit(c);
                        });
                        await Task.Delay(80);
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var state = (EmojiTriggerStateMachine)typeof(Cotabby.App.Emoji.EmojiPickerController)
                            .GetField("_state", flags)!.GetValue(emojiPicker)!;
                        var matches = (List<EmojiEntry>)typeof(Cotabby.App.Emoji.EmojiPickerController)
                            .GetField("_matches", flags)!.GetValue(emojiPicker)!;
                        Require(state.IsActive && state.Query == "smile" && matches.Count > 0 && box.IsKeyboardFocused,
                            "supported emoji proof did not arm a deletion-capable query");
                        EmojiEntry selectedEmoji = matches[0];
                        Require(emojiPicker.CommitAdmission?.Invoke(selectedEmoji.Glyph) == true,
                            $"live emoji admission rejected supported {supported}");
                        var commitKey = await Task.Run(() => emojiHook.Emit(':'));
                        await Task.Delay(300);
                        Require(state.LastCommittedQuery == "smile" && state.QueryAtCommitLength == queryText.Length,
                            "supported emoji proof did not reach the real commit state");
                        Require(commitKey.Suppress && box.Text == selectedEmoji.Glyph,
                            $"supported {supported} emoji commit did not replace the fixture query exactly");
                    }
                    Console.WriteLine("EMOJI_SUPPORTED_STRATEGY_LIVE_ADMISSION_AND_REAL_REPLACEMENT=PASS");
                }
                finally
                {
                    host.Settings.Enabled = enabledBeforeEmoji;
                    host.Settings.EmojiPickerEnabled = emojiEnabledBefore;
                    host.ApplyRuntimeSettings();
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

    private static void RequireInsertionStrategySurvivesReload(AppHost host, string expected)
    {
        var reloadedSettings = host.SettingsStore.Load();
        var restartedInserter = new SendInputTextInserter(NullLogger<SendInputTextInserter>.Instance)
        {
            InsertionStrategy = reloadedSettings.InsertionStrategy,
        };
        Require(reloadedSettings.InsertionStrategy == expected
            && restartedInserter.InsertionStrategy == expected,
            $"saved {expected} strategy did not configure a fresh inserter after reload");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureKeyboardHook : IKeyboardHook
    {
        public event EventHandler<Cotabby.Core.Input.KeyEventArgs>? KeyEvent;
        public void Start() { }
        public void Stop() { }
        public void Dispose() => KeyEvent = null;
        public Cotabby.Core.Input.KeyEventArgs Emit(char c)
        {
            var args = new Cotabby.Core.Input.KeyEventArgs(new KeyboardEvent
            {
                Kind = KeyKind.Character, Character = c,
                HasNonShiftModifier = false, IsKeyDown = true,
            });
            KeyEvent?.Invoke(this, args);
            return args;
        }
    }
}
