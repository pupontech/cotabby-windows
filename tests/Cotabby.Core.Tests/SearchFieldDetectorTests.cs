using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests;

public sealed class SearchFieldDetectorTests
{
    [Theory]
    [InlineData("Search mail", null, true)]
    [InlineData("Look for people, messages, files, and more.", null, true)]
    [InlineData("Find (Ctrl+F)", null, true)]
    [InlineData("Filter items", null, true)]
    [InlineData("Arama", null, true)]
    [InlineData("Research notes", null, false)]
    [InlineData("Finder notes", null, false)]
    [InlineData(null, "ms-searchux-input", true)]
    [InlineData(null, "quickSearchInput", true)]
    [InlineData(null, "research-notes", false)]
    [InlineData(null, null, false)]
    public void SingleLineMetadataUsesWholeWordsAndIdentifierSegments(string? label, string? id, bool expected) =>
        Assert.Equal(expected, SearchFieldDetector.IsSearchField("Edit", [label], id));

    [Fact]
    public void MultilineDocumentsAreNotSuppressedForMentioningSearch() =>
        Assert.False(SearchFieldDetector.IsSearchField("Document", ["Search research notes"], "searchDocument"));

    [Fact]
    public void ComboBoxSearchIsSuppressed() =>
        Assert.True(SearchFieldDetector.IsSearchField("ComboBox", ["Search"], null));

    [Fact]
    public void BrowserChromeIsNotInferredFromMissingDomAttributes() =>
        Assert.True(SearchFieldDetector.IsSearchField("Edit", ["Search the web"], null));
}
