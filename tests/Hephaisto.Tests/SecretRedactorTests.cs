using Hephaisto.Core.Safety;

namespace Hephaisto.Tests;

/// <summary>
/// The credential shapes <see cref="SecretRedactor"/> removes before text leaves for somebody
/// else's model.
/// </summary>
/// <remarks>
/// Written when the agent got a GitHub token of its own (v0.14.0), and found that the way a
/// credential is named in an environment was not matched: <c>_</c> is a word character, so
/// <c>\btoken\b</c> does not occur in <c>GITHUB_TOKEN</c>. A pod that printed its environment
/// would have carried the token into an investigation unless its value happened to start with
/// one of the known prefixes.
/// </remarks>
public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("GITHUB_TOKEN=abc123def456", "GITHUB_TOKEN=[redacted]")]
    [InlineData("GitHub__Token=abc123def456", "GitHub__Token=[redacted]")]
    [InlineData("GitHub:Token: abc123def456", "GitHub:Token: [redacted]")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN=abc123def456", "CLAUDE_CODE_OAUTH_TOKEN=[redacted]")]
    [InlineData("export NUGET_GITHUB_TOKEN='abc 123'", "export NUGET_GITHUB_TOKEN=[redacted]")]
    [InlineData("spring.datasource.password=hunter2", "spring.datasource.password=[redacted]")]
    [InlineData("Notifications__TeamsBot__ClientSecret=abc", "Notifications__TeamsBot__ClientSecret=[redacted]")]
    [InlineData("token=abc123", "token=[redacted]")]
    [InlineData("password: hunter2", "password: [redacted]")]
    public void A_credential_is_dropped_whatever_its_key_is_prefixed_with(string line, string expected) =>
        SecretRedactor.Redact(line).Should().Be(expected);

    [Theory]
    [InlineData("ghp_")]
    [InlineData("gho_")]
    [InlineData("ghu_")]
    [InlineData("ghs_")]
    [InlineData("ghr_")]
    [InlineData("github_pat_")]
    public void Every_github_token_prefix_is_known_by_sight(string prefix)
    {
        var token = prefix + "16C7e42F292c6912E7710c838347Ae178B4a";

        SecretRedactor.Redact($"cloning with {token} failed").Should().Be("cloning with [redacted] failed");
    }

    [Fact]
    public void An_authorization_header_loses_its_credential()
    {
        SecretRedactor.Redact("Authorization: Bearer abcdefgh12345678").Should().Be("Authorization: Bearer [redacted]");
    }

    [Theory]
    [InlineData("max_tokens: 4096")]
    [InlineData("input_tokens=1200 output_tokens=85")]
    [InlineData("the token bucket refilled")]
    [InlineData("tokenizer=cl100k")]
    public void A_word_that_only_contains_one_of_the_keys_is_left_alone(string line) =>
        SecretRedactor.Redact(line).Should().Be(line);
}
