using Cotabby.Core.Insertion;

namespace Cotabby.Core.Tests;

public sealed class EmojiReplacementContractTests
{
    [Fact]
    public void TextInserterExposesOneGuardedReplaceTransaction()
    {
        var method = typeof(ITextInserter).GetMethod("ReplaceTextAsync");

        Assert.NotNull(method);
        Assert.Equal("TextReplacementResult", method!.ReturnType.GetGenericArguments()[0].Name);
        Assert.Equal(
            new[]
            {
                typeof(Cotabby.Core.Focus.FocusedField), typeof(int), typeof(string), typeof(string), typeof(int),
                typeof(CancellationToken),
            },
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
