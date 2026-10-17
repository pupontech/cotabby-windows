using System.Text.RegularExpressions;

namespace Cotabby.Core.Tests;

// Source contract, not a claim of Windows runtime coverage. Covers all logger
// levels, so enabling verbose diagnostics cannot silently re-enable prose dumps.
public sealed class ContentFreeDiagnosticsTests
{
    [Theory]
    [InlineData("Cotabby.Core/Suggestions/SuggestionCoordinator.cs")]
    [InlineData("Cotabby.Inference/LlamaSuggestionEngine.cs")]
    [InlineData("Cotabby.Win32/Insertion/SendInputTextInserter.cs")]
    public void ProductionDiagnosticsDoNotReceiveSuggestionContent(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Cotabby.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root!.FullName, "src", relativePath));
        foreach (Match call in Regex.Matches(source, @"_logger\.Log\w+\([\s\S]*?;"))
        {
            Assert.DoesNotMatch(@"\b(EscapeFull|HexDump|Preview)\s*\(", call.Value);
            Assert.DoesNotMatch(@",\s*(raw|accumulated|normalized\.Text|text)\s*[,)]", call.Value);
        }
    }
}
