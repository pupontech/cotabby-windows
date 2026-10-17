namespace Cotabby.Core.Focus;

/// <summary>Opt-in metadata denylist and bounded transient back-off, never a secret detector guarantee.</summary>
public static class FieldSuppressionPolicy
{
    public static IReadOnlyList<string> ProtectedProcesses { get; } = Array.AsReadOnly(new[]
        { "1password", "keepass", "keepassxc", "bitwarden", "enpass", "dashlane" });

    public static bool IsProtectedProcess(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return ProtectedProcesses.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static bool ShouldBackOff(string? text, bool isSingleLine)
    {
        if (!isSingleLine || string.IsNullOrEmpty(text) || char.IsWhiteSpace(text[^1])) return false;
        int lower = 0, upper = 0, digit = 0, symbol = 0, length = 0;
        for (int i = text.Length - 1; i >= 0 && length < 128 && !char.IsWhiteSpace(text[i]); i--, length++)
        {
            char c = text[i];
            if (char.IsLower(c)) lower = 1;
            else if (char.IsUpper(c)) upper = 1;
            else if (char.IsDigit(c)) digit = 1;
            else symbol = 1;
        }
        return length >= 8 && lower + upper + digit + symbol >= 3;
    }
}
