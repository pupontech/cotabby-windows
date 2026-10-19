using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Cotabby.App.Hosting;
using Cotabby.App.Metrics;
using Cotabby.App.Settings;
using Cotabby.Core.Metrics;
using Cotabby.Core.Models;
using Cotabby.Core.Presentation;
using Cotabby.Inference;
using Microsoft.Win32;

namespace Cotabby.App.UI;

/// <summary>
/// Settings window matching the macOS original: a sidebar of category panes
/// (General, Engine &amp; Model, Writing, Shortcuts, Apps, Advanced, About)
/// with grouped cards on the right. Unlike the old tabbed layout there is no
/// Save button — every control commits straight to <see cref="AppHost.Settings"/>
/// and calls <see cref="AppHost.PersistSettings"/>, mirroring how the macOS
/// app applies preferences live.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;
    private readonly AppHost _host;
    private CancellationTokenSource? _downloadCts;

    // Gates persist-on-change handlers until initial population is done, so
    // seeding the controls doesn't rewrite settings.json a dozen times.
    private bool _loaded;
    private bool _modelBusy;
    private bool _profileDirty;
    private ProgressBar? _busyBar;
    private TextBlock? _busyLabel;

    private FrameworkElement[] _panes = Array.Empty<FrameworkElement>();
    private static readonly string[] PaneTitles =
    {
        "General", "Engine & Model", "Writing", "Shortcuts", "Apps", "Advanced", "Performance", "About",
    };

    private sealed record ChoiceItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    // Swatches store hex ids rather than palette names so GhostTextPalette.Resolve
    // and the overlay accept them regardless of which palette revision ships.
    private static readonly (string Id, string Label)[] ColorPresets =
    {
        ("auto", "Automatic"),
        ("#3B82F6", "Blue"),
        ("#22C55E", "Green"),
        ("#A855F7", "Purple"),
        ("#F97316", "Orange"),
        ("#EC4899", "Pink"),
        ("#EF4444", "Red"),
        ("#EAB308", "Yellow"),
        ("#14B8A6", "Teal"),
        ("#06B6D4", "Cyan"),
        ("#6366F1", "Indigo"),
    };

    private static readonly ChoiceItem[] AcceptKeyChoices =
    {
        new("Backtick", "Backtick"),
        new("Tilde", "Tilde"),
        new("Backslash", "Backslash"),
        new("DoubleTap", "Double-tap Tab"),
        new("", "None"),
    };

    private static readonly string[] SuggestedLanguages = { "German", "French", "Spanish", "Chinese" };

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        // Platform-free sampling helper: provider reads happen off the UI thread
        // behind a single-flight gate and every result is tagged with the
        // generation it was taken in, so a late reading can be recognised as
        // stale instead of being written to a readout.
        _gpuSession = new PerformanceSamplingSession(_gpuTracker.Sample, _gpuTracker.Reset);

        _panes = new FrameworkElement[]
        {
            GeneralPane, EnginePane, WritingPane, ShortcutsPane, AppsPane, AdvancedPane, PerformancePane, AboutPane,
        };

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // Sampling is bounded to "Performance pane on screen in a visible,
        // non-minimized window": minimizing, hiding or closing stops it as well.
        StateChanged += (_, _) => SyncGpuSamplingToVisibility();
        IsVisibleChanged += (_, _) => SyncGpuSamplingToVisibility();
        // The HWND doesn't exist yet in the constructor, so the title bar tint
        // needs a second pass once the window is realized.
        SourceInitialized += (_, _) => ApplyTitleBarTheme(IsSystemLight());

        PopulateGeneral();
        PopulateEngine();
        PopulateWriting();
        PopulateShortcuts();
        PopulateApps();
        PopulateAdvanced();
        PopulatePerformance();
        PopulateAbout();

        _loaded = true;
        NavList.SelectedIndex = 0;

        Closed += (_, _) =>
        {
            _downloadCts?.Cancel();
            StopGpuSampling();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            // The name field persists on focus loss; closing the window is the
            // other way focus "leaves" it.
            if (_profileDirty) _host.PersistSettings();
            _instance = null;
        };
    }

    public static void ShowOrFocus(AppHost host)
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(host);
        _instance.Show();
    }

    // ---------- Theme ----------

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ApplyTheme);

    private void ApplyTheme()
    {
        bool light = IsSystemLight();
        SetBrush("WindowBg", light ? "#F5F5F7" : "#1F1F1F");
        SetBrush("SidebarBg", light ? "#ECECEE" : "#252525");
        SetBrush("CardBg", light ? "#FFFFFF" : "#2A2A2A");
        SetBrush("CardBorder", light ? "#E3E3E3" : "#3D3D3D");
        SetBrush("TextPrimary", light ? "#1D1D1F" : "#E8E8E8");
        SetBrush("TextSecondary", light ? "#6E6E73" : "#9A9A9A");
        SetBrush("InputBg", light ? "#F2F2F4" : "#333333");
        SetBrush("KeycapBg", light ? "#EFEFF1" : "#3A3A3A");
        SetBrush("TrackOff", light ? "#D1D1D6" : "#4A4A4A");
        SetBrush("HoverBg", light ? "#0D000000" : "#12FFFFFF");
        ApplyTitleBarTheme(light);
    }

    private void SetBrush(string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Resources[key] = brush;
    }

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyTitleBarTheme(bool light)
    {
        // Without DWMWA_USE_IMMERSIVE_DARK_MODE the title bar stays white on a
        // dark window. Cosmetic only, so failures are ignored.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = light ? 0 : 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { /* pre-1809 builds lack the attribute */ }
    }

    // ---------- Navigation ----------

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_panes.Length == 0) return;
        var idx = Math.Clamp(NavList.SelectedIndex, 0, _panes.Length - 1);
        for (int i = 0; i < _panes.Length; i++)
        {
            _panes[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
        }
        Title = $"Settings — {PaneTitles[idx]}";

        // GPU sampling and the request-table refresh run only while this pane is
        // actually on screen in a visible, non-minimized window.
        SyncGpuSamplingToVisibility();
    }

    // ---------- General ----------

    private void PopulateGeneral()
    {
        var s = _host.Settings;
        EnableToggle.IsChecked = s.Enabled;
        // The registry is the source of truth for login launch; the stored flag
        // only matters when the registry write failed last time.
        LoginToggle.IsChecked = LaunchAtLogin.IsEnabled() || s.LaunchAtLogin;
        MultiLineToggle.IsChecked = s.AllowMultiLine;
        PunctToggle.IsChecked = s.AcceptPunctuationWithWord;
        EmojiToggle.IsChecked = s.EmojiPickerEnabled;
        LowPowerToggle.IsChecked = s.PauseInLowPowerMode;
        HintToggle.IsChecked = s.ShowAcceptanceHint;

        OpacitySlider.Value = Math.Clamp(s.GhostTextOpacity, 0.3, 1.0);
        OpacityLabel.Text = $"{Math.Round(OpacitySlider.Value * 100)}%";
        BuildSwatches();
    }

    private void OnGeneralToggle(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var s = _host.Settings;
        if (ReferenceEquals(sender, EnableToggle)) s.Enabled = EnableToggle.IsChecked == true;
        else if (ReferenceEquals(sender, LoginToggle)) s.LaunchAtLogin = LoginToggle.IsChecked == true;
        else if (ReferenceEquals(sender, MultiLineToggle)) s.AllowMultiLine = MultiLineToggle.IsChecked == true;
        else if (ReferenceEquals(sender, PunctToggle)) s.AcceptPunctuationWithWord = PunctToggle.IsChecked == true;
        else if (ReferenceEquals(sender, EmojiToggle)) s.EmojiPickerEnabled = EmojiToggle.IsChecked == true;
        else if (ReferenceEquals(sender, LowPowerToggle)) s.PauseInLowPowerMode = LowPowerToggle.IsChecked == true;
        else if (ReferenceEquals(sender, HintToggle)) s.ShowAcceptanceHint = HintToggle.IsChecked == true;
        _host.PersistSettings();
        if (ReferenceEquals(sender, LowPowerToggle)) _host.ApplyRuntimeSettings();
        if (ReferenceEquals(sender, HintToggle)) PushAppearance();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during XAML parse when Minimum coerces the default value.
        if (OpacityLabel is null) return;
        OpacityLabel.Text = $"{Math.Round(OpacitySlider.Value * 100)}%";
        if (!_loaded) return;
        _host.Settings.GhostTextOpacity = Math.Round(OpacitySlider.Value, 2);
        _host.PersistSettings();
        PushAppearance();
    }

    private void PushAppearance()
    {
        var s = _host.Settings;
        try
        {
            _host.Overlay.ApplyAppearance(s.GhostTextColor, s.GhostTextOpacity, s.ShowAcceptanceHint);
        }
        catch { /* overlay may not be realized yet — first show picks them up */ }
    }

    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        var current = _host.Settings.GhostTextColor ?? "auto";
        foreach (var (id, label) in ColorPresets)
        {
            bool selected = string.Equals(current, id, StringComparison.OrdinalIgnoreCase);
            var cell = new Grid
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 5, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = label,
            };
            if (selected)
            {
                cell.Children.Add(new Ellipse
                {
                    Width = 24,
                    Height = 24,
                    Stroke = (Brush)FindResource("AccentBrush"),
                    StrokeThickness = 2,
                });
            }
            cell.Children.Add(new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = SwatchFill(id),
                // Faint outline keeps light swatches visible on white cards.
                Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
                StrokeThickness = 1,
            });
            var chosen = id;
            cell.MouseLeftButtonUp += (_, _) =>
            {
                _host.Settings.GhostTextColor = chosen;
                _host.PersistSettings();
                PushAppearance();
                BuildSwatches();
            };
            SwatchPanel.Children.Add(cell);
        }
    }

    private static Brush SwatchFill(string id)
    {
        if (id == "auto")
        {
            // Half dark / half light reads as "follows the system theme".
            var split = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
            };
            split.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x3A, 0x3A), 0.0));
            split.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x3A, 0x3A), 0.5));
            split.GradientStops.Add(new GradientStop(Color.FromRgb(0xD6, 0xD6, 0xD6), 0.5));
            split.GradientStops.Add(new GradientStop(Color.FromRgb(0xD6, 0xD6, 0xD6), 1.0));
            split.Freeze();
            return split;
        }
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(id));
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    // ---------- Engine & Model ----------

    private void PopulateEngine()
    {
        ModelsFolderText.Text = ModelCatalog.DefaultLocalDirectory();
        RefreshRuntimeStatus();
        BuildModelRows();
    }

    private void RefreshRuntimeStatus(string? error = null)
    {
        if (error is not null)
        {
            RuntimeDot.Fill = (Brush)FindResource("OrangeBrush");
            RuntimeStatusText.Text = "Failed: " + error;
            return;
        }
        bool ready = _host.Runtime.IsReady;
        RuntimeDot.Fill = (Brush)FindResource(ready ? "GreenBrush" : "OrangeBrush");
        RuntimeStatusText.Text = ready
            ? $"Ready · {_host.Runtime.ActiveModel?.DisplayName}"
            : "Not loaded";
    }

    private void BuildModelRows()
    {
        ModelsPanel.Children.Clear();
        _busyBar = null;
        _busyLabel = null;
        var activeId = _host.Settings.ActiveModelId ?? ModelCatalog.All[0].Id;
        bool first = true;
        foreach (var model in ModelCatalog.All)
        {
            if (!first) ModelsPanel.Children.Add(NewSeparator());
            first = false;
            ModelsPanel.Children.Add(BuildModelRow(model, model.Id == activeId));
        }
    }

    private FrameworkElement BuildModelRow(CotabbyModel model, bool selected)
    {
        bool cached = ModelDownloader.IsCached(model);
        bool active = selected && _host.Runtime.IsReady && _host.Runtime.ActiveModel?.Id == model.Id;
        // Downloads always run against the selected model, so "busy row" and
        // "selected row" coincide while a download is in flight.
        bool busyRow = _modelBusy && selected;

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var indicator = new Grid
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 12, 0),
        };
        if (selected)
        {
            indicator.Children.Add(new Ellipse { Fill = (Brush)FindResource("AccentBrush") });
            indicator.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White });
        }
        else
        {
            var ring = new Ellipse { StrokeThickness = 1.5 };
            ring.SetResourceReference(Shape.StrokeProperty, "TextSecondary");
            indicator.Children.Add(ring);
        }
        grid.Children.Add(indicator);

        var names = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        names.Children.Add(new TextBlock
        {
            Text = model.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        });
        var subtitle = new TextBlock
        {
            Text = model.Subtitle,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        names.Children.Add(subtitle);
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);

        FrameworkElement trailing;
        if (busyRow)
        {
            var label = new TextBlock
            {
                Text = "Preparing…",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            _busyLabel = label;
            trailing = label;

            var bar = new ProgressBar
            {
                Style = (Style)FindResource("Bar"),
                Minimum = 0,
                Maximum = 1,
                Margin = new Thickness(0, 8, 0, 0),
                // Hidden until the first progress event so a cached load
                // doesn't flash an empty bar.
                Visibility = Visibility.Collapsed,
            };
            Grid.SetRow(bar, 1);
            Grid.SetColumn(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            grid.Children.Add(bar);
            _busyBar = bar;
        }
        else if (active)
        {
            var badge = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = (Brush)FindResource("GreenBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 5, 0),
            });
            badge.Children.Add(new TextBlock
            {
                Text = "Active",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("GreenBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            trailing = badge;
        }
        else if (!cached)
        {
            var button = new Button
            {
                Style = (Style)FindResource("Btn"),
                Content = "Download",
                IsEnabled = !_modelBusy,
            };
            button.Click += (_, e) => { e.Handled = true; SelectAndEnsure(model); };
            trailing = button;
        }
        else if (selected)
        {
            var button = new Button
            {
                Style = (Style)FindResource("Btn"),
                Content = "Load",
                IsEnabled = !_modelBusy,
            };
            button.Click += (_, e) => { e.Handled = true; SelectAndEnsure(model); };
            trailing = button;
        }
        else
        {
            var label = new TextBlock
            {
                Text = "Downloaded",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            trailing = label;
        }
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        var row = new Border { Background = Brushes.Transparent, Child = grid };
        if (_modelBusy)
        {
            if (!busyRow) row.Opacity = 0.45;
        }
        else
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => OnModelRowClicked(model);
        }
        return row;
    }

    private void OnModelRowClicked(CotabbyModel model)
    {
        if (_modelBusy) return;
        if (_host.Settings.ActiveModelId == model.Id) return;
        _host.Settings.ActiveModelId = model.Id;
        _host.PersistSettings();
        // Cached models swap in immediately; uncached ones wait for an explicit
        // Download press so a stray click can't start a multi-GB fetch.
        if (ModelDownloader.IsCached(model)) SelectAndEnsure(model);
        else BuildModelRows();
    }

    private async void SelectAndEnsure(CotabbyModel model)
    {
        if (_modelBusy) return;
        if (_host.Settings.ActiveModelId != model.Id)
        {
            _host.Settings.ActiveModelId = model.Id;
            _host.PersistSettings();
        }

        _modelBusy = true;
        _downloadCts = new CancellationTokenSource();
        BuildModelRows();

        var progress = new Progress<ModelDownloader.Progress>(p =>
        {
            if (_busyBar is null || _busyLabel is null) return;
            if (p.TotalBytes is long total && total > 0)
            {
                _busyBar.Visibility = Visibility.Visible;
                _busyBar.Value = (double)p.BytesDownloaded / total;
                _busyLabel.Text = $"{p.BytesDownloaded * 100 / total}%";
            }
            else
            {
                _busyLabel.Text = $"{p.BytesDownloaded / 1024 / 1024:N0} MB";
            }
        });

        string? error = null;
        try
        {
            await _host.EnsureModelReadyAsync(progress, _downloadCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            _modelBusy = false;
            _downloadCts = null;
            BuildModelRows();
            RefreshRuntimeStatus(error);
        }
    }

    private void OnOpenModelsFolder(object sender, RoutedEventArgs e)
    {
        var path = ModelCatalog.DefaultLocalDirectory();
        try { Directory.CreateDirectory(path); } catch { }
        TryShellOpen(path);
    }

    // ---------- Writing ----------

    private void PopulateWriting()
    {
        NameBox.Text = _host.Settings.UserName;
        BuildLengthRadios();
        BuildLanguageChips();
    }

    private static string LengthTitle(string id) => id switch
    {
        CompletionLengthPreset.VeryShort => "Very Short",
        CompletionLengthPreset.Short => "Short",
        CompletionLengthPreset.Medium => "Medium",
        CompletionLengthPreset.Long => "Long",
        _ => id,
    };

    private void BuildLengthRadios()
    {
        LengthPanel.Children.Clear();
        var s = _host.Settings;
        var style = (Style)FindResource("RadioRow");
        foreach (var (id, label) in CompletionLengthPreset.All)
        {
            var radio = new RadioButton
            {
                Style = style,
                GroupName = "LenPreset",
                Content = $"{LengthTitle(id)} · {label}",
                IsChecked = id == s.CompletionLengthPreset,
            };
            var chosen = id;
            radio.Checked += (_, _) =>
            {
                if (!_loaded) return;
                s.CompletionLengthPreset = chosen;
                _host.PersistSettings();
            };
            LengthPanel.Children.Add(radio);
        }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        _host.Settings.UserName = NameBox.Text?.Trim() ?? "";
        // Persist on focus loss rather than every keystroke.
        _profileDirty = true;
    }

    private void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_profileDirty) return;
        _profileDirty = false;
        _host.PersistSettings();
    }

    private void BuildLanguageChips()
    {
        LanguageChips.Children.Clear();
        foreach (var lang in _host.Settings.Languages.ToList())
        {
            var name = lang;
            LanguageChips.Children.Add(BuildChip(name, () => RemoveLanguage(name)));
        }

        LanguageSuggestions.Children.Clear();
        foreach (var lang in SuggestedLanguages)
        {
            if (_host.Settings.Languages.Any(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var name = lang;
            LanguageSuggestions.Children.Add(BuildSuggestionChip(name, () => AddLanguage(name)));
        }
    }

    private FrameworkElement BuildChip(string text, Action remove)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var close = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9,
            Margin = new Thickness(7, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Remove",
        };
        close.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; remove(); };
        content.Children.Add(close);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = content,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "KeycapBg");
        chip.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        return chip;
    }

    private FrameworkElement BuildSuggestionChip(string label, Action add)
    {
        var cell = new Grid
        {
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        // Border can't dash its stroke, so the chip outline is a Rectangle.
        var outline = new Rectangle
        {
            RadiusX = 12,
            RadiusY = 12,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = Brushes.Transparent,
        };
        outline.SetResourceReference(Shape.StrokeProperty, "TextSecondary");
        var text = new TextBlock
        {
            Text = "+ " + label,
            FontSize = 12,
            Margin = new Thickness(10, 4, 10, 4),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        cell.Children.Add(outline);
        cell.Children.Add(text);
        cell.MouseLeftButtonUp += (_, _) => add();
        return cell;
    }

    private void AddLanguage(string? raw)
    {
        var lang = raw?.Trim();
        if (string.IsNullOrEmpty(lang)) return;
        var s = _host.Settings;
        // Same cap the old comma-separated editor enforced.
        if (s.Languages.Count >= 6) return;
        if (s.Languages.Any(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase))) return;
        s.Languages.Add(lang);
        _host.PersistSettings();
        BuildLanguageChips();
    }

    private void RemoveLanguage(string lang)
    {
        var s = _host.Settings;
        s.Languages.RemoveAll(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase));
        // The prompt renderer expects at least one language.
        if (s.Languages.Count == 0) s.Languages.Add("English");
        _host.PersistSettings();
        BuildLanguageChips();
    }

    private void OnAddLanguage(object sender, RoutedEventArgs e)
    {
        AddLanguage(LanguageBox.Text);
        LanguageBox.Text = "";
    }

    private void OnLanguageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddLanguage(LanguageBox.Text);
        LanguageBox.Text = "";
    }

    // ---------- Shortcuts ----------

    private void PopulateShortcuts()
    {
        var s = _host.Settings;
        ModePhrase.IsChecked = s.AcceptanceMode == "phrase";
        ModeWord.IsChecked = s.AcceptanceMode != "phrase";

        AcceptKeyCombo.ItemsSource = AcceptKeyChoices;
        AcceptKeyCombo.SelectedItem = MatchAcceptKey(s.AcceptSuggestionKey);
        UpdateAcceptKeycap();
    }

    private static ChoiceItem MatchAcceptKey(string? raw)
    {
        // Older configs may hold the literal character rather than the name.
        var norm = (raw ?? "").Trim() switch
        {
            "`" => "Backtick",
            "~" => "Tilde",
            "\\" => "Backslash",
            var v => v,
        };
        return AcceptKeyChoices.FirstOrDefault(c => c.Id.Equals(norm, StringComparison.OrdinalIgnoreCase))
            ?? AcceptKeyChoices[0];
    }

    private void UpdateAcceptKeycap()
    {
        AcceptKeycapText.Text = (AcceptKeyCombo.SelectedItem as ChoiceItem)?.Id switch
        {
            "DoubleTap" => "Tab ×2",
            "Tilde" => "~",
            "Backslash" => "\\",
            "" => "—",
            _ => "`",
        };
    }

    private void OnAcceptKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (AcceptKeyCombo.SelectedItem is not ChoiceItem choice) return;
        _host.Settings.AcceptSuggestionKey = choice.Id;
        _host.PersistSettings();
        UpdateAcceptKeycap();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _host.Settings.AcceptanceMode = ModePhrase.IsChecked == true ? "phrase" : "word";
        _host.PersistSettings();
    }

    // ---------- Apps ----------

    private void PopulateApps()
    {
        SmarterFieldsToggle.IsChecked = _host.Settings.SmarterFieldSuppression;
        BuildBlockedAppRows();
        BuildAppProfileRows();
        LoadAppProfileEditor(null);
    }

    private AppProfile? _editingAppProfile;

    private void BuildAppProfileRows()
    {
        AppProfileList.ItemsSource = (_host.Settings.AppProfiles ?? new()).Where(p => p is not null).ToList();
    }

    private static string? ProfileChoice(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } value ? value : null;

    private static void SelectProfileChoice(ComboBox combo, string? value)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag as string, value ?? "", StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0];
    }

    private void LoadAppProfileEditor(AppProfile? profile)
    {
        _editingAppProfile = profile;
        ProfileProcessBox.Text = profile?.ProcessName ?? "";
        SelectProfileChoice(ProfileEnabledCombo, profile?.Enabled?.ToString().ToLowerInvariant());
        SelectProfileChoice(ProfileAcceptanceCombo, profile?.AcceptanceMode);
        SelectProfileChoice(ProfileLengthCombo, profile?.CompletionLengthPreset);
        SelectProfileChoice(ProfileDisplayCombo, profile?.DisplayMode);
        SelectProfileChoice(ProfileMultilineCombo, profile?.AllowMultiLine?.ToString().ToLowerInvariant());
        ProfileDebounceBox.Text = profile?.DebounceMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        ProfileSingleTokensBox.Text = profile?.MaxTokensSingleLine?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        ProfileMultiTokensBox.Text = profile?.MaxTokensMultiLine?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        ProfileValidationText.Text = "";
        ProfileRemoveButton.IsEnabled = profile is not null;
    }

    private void OnAppProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        LoadAppProfileEditor(AppProfileList.SelectedItem as AppProfile);
    }

    private void OnNewAppProfile(object sender, RoutedEventArgs e)
    {
        AppProfileList.SelectedItem = null;
        LoadAppProfileEditor(null);
    }

    private void OnRemoveAppProfile(object sender, RoutedEventArgs e)
    {
        if (_editingAppProfile is null) return;
        _host.Settings.AppProfiles?.Remove(_editingAppProfile);
        _host.PersistSettings();
        BuildAppProfileRows();
        LoadAppProfileEditor(null);
    }

    private void OnSaveAppProfile(object sender, RoutedEventArgs e)
    {
        var name = ProfileProcessBox.Text.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int? Number(TextBox box, int min, int max)
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return null;
            if (!int.TryParse(box.Text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                throw new ArgumentException($"Enter a whole number from {min} to {max}, or leave blank to inherit.");
            return value;
        }
        try
        {
            if (AppProfile.GetProcessNameValidationError(name, Process.GetCurrentProcess().ProcessName) is { } nameError)
                throw new ArgumentException(nameError);
            var profile = new AppProfile
            {
                ProcessName = name,
                Enabled = ProfileChoice(ProfileEnabledCombo) is { } enabled ? bool.Parse(enabled) : null,
                AcceptanceMode = ProfileChoice(ProfileAcceptanceCombo),
                CompletionLengthPreset = ProfileChoice(ProfileLengthCombo),
                DisplayMode = ProfileChoice(ProfileDisplayCombo),
                AllowMultiLine = ProfileChoice(ProfileMultilineCombo) is { } multiline ? bool.Parse(multiline) : null,
                DebounceMs = Number(ProfileDebounceBox, 20, 300),
                MaxTokensSingleLine = Number(ProfileSingleTokensBox, 4, 96),
                MaxTokensMultiLine = Number(ProfileMultiTokensBox, 4, 128),
            };
            if (!Cotabby.Core.Settings.AppProfileResolver.IsValidProfile(new Cotabby.Core.Settings.AppProfileOverride
                {
                    ProcessName = name, Enabled = profile.Enabled, AcceptanceMode = profile.AcceptanceMode,
                    CompletionLengthPreset = profile.CompletionLengthPreset, DisplayMode = profile.DisplayMode,
                    AllowMultiLine = profile.AllowMultiLine, DebounceMs = profile.DebounceMs,
                    MaxTokensSingleLine = profile.MaxTokensSingleLine, MaxTokensMultiLine = profile.MaxTokensMultiLine,
                }))
                throw new ArgumentException("Choose valid profile options; use Inherit to keep the global setting.");
            var profiles = _host.Settings.AppProfiles ??= new();
            if (profiles.Any(p => p is not null && !ReferenceEquals(p, _editingAppProfile)
                    && string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A profile for this process already exists. Select it to edit instead.");
            int index = _editingAppProfile is null ? -1 : profiles.IndexOf(_editingAppProfile);
            if (index >= 0) profiles[index] = profile;
            else profiles.Add(profile);
            _host.PersistSettings();
            BuildAppProfileRows();
            AppProfileList.SelectedItem = profile;
            LoadAppProfileEditor(profile);
        }
        catch (ArgumentException error)
        {
            ProfileValidationText.Text = error.Message;
        }
    }

    private void OnSmarterFieldsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _host.Settings.SmarterFieldSuppression = SmarterFieldsToggle.IsChecked == true;
        _host.PersistSettings();
    }

    private void BuildBlockedAppRows()
    {
        BlockedAppsPanel.Children.Clear();
        var apps = _host.Settings.BlockedApps;
        if (apps.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No apps disabled yet.",
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 6),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            BlockedAppsPanel.Children.Add(empty);
            return;
        }

        bool first = true;
        foreach (var app in apps.ToList())
        {
            if (!first) BlockedAppsPanel.Children.Add(NewSeparator());
            first = false;

            var row = new Grid { MinHeight = 38 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = app,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var remove = new Button
            {
                Style = (Style)FindResource("Btn"),
                Content = "Remove",
                FontSize = 11,
                Padding = new Thickness(9, 3, 9, 3),
            };
            Grid.SetColumn(remove, 1);
            var name = app;
            remove.Click += (_, _) =>
            {
                _host.Settings.BlockedApps.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                _host.PersistSettings();
                BuildBlockedAppRows();
            };
            row.Children.Add(remove);
            BlockedAppsPanel.Children.Add(row);
        }
    }

    private void OnAddBlockedApp(object sender, RoutedEventArgs e) => AddBlockedApp();

    private void OnNewAppKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddBlockedApp();
    }

    private void AddBlockedApp()
    {
        var raw = NewAppBox.Text?.Trim() ?? "";
        if (raw.Length == 0) return;
        var name = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
        NewAppBox.Text = "";
        if (_host.Settings.BlockedApps.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        _host.Settings.BlockedApps.Add(name);
        _host.PersistSettings();
        BuildBlockedAppRows();
    }

    // ---------- Advanced ----------

    private void PopulateAdvanced()
    {
        BatteryAwareToggle.IsChecked = _host.Settings.BatteryAwareInference;
        BatteryThresholdSlider.Value = Math.Clamp(_host.Settings.BatteryThresholdPercent, 5, 95);
        RefreshBatteryStatus();
        DebounceSlider.Value = Math.Clamp(_host.Settings.DebounceMs, 20, 300);
        DebounceLabel.Text = $"{(int)DebounceSlider.Value} ms";
        SettingsPathText.Text = _host.SettingsStore.Path;
        ModelsPathText.Text = ModelCatalog.DefaultLocalDirectory();
        LogPathText.Text = @"C:\tmp\cotabby-live.log";
        LogVerbosityCombo.SelectedIndex = _host.Settings.LogVerbosity?.ToLowerInvariant() switch
        {
            "debug" => 1, "warning" => 2, "error" => 3, _ => 0,
        };

        string? savedStrategy = _host.Settings.InsertionStrategy?.ToLowerInvariant();
        if (savedStrategy is "unicode" or "uia")
        {
            InsertionStrategyCombo.SelectedIndex = -1;
            InsertionStrategyExplanation.Text = $"The saved '{savedStrategy}' strategy is unsupported and deferred. Non-empty insertions are refused without clipboard fallback. Choose Auto or Clipboard to resume.";
        }
        else
        {
            string strategy = savedStrategy == "clipboard" ? "clipboard" : "auto";
            InsertionStrategyCombo.SelectedItem = InsertionStrategyCombo.Items
                .Cast<ComboBoxItem>()
                .Single(item => string.Equals(item.Tag as string, strategy, StringComparison.Ordinal));
            InsertionStrategyExplanation.Text = InsertionStrategyExplanationFor(strategy);
        }
    }

    private static string InsertionStrategyExplanationFor(string strategy) => strategy switch
    {
        "clipboard" => "Temporarily puts text on the clipboard to paste, then restores saved formats only while Cotabby still owns it. Clipboard managers may observe the temporary text.",
        _ => "Auto preserves the existing heuristic: up to 24 UTF-16 code units with no LF use one Unicode SendInput batch; all other text uses clipboard paste. Short Unicode batches may still be dropped by some hosts. A carriage return alone does not change the rule.",
    };

    private void OnInsertionStrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || InsertionStrategyCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not string strategy) return;
        if (string.Equals(_host.Settings.InsertionStrategy, strategy, StringComparison.Ordinal)) return;
        _host.Settings.InsertionStrategy = strategy;
        InsertionStrategyExplanation.Text = InsertionStrategyExplanationFor(strategy);
        _host.PersistSettings();
    }

    private void OnBatterySettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _host.Settings.BatteryAwareInference = BatteryAwareToggle.IsChecked == true;
        _host.PersistSettings();
        RefreshBatteryStatus();
    }

    private void OnBatteryThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        _host.Settings.BatteryThresholdPercent = (int)BatteryThresholdSlider.Value;
        _host.PersistSettings();
        RefreshBatteryStatus();
    }

    private void OnRefreshBatteryStatus(object sender, RoutedEventArgs e) => RefreshBatteryStatus();

    private void RefreshBatteryStatus()
    {
        if (BatteryPostureText is null) return;
        BatteryPostureText.Text = $"Current posture: {_host.Coordinator.BatteryPosture}. Pause below {_host.Settings.BatteryThresholdPercent}% (refresh to update).";
    }

    private void OnLogVerbosityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || LogVerbosityCombo.SelectedItem is not ComboBoxItem item) return;
        _host.Settings.LogVerbosity = item.Tag as string ?? "information";
        _host.PersistSettings();
    }

    private void OnDebounceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during XAML parse when Minimum coerces the default value.
        if (DebounceLabel is null) return;
        DebounceLabel.Text = $"{(int)DebounceSlider.Value} ms";
        if (!_loaded) return;
        _host.Settings.DebounceMs = (int)Math.Round(DebounceSlider.Value);
        _host.PersistSettings();
    }

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e) =>
        TryShellOpen(_host.SettingsStore.Path);

    private void OnOpenLog(object sender, RoutedEventArgs e) =>
        TryShellOpen(@"C:\tmp\cotabby-live.log");

    private static void TryShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { /* user can copy the path from the label */ }
    }

    // ---------- Performance ----------

    private readonly GpuUsageTracker _gpuTracker = new(new WindowsGpuUsageProvider());
    private readonly PerformanceSamplingSession _gpuSession;
    private System.Windows.Threading.DispatcherTimer? _gpuTimer;
    private bool _gpuSampling;
    private int _gpuEpoch;
    private PerformanceCounterAvailability _gpuAvailability = PerformanceCounterAvailability.Unknown;

    private void PopulatePerformance()
    {
        SuggestionStatsToggle.IsChecked = _host.Settings.EnableSuggestionStatistics;
        RefreshSuggestionStats();
        PerfTrackToggle.IsChecked = _host.Settings.EnablePerformanceTracking;
        if (_host.PerformanceMetrics is { } metrics) metrics.TrackingEnabled = PerfTrackToggle.IsChecked == true;
        // No counter-category probe here: enumerating the "GPU Engine" set is a
        // synchronous registry walk, and this runs on the UI thread during
        // construction, before the pane is on screen. Availability is learned
        // from the samples themselves (see ApplyGpuSample), so the hint starts
        // as unknown and stays hidden until real data says otherwise.
        _gpuAvailability = PerformanceCounterAvailability.Unknown;
        ApplyGpuAvailabilityHint();
        RefreshPerformanceLog();
        // No GPU sample here either: the pane is not on screen yet, and taking
        // one now would consume the tracker's baseline outside the sampling
        // window.
    }

    private void OnSuggestionStatsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _host.Settings.EnableSuggestionStatistics = SuggestionStatsToggle.IsChecked == true;
        _host.PersistSettings();
        RefreshSuggestionStats();
    }

    private void OnRefreshSuggestionStats(object sender, RoutedEventArgs e) => RefreshSuggestionStats();
    private void OnClearSuggestionStats(object sender, RoutedEventArgs e)
    {
        _host.Statistics.Clear();
        RefreshSuggestionStats();
    }

    private void RefreshSuggestionStats() => SuggestionStatsText.Text =
        (_host.Statistics.Enabled ? "Recording — " : "Off — ") +
        string.Join(" · ", _host.Statistics.Snapshot().Select(pair => $"{pair.Key}: {pair.Value}"));

    private void OnPerfTrackChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        bool enabled = PerfTrackToggle.IsChecked == true;
        _host.Settings.EnablePerformanceTracking = enabled;
        if (_host.PerformanceMetrics is { } metrics) metrics.TrackingEnabled = enabled;
        _host.PersistSettings();
        RefreshPerformanceLog();
    }

    private void OnClearPerformanceLog(object sender, RoutedEventArgs e)
    {
        _host.PerformanceMetrics?.Clear();
        RefreshPerformanceLog();
    }

    private void RefreshPerformanceLog()
    {
        var entries = _host.PerformanceMetrics?.Entries ?? Array.Empty<Cotabby.Core.Suggestions.PerformanceEntry>();
        bool any = entries.Count > 0;
        PerfEmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        PerfClearButton.IsEnabled = any;
        // Newest first, matching a debugging session's reading order.
        PerfEntriesPanel.ItemsSource = entries.Reverse().Select(e => new
        {
            When = e.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
            Model = e.ModelId,
            Elapsed = e.ElapsedMilliseconds + " ms",
        }).ToList();
    }

    /// <summary>
    /// True when the Performance pane is the one on screen.
    /// </summary>
    private bool IsPerformancePaneShowing
    {
        get
        {
            if (_panes.Length == 0) return false;
            int idx = Math.Clamp(NavList.SelectedIndex, 0, _panes.Length - 1);
            return ReferenceEquals(_panes[idx], PerformancePane);
        }
    }

    /// <summary>
    /// Sampling, like upstream's zero-background-cost rule, is allowed only
    /// while the pane is on screen inside a visible, non-minimized window.
    /// </summary>
    private bool IsSamplingAllowed =>
        IsPerformancePaneShowing && IsVisible && WindowState != System.Windows.WindowState.Minimized;

    /// <summary>
    /// Single entry point for every state change that can start or end live
    /// sampling: pane switches, minimize/restore, hide/show and close.
    /// </summary>
    private void SyncGpuSamplingToVisibility()
    {
        if (IsSamplingAllowed) StartGpuSampling();
        else StopGpuSampling();
    }

    /// <summary>
    /// Entering the pane starts a new generation. The helper resets the tracker's
    /// baseline before that generation's first read (a fresh baseline for every
    /// visit) and the 1 s tick keeps both the readouts and the request table
    /// current.
    /// </summary>
    private void StartGpuSampling()
    {
        if (_gpuSampling) return;
        _gpuSampling = true;
        _gpuEpoch = _gpuSession.Start();
        _gpuAvailability = PerformanceCounterAvailability.Unknown;
        ApplyGpuAvailabilityHint();
        // Requests recorded while the pane sits open show up without reopening it.
        RefreshPerformanceLog();

        _gpuTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _gpuTimer.Tick -= OnGpuTick;
        _gpuTimer.Tick += OnGpuTick;
        _gpuTimer.Start();

        _ = PumpGpuSampleAsync(_gpuEpoch);
    }

    /// <summary>
    /// Leaving the pane (or hiding/minimizing/closing the window) stops sampling.
    /// This returns at once: the helper never waits for a provider read that is
    /// stuck inside the OS, and it makes any late result stale. The OS read
    /// itself cannot be cancelled, so it may still complete in the background —
    /// what is guaranteed is that nothing further is polled and that its result
    /// is dropped instead of reaching a readout.
    /// </summary>
    private void StopGpuSampling()
    {
        if (!_gpuSampling) return;
        _gpuSampling = false;
        _gpuTimer?.Stop();
        _gpuSession.Stop();

        // No numbers from the finished visit are left standing to look live.
        GpuProcessText.Text = "—";
        GpuDeviceText.Text = "—";
        GpuMemoryText.Text = "—";
        _gpuAvailability = PerformanceCounterAvailability.Unknown;
        ApplyGpuAvailabilityHint();
    }

    private void OnGpuTick(object? sender, EventArgs e)
    {
        // The request table refreshes on every tick even when the counters are
        // unavailable, so entries recorded while the pane stays open appear
        // without a manual refresh.
        RefreshPerformanceLog();
        _ = PumpGpuSampleAsync(_gpuEpoch);
    }

    /// <summary>
    /// Takes at most one reading per generation and writes it only if it still
    /// belongs to the live one. The continuation here can be deferred by the
    /// dispatcher, so the generation is checked again after the await: leaving
    /// the pane, minimizing or re-entering must not let an old reading through.
    /// </summary>
    private async Task PumpGpuSampleAsync(int epoch)
    {
        var sampled = await _gpuSession.SampleAsync();
        if (!_gpuSampling || epoch != _gpuEpoch) return;
        if (!sampled.HasValue) return;
        if (!sampled.Value.IsCurrent(_gpuSession.CurrentEpoch)) return;
        ApplyGpuSample(sampled.Value.Sample);
    }

    private void ApplyGpuSample(GpuUsageSample sample)
    {
        GpuProcessText.Text = WindowsGpuUsageProvider.FormatPercent(sample.ProcessUtilizationPercent);
        GpuDeviceText.Text = WindowsGpuUsageProvider.FormatPercent(sample.DeviceUtilizationPercent);
        GpuMemoryText.Text = WindowsGpuUsageProvider.FormatBytes(sample.DeviceMemoryBytes);
        // Availability follows the returned data, never a category-existence probe.
        _gpuAvailability = PerformanceSamplingSession.Classify(sample);
        ApplyGpuAvailabilityHint();
    }

    /// <summary>
    /// The hint appears only once real data says there is nothing to read. Before
    /// the first reading of a generation availability is unknown, so the hint
    /// stays hidden rather than claiming the counters are broken.
    /// </summary>
    private void ApplyGpuAvailabilityHint()
    {
        GpuUnavailableText.Visibility = _gpuAvailability == PerformanceCounterAvailability.Unavailable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---------- About ----------

    private void PopulateAbout()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"Version {version?.ToString() ?? "unknown"}";
        try
        {
            var logo = new BitmapImage(new Uri("pack://application:,,,/Assets/CotabbyLogo.png"));
            AboutLogo.Background = new ImageBrush(logo) { Stretch = Stretch.UniformToFill };
            AboutLogoFallback.Visibility = Visibility.Collapsed;
        }
        catch { /* asset ships separately — the emoji fallback stays */ }
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }

    // ---------- Shared ----------

    private Border NewSeparator()
    {
        var sep = new Border { Height = 1 };
        sep.SetResourceReference(Border.BackgroundProperty, "CardBorder");
        return sep;
    }
}
