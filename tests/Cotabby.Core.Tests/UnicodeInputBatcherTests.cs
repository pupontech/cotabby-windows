using Cotabby.Core.Insertion;

namespace Cotabby.Core.Tests;

public sealed class UnicodeInputBatcherTests
{
    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    [InlineData("1234567😀abcdefgh😀")]
    [InlineData("short")]
    public void BatchesPreserveTextAndDoNotSplitSurrogatePairs(string text)
    {
        var batches = UnicodeInputBatcher.Split(text).ToArray();
        Assert.Equal(text, string.Concat(batches));
        Assert.All(batches, batch =>
        {
            Assert.InRange(batch.Length, 1, 8);
            Assert.False(char.IsHighSurrogate(batch[^1]));
            Assert.False(char.IsLowSurrogate(batch[0]));
        });
    }
}
