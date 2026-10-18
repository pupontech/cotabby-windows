using Cotabby.Core.Focus;
using Cotabby.Core.Settings;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests;

public sealed class AppProfileCoordinatorTests
{
    [Fact]
    public async Task FocusChangeAppliesProfileAndUnlistedProcessRestoresGlobalSettings()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        var profile = new AppProfileOverride
        {
            ProcessName = "notepad",
            AcceptanceMode = "phrase",
            CompletionLengthPreset = "short",
            DisplayMode = "popup",
            AllowMultiLine = false,
            DebounceMs = 120,
            MaxTokensSingleLine = 14,
            MaxTokensMultiLine = 22,
        };

        rig.Coordinator.ConfigureProfileSettings(global, new AppProfileOverride?[] { profile }, "cotabby");
        rig.Focus.SetField(Field("notepad", 41001));
        rig.Ui.DrainAll();

        Assert.Equal("phrase", rig.Coordinator.AcceptanceGranularity);
        Assert.Equal(14, rig.Coordinator.MaxTokensSingleLine);
        Assert.Equal(22, rig.Coordinator.MaxTokensMultiLine);
        Assert.Equal(TimeSpan.FromMilliseconds(120), rig.Coordinator.RequestDebounce);
        Assert.False(rig.Coordinator.AllowMultiLineSuggestions);
        Assert.Equal("popup", rig.Coordinator.DisplayMode);
        Assert.Equal("popup", rig.Overlay.DisplayMode);

        rig.Focus.SetField(Field("other-app", 41002));
        rig.Ui.DrainAll();

