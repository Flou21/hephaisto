using Hephaisto.Agent.Mcp;
using ModelContextProtocol;

namespace Hephaisto.Tests.Mcp;

/// <summary>Reading a caller's words, and the cursor that pages an answer.</summary>
public sealed class McpQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("signals:0199aaaa:Firing")]
    [InlineData("timeline:0199aaaa")]
    [InlineData("plain")]
    public void A_cursor_comes_back_as_written_whatever_its_search_is_called(string key)
    {
        McpQuery.ReadOffset(McpQuery.Cursor(key, 25), key).Should().Be(25);

        var id = Guid.CreateVersion7();
        McpQuery.ReadKeyset(McpQuery.Cursor(key, Now, id), key).Should().Be((Now, id));
    }

    [Fact]
    public void A_cursor_of_another_search_or_none_of_ours_is_refused()
    {
        var cursor = McpQuery.Cursor("search:a", 10);

        var other = () => McpQuery.ReadOffset(cursor, "search:b");
        other.Should().Throw<McpException>().WithMessage("*different search*");

        var junk = () => McpQuery.ReadOffset("not-a-cursor!", "search:a");
        junk.Should().Throw<McpException>();

        McpQuery.ReadOffset(null, "search:a").Should().Be(0);
    }

    [Theory]
    [InlineData("24h", -24)]
    [InlineData("30m", -0.5)]
    [InlineData("7d", -168)]
    [InlineData("2w", -336)]
    public void A_duration_is_back_from_now(string value, double hours)
    {
        McpQuery.Time(value, Now, "openedAfter").Should().Be(Now.AddHours(hours));
    }

    [Fact]
    public void An_iso_time_is_itself_and_nonsense_is_refused_by_name()
    {
        McpQuery.Time("2026-09-01T00:00:00Z", Now, "openedAfter").Should().Be(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        var bad = () => McpQuery.Time("yesterday-ish", Now, "openedAfter");
        bad.Should().Throw<McpException>().WithMessage("openedAfter*");
    }

    [Fact]
    public void Severities_and_kinds_are_read_in_any_case_and_refused_when_unknown()
    {
        McpQuery.Severities("critical,Warning").Should().Equal(Hephaisto.Core.Domain.Severity.Critical, Hephaisto.Core.Domain.Severity.Warning);

        var bad = () => McpQuery.Severities("urgent");
        bad.Should().Throw<McpException>().WithMessage("*Critical*");
    }

    [Fact]
    public void What_a_caller_sent_is_repeated_short_and_without_markup()
    {
        McpQuery.Echo("<b>" + new string('x', 200)).Should().NotContain("<").And.HaveLength(67);
    }
}
