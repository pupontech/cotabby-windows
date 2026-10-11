using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class CompletionPromptBudgetTests
{
    [Theory]
    [InlineData(4096, 25, 3982)]
    [InlineData(4096, 50, 3982)]
    [InlineData(4096, 52, 3980)]
    [InlineData(4096, 120, 3912)]
    [InlineData(4096, 128, 3904)]
    [InlineData(8192, 128, 8000)]
    [InlineData(100, 120, 0)]
    [InlineData(0, 25, 0)]
    [InlineData(-1, 25, 0)]
    [InlineData(int.MaxValue, int.MaxValue, 0)]
    public void ReservesWholeOutputAndSafetyMargin(int context, int output, int expected)
    {
        Assert.Equal(expected, CompletionPromptBudget.ForContext(context, output));
    }

    [Fact]
    public void NegativeOutputBudgetIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompletionPromptBudget.ForContext(4096, -1));
    }
}
