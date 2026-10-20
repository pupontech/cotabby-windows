using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Cotabby.App.Hosting;
using Cotabby.App.Overlay;
using Cotabby.App.UI;
using Cotabby.Core.Focus;
using Cotabby.Win32.Insertion;
using Cotabby.Win32.Focus;
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
                Require(target is { IsCredentialField: false, StableFieldIdentity: not null }, "fixture has no proven UIA identity");
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
                // Prove metadata policy through a real UIA provider. A positive
                // ordinary-field snapshot immediately before/after rules out a
                // generic focus/UIA outage being mistaken for search suppression.
                var searchBox = new TextBox { Text = "dummy query", FontSize = 18 };
                System.Windows.Automation.AutomationProperties.SetName(searchBox, "Search mail");
                System.Windows.Automation.AutomationProperties.SetAutomationId(searchBox, "ms-searchux-input");
                var searchWindow = new Window { Content = searchBox, Width = 400, Height = 160 };
                searchWindow.Show();
                searchWindow.Activate();
                searchBox.Focus();
                await Task.Delay(200);
                Require(searchBox.IsKeyboardFocused, "search fixture did not receive focus");
                Require(await Task.Run(() => tracker.Refresh()) is null, "search query produced a completion snapshot");
                System.Windows.Automation.AutomationProperties.SetName(searchBox, "Research notes");
                System.Windows.Automation.AutomationProperties.SetAutomationId(searchBox, "research-notes");
                Require(await Task.Run(() => tracker.Refresh()) is { IsCredentialField: false, Text: "dummy query" },
                    "ordinary writing fixture was incorrectly suppressed or UIA unavailable");
                Console.WriteLine("SEARCH_UIA_METADATA_SUPPRESSION_AND_ORDINARY_FIELD_CONTROL=PASS");
                searchWindow.Close();
                fixture.Close();
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
