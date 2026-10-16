using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public sealed class SecretSanitizerTests
{
    [Theory]
    [InlineData("ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("github_pat_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("glpat-abcdefghijklmnopqrstuvwx")]
    [InlineData("hf_abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("x" + "oxb-1...mnop")]
    [InlineData("s" + "k_liv...uvwx")]
    [InlineData("AIzaabcdefghijklmnopqrstuvwxyz123456789")]
    [InlineData("npm_abcdefghijklmnopqrstuvwxyz1234567890")]
    public void ProviderFormatsAreRedacted(string key) =>
        Assert.Equal(SecretSanitizer.Placeholder, SecretSanitizer.Sanitize(key));

    [Theory]
    [InlineData("password: hunter2", "password: [redacted]")]
    [InlineData("token=abcdefgh", "token=[redacted]")]
    [InlineData("\"password\": \"two secret words\"", "\"password\": [redacted]")]
    [InlineData("API_KEY='a value with spaces'; next", "API_KEY=[redacted]; next")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuv", "Authorization: Bearer [redacted]")]
    [InlineData("https://example.test/?access_token=abc12345&sort=date", "https://example.test/?access_token=[redacted]&sort=date")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nabcdef\n-----END RSA PRIVATE KEY-----", "[redacted]")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nunterminated", "[redacted]")]
    public void ExplicitSecretsAreRemoved(string input, string expected) =>
        Assert.Equal(expected, SecretSanitizer.Sanitize(input));

    [Theory]
    [InlineData("4111 1111 1111 1111", "[redacted]")]
    [InlineData("4111-1111-1111-1111", "[redacted]")]
    [InlineData("4111111111111112", "4111111111111112")]
    [InlineData("0000000000000000", "0000000000000000")]
    [InlineData("11111111111111111", "11111111111111111")]
    public void CardCandidatesRequireLuhnAndNontrivialDigits(string input, string expected) =>
        Assert.Equal(expected, SecretSanitizer.Sanitize(input));

    [Theory]
    [InlineData("This is ordinary prose with Unicode café 中文 🙂.")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("https://example.test/?sort=date&key=enter")]
    [InlineData("var tokenCount = 1234; const passwordLength = 8;")]
    [InlineData("abcDEF123ghiJKL456mnoPQR789stuVWX012yz")]
    [InlineData("-----BEGIN PUBLIC KEY-----abc-----END PUBLIC KEY-----")]
    public void OrdinaryTechnicalContentSurvives(string input) =>
        Assert.Equal(input, SecretSanitizer.Sanitize(input));

    [Fact]
    public void EmptyOversizedAndRepeatedInputsAreSafe()
    {
        Assert.Equal("", SecretSanitizer.Sanitize(null));
        Assert.Equal("", SecretSanitizer.Sanitize(""));
        Assert.Equal("[redacted]", SecretSanitizer.Sanitize(new string('x', SecretSanitizer.MaximumInputCharacters + 1)));
        string result = SecretSanitizer.Sanitize("password='synthetic secret' https://example.test/?token=abcdef&sort=date");
        Assert.Equal(result, SecretSanitizer.Sanitize(result));
    }

    [Fact]
    public void JwtAndConnectionPasswordAreRemoved()
    {
        string jwt = "eyJabcdefghijk.eyJabcdefghijk.abcdefghijklmn";
        Assert.Equal("[redacted]", SecretSanitizer.Sanitize(jwt));
        Assert.Equal("https://user:[redacted]@example.test/path", SecretSanitizer.Sanitize("https://user:synthetic@example.test/path"));
    }

    [Fact]
    public void RecognizedProviderKeyIsRemovedWithoutChangingProse()
    {
        string key = "sk-proj-" + new string('a', 32);
        Assert.Equal("Hello [redacted] world", SecretSanitizer.Sanitize($"Hello {key} world"));
    }
}
