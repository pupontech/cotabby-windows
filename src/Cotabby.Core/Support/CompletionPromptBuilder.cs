using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Support;

/// <summary>The Windows request-to-prompt seam; callers supply the loaded model's tokenizer.</summary>
public static class CompletionPromptBuilder
{
    public static string Build(SuggestionRequest request, int contextTokens, Func<string, int> tokenCounter)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        return BaseCompletionPromptRenderer.Prompt(
            BaseCompletionPromptRenderer.WindowPrefix(request.Prefix),
            userName: request.UserName,
            languageInstruction: request.LanguageInstruction,
            tokenBudget: CompletionPromptBudget.ForContext(contextTokens, request.MaxTokens),
            tokenCounter: tokenCounter);
    }
}