        Assert.Equal("word", rig.Coordinator.AcceptanceGranularity);
        Assert.Equal(25, rig.Coordinator.MaxTokensSingleLine);
        Assert.Equal(TimeSpan.FromMilliseconds(50), rig.Coordinator.RequestDebounce);
        Assert.True(rig.Coordinator.AllowMultiLineSuggestions);
        Assert.Equal("auto", rig.Coordinator.DisplayMode);
        Assert.Equal("auto", rig.Overlay.DisplayMode);
    }

    [Fact]
    public async Task AddingFocusedProcessToBlocklistRetiresVisibleSuggestion()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        rig.Coordinator.ConfigureProfileSettings(global, Array.Empty<AppProfileOverride?>(), "cotabby");
        rig.Ui.DrainAll();

        rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Character,
            Character = 'x',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        });
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Engine.Release.TrySetResult();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (!rig.Overlay.IsVisible)
            {
                await rig.Ui.WaitForPendingAtLeastAsync(1).WaitAsync(timeout.Token);
                rig.Ui.DrainAll();
            }
        }

        rig.Coordinator.BlockedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test-host" };
        rig.Ui.DrainAll();

        Assert.False(rig.Overlay.IsVisible);
        Assert.False(rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Tab,
            Character = '\0',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        }));
        Assert.Empty(rig.Inserter.Inserted);
    }

    [Fact]
    public async Task SameProcessFieldChangeCancelsInFlightGeneration()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        rig.Coordinator.ConfigureProfileSettings(global, Array.Empty<AppProfileOverride?>(), "cotabby");
        rig.Ui.DrainAll();
        rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Character,
            Character = 'x',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        });
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var current = rig.Focus.Current!;
        rig.Focus.SetField(current with { StableFieldIdentity = "different-field" });
        rig.Ui.DrainAll();

        await rig.Engine.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        rig.Engine.Release.TrySetResult();
        rig.Ui.DrainAll();

        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task ProfileDisableAfterPresentationRetiresSuggestionBeforeAccept()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        rig.Coordinator.ConfigureProfileSettings(global,
            [new AppProfileOverride { ProcessName = "test-host" }], "cotabby");
        rig.Ui.DrainAll();
        rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Character,
            Character = 'x',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        });
        await rig.Engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Engine.Release.TrySetResult();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (!rig.Overlay.IsVisible)
            {
                await rig.Ui.WaitForPendingAtLeastAsync(1).WaitAsync(timeout.Token);
                rig.Ui.DrainAll();
            }
        }

        rig.Coordinator.ConfigureProfileSettings(global,
            [new AppProfileOverride { ProcessName = "test-host", Enabled = false }], "cotabby");
        rig.Ui.DrainAll();

        Assert.False(rig.Overlay.IsVisible);
        Assert.False(rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Tab,
            Character = '\0',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        }));
        Assert.Empty(rig.Inserter.Inserted);
    }

    [Fact]
    public async Task FirstProfileSnapshotRetiresAnAlreadyVisibleSuggestion()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false);
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);

        rig.Coordinator.ConfigureProfileSettings(global,
            [new AppProfileOverride { ProcessName = "test-host", Enabled = false }], "cotabby");
        rig.Ui.DrainAll();

        Assert.False(rig.Overlay.IsVisible);
        Assert.False(rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Tab,
            Character = '\0',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        }));
        Assert.Empty(rig.Inserter.Inserted);
    }

    [Fact]
    public async Task ProfileEditCancelsPendingDebounce()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false, showSuggestion: false);
        var global = new EffectiveSuggestionSettings(true, "word", "long", "auto", true, 50, 25, 50);
        rig.Coordinator.ConfigureProfileSettings(global,
            [new AppProfileOverride { ProcessName = "test-host", DebounceMs = 200 }], "cotabby");
        rig.Ui.DrainAll();
        rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Character,
            Character = 'x',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        });

        rig.Coordinator.ConfigureProfileSettings(global,
            [new AppProfileOverride { ProcessName = "test-host", DebounceMs = 50 }], "cotabby");
        rig.Ui.DrainAll();
        await Task.Delay(300);
        rig.Ui.DrainAll();

        Assert.False(rig.Engine.Started.Task.IsCompleted);
        Assert.Null(rig.Engine.LastRequest);
        Assert.False(rig.Overlay.IsVisible);
    }

    [Fact]
    public async Task MissingFocusNotificationRetiresVisibleSessionOnNextKey()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false);

        rig.Focus.ClearField();
        rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Character,
            Character = 'y',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        });

        Assert.False(rig.Overlay.IsVisible);
        Assert.Empty(rig.Inserter.Inserted);
    }

    [Fact]
    public async Task LowPowerGateBlocksPostAcceptContinuation()
    {
        await using var rig = await SuggestionCoordinatorDoubleTapTests.Rig.CreateAsync(false);
        rig.Coordinator.AlternateAcceptKey = '~';
        Assert.True(rig.Hook.Emit(new Cotabby.Core.Input.KeyboardEvent
        {
            Kind = Cotabby.Core.Input.KeyKind.Character,
            Character = '~',
            HasNonShiftModifier = false,
            IsKeyDown = true,
        }));
        await rig.WaitForOutcomesAsync(1);
        Assert.Single(rig.Inserter.Inserted);

        rig.Coordinator.LowPowerModePause = new FixedLowPowerModeProvider(true);
        await Task.Delay(200);
        rig.Ui.DrainAll();

        Assert.Equal(1, rig.Engine.GenerationCount);
        Assert.False(rig.Overlay.IsVisible);
    }

    private sealed class FixedLowPowerModeProvider(bool enabled) : Cotabby.Core.Suggestions.ILowPowerModeProvider
    {
        public bool IsLowPowerModeEnabled { get; } = enabled;
    }

    private static FocusedField Field(string processName, int processId) => new()
    {
        ElementHandle = new object(),
        StableFieldIdentity = processName + "-field",
        ProcessId = processId,
        ProcessName = processName,
        Text = "a",
        CaretOffset = 1,
        CaretRect = new ScreenRect(10, 20, 1, 20),
        FieldRect = new ScreenRect(0, 0, 100, 30),
        IsSingleLine = true,
        IsSecure = false,
        IsCredentialField = false,
    };
}
