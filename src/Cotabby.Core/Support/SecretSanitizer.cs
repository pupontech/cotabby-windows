using System.Text.RegularExpressions;

namespace Cotabby.Core.Support;

/// <summary>
/// Conservative defense in depth for approved imports and metadata diagnostics.
/// Never authorizes reading excluded fields or logging document content.
/// Adapted from TabType (MIT); see THIRD_PARTY_NOTICES.md.
/// </summary>
public static class SecretSanitizer
{
    public const string Placeholder = "[redacted]";
    public const int MaximumInputCharacters = 256 * 1024;
    private static readonly Regex ProviderKey = new(
        @"\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,}|(?:sk|rk)_(?:live|test)_[A-Za-z0-9]{16,}|(?:AKIA|ASIA)[0-9A-Z]{16}\b|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|glpat-[A-Za-z0-9_-]{20,}|xox[abposr]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35}|hf_[A-Za-z0-9]{30,}|npm_[A-Za-z0-9]{36})",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static Regex Rule(string pattern) => new(pattern,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));

    private static readonly Regex PrivateKey = Rule(
        @"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z0-9]+ )*PRIVATE KEY-----|\z)");
    private static readonly Regex Authorization = Rule(@"(\b(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]{16,}");
    private static readonly Regex UrlSecret = Rule(
        "([?&](?:sig|signature|x-amz-signature|x-goog-signature|token|access_token|api_key|apikey|secret|password)=)[^&\\s\"'<>]+");
    private static readonly Regex Assignment = Rule(
        "(\\b(?:password|passwd|passphrase|pwd|secret|client[_-]?secret|api[_-]?key|access[_-]?key|private[_-]?key|token|auth[_-]?token|access[_-]?token|refresh[_-]?token)\\b[\"']?\\s*[:=]\\s*)(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s\"',;&<>]+)");

    private static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex ConnectionPassword = Rule(@"(\b[a-z][a-z0-9+.-]*://[^\s:/@]+:)[^\s@/]+(?=@)");
    private static readonly Regex CardCandidate = Rule(@"\b[0-9](?:[ -]?[0-9]){12,18}\b");

    private static string RedactCard(Match match)
    {
        Span<int> digits = stackalloc int[19];
        int count = 0;
        foreach (char c in match.ValueSpan)
            if (c is >= '0' and <= '9') digits[count++] = c - '0';
        bool identical = true;
        for (int i = 1; i < count; i++) identical &= digits[i] == digits[0];
        if (identical || count is < 13 or > 19) return match.Value;
        int sum = 0;
        for (int i = count - 1, offset = 0; i >= 0; i--, offset++)
        {
            int value = digits[i];
            if ((offset & 1) != 0) { value *= 2; if (value > 9) value -= 9; }
            sum += value;
        }
        return sum % 10 == 0 ? Placeholder : match.Value;
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (text.Length > MaximumInputCharacters) return Placeholder;
        try
        {
            string result = PrivateKey.Replace(text, Placeholder);
            result = Jwt.Replace(result, Placeholder);
            result = ConnectionPassword.Replace(result, m => m.Groups[1].Value + Placeholder);
            result = ProviderKey.Replace(result, Placeholder);
            result = Authorization.Replace(result, m => m.Groups[1].Value + Placeholder);
            result = UrlSecret.Replace(result, m => m.Groups[1].Value + Placeholder);
            result = Assignment.Replace(result, m => m.Groups[1].Value + Placeholder);
            return CardCandidate.Replace(result, RedactCard);
        }
        catch (RegexMatchTimeoutException) { return Placeholder; }
    }
}
