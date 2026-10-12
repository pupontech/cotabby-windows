using System.Xml.Linq;
using Xunit;

namespace Cotabby.Core.Tests;

/// <summary>Supplementary source wiring checks; not a substitute for live WPF rendering.</summary>
public class PerformancePaneMarkupTests
{
    [Fact]
    public void GpuReadoutsOccupyASeparateColumnFromTheirLabels()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cotabby.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory!.FullName,
            "src", "Cotabby.App", "UI", "SettingsWindow.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "GpuProcessText", "GpuDeviceText", "GpuMemoryText" })
        {
            var readout = Assert.Single(document.Descendants(wpf + "TextBlock"),
                element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("1", (string?)readout.Attribute("Grid.Column"));
            Assert.Equal(2, readout.Parent!.Element(wpf + "Grid.ColumnDefinitions")!
                .Elements(wpf + "ColumnDefinition").Count());
        }
    }
}
