using System.Text;
using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class TokenBudgetedPromptTests
{
    private static int CountTokens(string text) => Encoding.UTF8.GetByteCount(text) + 1;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void NoRoomForTextReturnsEmpty(int budget)
    {
        Assert.Empty(BaseCompletionPromptRenderer.Prompt("caret", tokenBudget: budget, tokenCounter: CountTokens));
    }

    [Fact]
    public void TokenBudgetRequiresCounter()
    {
        Assert.Throws<ArgumentNullException>(() => BaseCompletionPromptRenderer.Prompt("caret", tokenBudget: 10));
    }

    [Fact]
    public void NegativeTokenCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BaseCompletionPromptRenderer.Prompt(
            "caret", tokenBudget: 10, tokenCounter: _ => -1));
    }

    [Fact]
    public void EnoughTokensLeaveLegacyPromptUnchanged()
    {
        var legacy = BaseCompletionPromptRenderer.Prompt("caret", userName: "Troy",
            customRules: ["clear"], languageInstruction: "English", extendedContext: "notes");
        Assert.Equal(legacy, BaseCompletionPromptRenderer.Prompt("caret", userName: "Troy",
            customRules: ["clear"], languageInstruction: "English", extendedContext: "notes",
            tokenBudget: 200, tokenCounter: CountTokens));
    }

    [Fact]
    public void SerializedSeparatorsAreCountedAndLowerPriorityNotesAreDroppedFirst()
    {
        var expected = "Written by A.\nWriting style: clear.\nEnglish\n\ncaret";
        var prompt = BaseCompletionPromptRenderer.Prompt("caret", userName: "A",
            customRules: ["clear"], languageInstruction: "English", extendedContext: "large notes",
            tokenBudget: CountTokens(expected), tokenCounter: CountTokens);
        Assert.Equal(expected, prompt);
    }

    [Fact]
    public void NonAdditiveTokenCounterMeasuresWholeSerializedPrompt()
    {
        int Counter(string text) => CountTokens(text) + (text.Contains("\n\n") ? 100 : 0);
        var prompt = BaseCompletionPromptRenderer.Prompt("caret", userName: "A",
            tokenBudget: 30, tokenCounter: Counter);
        Assert.Equal("caret", prompt);
    }

    [Fact]
    public void NonMonotonicCounterStillReturnsMeasuredFit()
    {
        int Counter(string text) => text.Length is 5 or 7 ? 100 : CountTokens(text);
        var prompt = BaseCompletionPromptRenderer.Prompt("abcdefghijk", tokenBudget: 9, tokenCounter: Counter);
        Assert.NotEmpty(prompt);
        Assert.InRange(Counter(prompt), 0, 9);
        Assert.EndsWith(prompt, "abcdefghijk");
    }

    [Fact]
    public void CharacterTrimmingDoesNotSplitSurrogatePairBeforeTokenCounting()
    {
        Assert.Equal("B", BaseCompletionPromptRenderer.Prompt("😀B", contextBudget: 2,
            tokenBudget: 10, tokenCounter: CountTokens));
    }

    [Fact]
    public void TokenTrimmingDoesNotSplitSurrogatePairAtCaretTail()
    {
        var prompt = BaseCompletionPromptRenderer.Prompt("😀B", tokenBudget: 5, tokenCounter: CountTokens);
        Assert.Equal("B", prompt);
    }

    [Fact]
    public void ExactBudgetCountsBosAndKeepsCaretTail()
    {
        var prompt = BaseCompletionPromptRenderer.Prompt("old text nearest caret",
            userName: "Troy", tokenBudget: 14, tokenCounter: CountTokens);
        Assert.Equal("nearest caret", prompt);
        Assert.Equal(14, CountTokens(prompt));
    }
}
