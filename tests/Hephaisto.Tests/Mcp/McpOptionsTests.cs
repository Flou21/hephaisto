using Hephaisto.Agent.Mcp;

namespace Hephaisto.Tests.Mcp;

/// <summary>
/// What the MCP endpoint refuses to start with. Each refusal is a way the endpoint would be open
/// to somebody it should not be, or would record somebody it is not.
/// </summary>
public sealed class McpOptionsTests
{
    private static readonly Dictionary<string, int> Ports = new()
    {
        ["the console"] = 8080,
        ["the webhook"] = 8081,
        ["the Teams actions"] = 8082,
    };

    private const string Good = "0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void A_good_configuration_starts()
    {
        Options(Token("litellm", Good)).Refusals(Ports, signInOn: false, webhookToken: null).Should().BeEmpty();
    }

    [Fact]
    public void Off_is_never_refused()
    {
        new McpOptions { Enabled = false, Port = 8080 }.Refusals(Ports, false, null).Should().BeEmpty();
    }

    [Fact]
    public void No_credential_at_all_is_refused_and_sign_in_is_one()
    {
        Options().Refusals(Ports, signInOn: false, null).Should().ContainSingle(r => r.Contains("no caller could ever get in"));
        Options().Refusals(Ports, signInOn: true, null).Should().BeEmpty();

        var noIdp = Options();
        noIdp.AcceptIdentityProviderTokens = false;
        noIdp.Refusals(Ports, signInOn: true, null).Should().ContainSingle(r => r.Contains("no caller could ever get in"));
    }

    [Theory]
    [InlineData(8080, "the console")]
    [InlineData(8081, "the webhook")]
    [InlineData(8082, "the Teams actions")]
    public void A_port_somebody_else_has_is_refused(int port, string whose)
    {
        var o = Options(Token("litellm", Good));
        o.Port = port;

        o.Refusals(Ports, false, null).Should().ContainSingle(r => r.Contains(whose));
    }

    [Fact]
    public void A_short_token_is_refused_with_its_length_and_the_minimum()
    {
        Options(Token("litellm", "too-short")).Refusals(Ports, false, null)
            .Should().ContainSingle(r => r.Contains("9 characters") && r.Contains("32"));
    }

    [Fact]
    public void A_token_with_no_value_is_refused()
    {
        Options(Token("litellm", null)).Refusals(Ports, false, null).Should().ContainSingle(r => r.Contains("no value"));
    }

    [Fact]
    public void Two_tokens_with_one_value_or_one_name_are_refused()
    {
        Options(Token("a", Good), Token("b", Good)).Refusals(Ports, false, null)
            .Should().ContainSingle(r => r.Contains("same value"));
        Options(Token("a", Good), Token("a", Good + "x")).Refusals(Ports, false, null)
            .Should().ContainSingle(r => r.Contains("configured twice"));
    }

    [Fact]
    public void The_webhooks_token_is_refused_as_an_mcp_token()
    {
        Options(Token("litellm", Good)).Refusals(Ports, false, webhookToken: Good)
            .Should().ContainSingle(r => r.Contains("webhook's token"));
    }

    [Theory]
    [InlineData("LiteLLM")]
    [InlineData("lite llm")]
    [InlineData("-litellm")]
    [InlineData("")]
    public void A_name_the_audit_trail_cannot_carry_is_refused(string name)
    {
        Options(Token(name, Good)).Refusals(Ports, false, null).Should().Contain(r => r.Contains("is not a name"));
    }

    [Fact]
    public void A_role_or_kind_that_does_not_exist_is_refused()
    {
        var t = Token("litellm", Good);
        t.Role = "admin";
        t.Kind = "robot";

        var refusals = Options(t).Refusals(Ports, false, null);
        refusals.Should().Contain(r => r.Contains("reader or approver"));
        refusals.Should().Contain(r => r.Contains("shared or person"));
    }

    [Fact]
    public void A_person_token_needs_a_person()
    {
        var t = Token("flo", Good);
        t.Kind = McpTokenOptions.Person;

        Options(t).Refusals(Ports, false, null).Should().ContainSingle(r => r.Contains("no Subject"));
    }

    [Theory]
    [InlineData("hephaisto/model")]
    [InlineData("model")]
    [InlineData("llm")]
    [InlineData("hephaisto/anything")]
    [InlineData("mcp/litellm")]
    public void A_person_token_may_not_act_as_the_agent_the_model_or_a_shared_token(string subject)
    {
        var t = Token("somebody", Good);
        t.Kind = McpTokenOptions.Person;
        t.Subject = subject;

        Options(t).Refusals(Ports, false, null).Should().ContainSingle(r => r.Contains("reserved"));
    }

    [Fact]
    public void A_shared_token_acts_as_itself_and_a_person_token_as_its_person()
    {
        McpCaller.ActorFor(Token("litellm", Good)).Should().Be("mcp/litellm");

        var person = Token("flo", Good);
        person.Kind = McpTokenOptions.Person;
        person.Subject = "flo";
        McpCaller.ActorFor(person).Should().Be("flo");
    }

    private static McpOptions Options(params McpTokenOptions[] tokens) =>
        new() { Enabled = true, Port = 8083, Tokens = [.. tokens] };

    private static McpTokenOptions Token(string name, string? value) => new() { Name = name, Value = value };
}
