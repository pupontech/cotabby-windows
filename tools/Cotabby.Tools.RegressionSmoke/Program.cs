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
                    bool inserted = await Task.Run(() => inserter.InsertAsync(target, chunk, CancellationToken.None));
                    Require(inserted, "production insertion refused a fixture chunk");
                    expected += chunk;
                    await Task.Delay(180);
                    Require(box.Text == expected, $"production host readback mismatch; expected length={expected.Length}, actual length={box.Text.Length}");
                }
                Console.WriteLine("PRODUCTION_INSERTER_REAL_HOST_UNICODE_AND_CLIPBOARD_READBACK=PASS");
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
