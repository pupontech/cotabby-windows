using System.Text.RegularExpressions;

namespace Cotabby.Core.Tests;

public sealed class KeyboardHookInteropTests
{
    [Fact]
    public void HookShutdownBindsTheExportedUnicodePostThreadMessageEntryPoint()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Cotabby.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName,
            "src", "Cotabby.Win32", "Interop", "NativeMethods.cs"));
        Assert.Matches(new Regex(@"\[LibraryImport\(""user32\.dll"",\s*EntryPoint\s*=\s*""PostThreadMessageW""[^\]]*\)\][\s\S]*?public static partial bool PostThreadMessage\("), source);
    }
}
