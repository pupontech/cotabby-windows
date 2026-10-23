using System.Reflection;

namespace Cotabby.Core.Tests;

public sealed class TextReplacementResultTests
{
    [Fact]
    public void ShortSendInputCountIsReportedAsPartialWithKeyReleaseRecovery()
    {
        var resultType = typeof(Cotabby.Core.Insertion.ITextInserter).Assembly
            .GetType("Cotabby.Core.Insertion.TextReplacementResult");

        Assert.NotNull(resultType);
        var factory = resultType!.GetMethod("FromSendInput", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(factory);

        var result = factory!.Invoke(null, [8u, 3u, true]);
        Assert.NotNull(result);
        Assert.Equal("Partial", resultType.GetProperty("Status")!.GetValue(result)!.ToString());
        Assert.Equal(8u, resultType.GetProperty("RequestedInputCount")!.GetValue(result));
        Assert.Equal(3u, resultType.GetProperty("InsertedInputCount")!.GetValue(result));
        Assert.True((bool)resultType.GetProperty("KeyReleaseRecovered")!.GetValue(result)!);
    }
}
