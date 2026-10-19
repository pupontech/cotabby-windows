using Cotabby.Core.Insertion;

namespace Cotabby.Core.Tests;

public sealed class InsertionStrategySelectorTests
{
    [Theory]
    [InlineData("", InsertionMethod.Unicode)]
    [InlineData("short", InsertionMethod.Unicode)]
    [InlineData("123456789012345678901234", InsertionMethod.Unicode)]
    [InlineData("1234567890123456789012345", InsertionMethod.Clipboard)]
    [InlineData("line1\nline2", InsertionMethod.Clipboard)]
    [InlineData("\r", InsertionMethod.Unicode)]
    [InlineData("x\r\n", InsertionMethod.Clipboard)]
    public void Auto_UsesExistingUtf16LengthAndLfRule(string text, InsertionMethod expected)
    {
        Assert.Equal(expected, Select("auto", text));
    }

    [Fact]
    public void Auto_CountsSurrogatePairsAsTwoUtf16CodeUnits()
    {
        Assert.Equal(InsertionMethod.Unicode, Select("auto", new string('a', 22) + "😀"));
        Assert.Equal(InsertionMethod.Clipboard, Select("auto", new string('a', 23) + "😀"));
    }

    [Theory]
    [InlineData("unicode")]
    [InlineData("UNICODE")]
    [InlineData("UnIcOdE")]
    public void ExplicitUnicode_IsUnsupportedEvenForLongRequests(string strategy)
    {
        Assert.False(InsertionStrategySelector.TrySelect(strategy, new string('x', 500) + "\n😀", out _));
    }

    [Fact]
    public void ExplicitClipboard_SelectsClipboardEvenForShortText()
    {
        Assert.Equal(InsertionMethod.Clipboard, Select("clipboard", "ok"));
    }

    [Theory]
    [InlineData(null, "short", InsertionMethod.Unicode)]
    [InlineData("", "long enough to use clipboard", InsertionMethod.Clipboard)]
    [InlineData("legacy-value", "short", InsertionMethod.Unicode)]
    [InlineData("future-value", "long enough to use clipboard", InsertionMethod.Clipboard)]
    public void UnknownOrMissingSetting_FallsBackToAuto(
        string? configuredStrategy, string text, InsertionMethod expected)
    {
        Assert.Equal(expected, Select(configuredStrategy, text));
    }

    [Fact]
    public void Uia_IsUnsupportedAndDoesNotSelectAnyFallback()
    {
        Assert.False(InsertionStrategySelector.TrySelect("uia", "text", out _));
        Assert.False(InsertionStrategySelector.TrySelect("UIA", "text", out _));
        Assert.False(InsertionStrategySelector.TrySelect("UiA", "text", out _));
    }

    private static InsertionMethod Select(string? strategy, string text)
    {
        Assert.True(InsertionStrategySelector.TrySelect(strategy, text, out var method));
        return method;
    }
}
