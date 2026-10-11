using System.Text;
using Cotabby.Core.Support;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests;

public class CompletionPromptBuilderTests
{
    [Theory]
    [InlineData(512, 25)]
    [InlineData(512, 50)]
    [InlineData(512, 120)]
    [InlineData(1024, 128)]
    public void RequestWholeOutputFitsAlongsideRenderedPrompt(int context, int output)
    {
        var request = new SuggestionRequest
        {
            RequestId = "budget-test",
            Prefix = new string('界', 1000),
            Suffix = "",
            HostApp = "test",
            SingleLine = false,
            MaxTokens = output,
            UserName = "Troy",
            LanguageInstruction = "The text is written in English."
        };
        int Count(string text) => Encoding.UTF8.GetByteCount(text) + 1;
        var prompt = CompletionPromptBuilder.Build(request, context, Count);
        Assert.NotEmpty(prompt);
        Assert.InRange(Count(prompt) + Math.Max(50, output) + 64, 0, context);
        Assert.EndsWith(prompt, request.Prefix);
    }
}
