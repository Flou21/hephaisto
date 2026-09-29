using Hephaisto.Core.Domain;

namespace Hephaisto.Tests.Mcp;

/// <summary>How an agent's change reads to a person (#157, D3).</summary>
public sealed class ActorDisplayTests
{
    [Fact]
    public void A_person_is_shown_as_themselves()
    {
        ActorDisplay.Render("flo").Should().Be("flo");
        ActorDisplay.Render("flo", "lead").Should().Be("flo", "a claim only means something beside an agent");
        ActorDisplay.Render(null).Should().BeNull();
    }

    [Fact]
    public void A_shared_token_is_an_agent_and_its_claim_is_marked_unverified()
    {
        ActorDisplay.Render("mcp/litellm").Should().Be("an agent (litellm)");
        ActorDisplay.Render("mcp/litellm", "flo").Should().Be("an agent (litellm), for flo - unverified");
    }
}
