namespace Cotabby.Core.Focus;

/// <summary>Recognizes single-line sign-in and verification fields.</summary>
public static class CredentialFieldDetector
{
    private static readonly string[] LabelKeywords =
    [
        "email", "e-mail", "username", "user name", "user id", "userid", "login", "log in", "sign in",
        "phone", "mobile number", "password", "passcode", "pin", "one-time", "one time code",
        "verification code", "security code", "otp", "2fa", "two-factor", "authentication code",
        "card number", "cvc", "cvv", "expiry", "expiration",
        "passphrase", "pass phrase", "security answer", "recovery code",
    ];

    private static readonly string[] ProseLabelWords =
    [
        "subject", "message", "body", "preview", "title", "signature", "template",
        "comment", "note", "notes", "reply", "search",
    ];

    private static readonly string[] CredentialIdentifierWords =
    [
        "email", "mail", "username", "userid", "login", "logon", "signin", "passwd", "password", "pwd",
        "passcode", "otp", "totp", "mfa", "2fa", "phone", "telephone", "tel", "mobile", "msisdn", "pin",
        "cvc", "cvv", "cardnumber", "identifierid", "onetimecode", "verificationcode", "securitycode", "authcode",
    ];

    private static readonly string[] QualifierIdentifierWords =
    [
        "user", "account", "address", "addr", "id", "identifier", "name", "number", "num", "no", "code",
        "field", "input", "txt", "tb", "verification", "verify", "security", "auth", "onetime", "confirm",
        "current", "new", "primary", "your", "enter",
    ];

    private const int MaximumIdentifierLength = 64;

    /// <summary>Checks the whole metadata/text signature. Document and Pane are intentionally excluded.</summary>
    public static bool IsCredentialField(
        string controlType,
        IEnumerable<string?> labels,
        string? automationId,
        string? text,
        bool isPassword = false)
    {
        if (isPassword) return true;
        if (controlType is not ("Edit" or "ComboBox")) return false;

        if (labels.Any(label => label is not null && LabelNamesCredential(label))) return true;
        if (IdentifierNamesCredential(automationId)) return true;
        return LooksLikeAddressEntry(text);
    }

    /// <summary>Matches a credential label as whole words, unless it describes prose.</summary>
    public static bool LabelNamesCredential(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return LabelKeywords.Any(word => ContainsWord(label, word))
            && !ProseLabelWords.Any(word => ContainsWord(label, word));
    }

    /// <summary>Matches a complete, bounded identifier segmented by known vocabulary.</summary>
    public static bool IdentifierNamesCredential(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return false;
        var characters = identifier.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        if (characters.Length is 0 or > MaximumIdentifierLength) return false;

        // reach[i] records whether the prefix ending at i has a valid vocabulary split,
        // and whether any such split contains a credential word.
        var reach = new bool?[characters.Length + 1];
        reach[0] = false;
        for (int start = 0; start < characters.Length; start++)
        {
            if (reach[start] is not bool namesCredential) continue;
            if (char.IsDigit(characters[start]))
                reach[start + 1] = reach[start + 1] == true || namesCredential;

            foreach (string word in CredentialIdentifierWords)
                Advance(word, true);
            foreach (string word in QualifierIdentifierWords)
                Advance(word, false);

            void Advance(string word, bool isCredential)
            {
                if (start + word.Length > characters.Length) return;
                for (int offset = 0; offset < word.Length; offset++)
                    if (characters[start + offset] != word[offset]) return;
                int end = start + word.Length;
                reach[end] = reach[end] == true || namesCredential || isCredential;
            }
        }
        return reach[^1] == true;
    }

    /// <summary>Recognizes one complete or incomplete address-shaped token.</summary>
    public static bool LooksLikeAddressEntry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string candidate = text.Trim();
        int at = candidate.IndexOf('@');
        if (at <= 0 || candidate.IndexOf('@', at + 1) >= 0) return false;
        string localAndDomain = candidate;
        return !localAndDomain.Any(char.IsWhiteSpace)
            && !localAndDomain.Contains('/')
            && !localAndDomain.Contains(':');
    }

    private static bool ContainsWord(string label, string word)
    {
        int start = 0;
        while ((start = label.IndexOf(word, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = start + word.Length;
            bool before = start == 0 || !char.IsLetterOrDigit(label[start - 1]);
            bool after = end == label.Length || !char.IsLetterOrDigit(label[end]);
            if (before && after) return true;
            start = end;
        }
        return false;
    }
}
