using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Notifications.TeamsBot;
using Hephaisto.Core.Domain;
using Hephaisto.Core.Investigations;

namespace Hephaisto.Tests.Notifications;

/// <summary>
/// The investigation on the card: a person reads what was found before they open anything. What is
/// load-bearing is that model-written text can never become markup on a card people trust, and
/// that an investigation that found nothing grounded says so rather than looking like a diagnosis.
/// </summary>
public sealed class TeamsBotDiagnosisCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 7, 0, 0, TimeSpan.Zero);

    private static readonly TeamsCardLinks Links = new() { BaseUrl = "https://hephaisto.example/" };

    private static TeamsDiagnosis Found(string hypothesis = "ConnectionHealthCache lets a probe timeout stop the host.") => new()
    {
        Hypothesis = hypothesis,
        Category = "application",
        Confidence = 0.85,
        Summary = "A code fix is needed; nothing in the cluster to do.",
        Termination = TerminationReason.Concluded,
        Executor = InvestigationExecutors.Job,
        ModelId = "claude-opus-5-5",
        Evidence =
        [
            "at Hephaisto.Agent.Observability.OidcProbe.ProbeAsync in ConnectionProbes.cs:line 271",
            "BackgroundServiceExceptionBehavior is configured to StopHost",
            "a third excerpt that is not shown",
        ],
        CodeRefs = [new CodeRef { Path = "src/Hephaisto.Agent/Observability/ConnectionHealthCache.cs", Line = 84, Ref = "c421c384f80de48d39b729c407bb319913329711" }],
    };

    private static TeamsIncident Incident(TeamsDiagnosis? diagnosis) => new()
    {
        Id = Guid.Parse("0192a6f0-0000-7000-8000-0000000000bb"),
        Title = "CrashLoopBackOff on hephaisto",
        Kind = SignalKind.CrashLoopBackOff,
        Severity = Severity.Critical,
        State = IncidentState.Escalated,
        EscalationReason = EscalationReason.NoPlanProduced,
        Target = "hephaisto/Deployment/hephaisto",
        OpenedAt = Now.AddMinutes(-20),
        Diagnosis = diagnosis,
    };

    private static string CardJson(JsonObject activity) => activity["attachments"]![0]!["content"]!.ToJsonString();

    /// <summary>Every TextBlock's text in the card: the elements Teams renders as markdown.</summary>
    private static List<string> TextBlocks(JsonObject activity)
    {
        var found = new List<string>();

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock" && e.TryGetProperty("text", out var text))
                        found.Add(text.GetString() ?? string.Empty);
                    foreach (var p in e.EnumerateObject())
                        Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        Walk(item);
                    break;
            }
        }

        Walk(JsonDocument.Parse(CardJson(activity)).RootElement);
        return found;
    }

    [Fact]
    public void The_alert_shows_what_was_found_what_it_rests_on_and_who_found_it()
    {
        var json = CardJson(TeamsBotCards.Alert(Incident(Found()), Links));

        json.Should().Contain("Diagnosis")
            .And.Contain("ConnectionHealthCache lets a probe timeout stop the host.")
            .And.Contain("confidence 0.85")
            .And.Contain("by Claude Code in a Job, claude-opus-5-5")
            .And.Contain("A code fix is needed")
            .And.Contain("OidcProbe.ProbeAsync")
            .And.Contain("Evidence (2 of 3)")
            .And.Contain("ConnectionHealthCache.cs:84 at c421c384f80d");
        json.Should().NotContain("a third excerpt that is not shown", "a card is read on a phone; the console has the rest");
    }

    [Fact]
    public void Model_written_text_is_never_rendered_as_markdown()
    {
        // A log line can carry markdown, and a model can quote it. In a TextBlock Teams would
        // render [..](..) as a link on a card people trust; a TextRun renders it as text.
        var hostile = "Run [the fix](https://evil.example/pwn) now **urgently**";
        var activity = TeamsBotCards.Alert(Incident(Found(hostile) with { Evidence = [hostile] }), Links);

        TextBlocks(activity).Should().NotContain(t => t.Contains("evil.example"));
        CardJson(activity).Should().Contain("evil.example", "the text is shown, as text");
        TextBlocks(TeamsBotCards.Board([Incident(Found(hostile))], 1, Links, Now))
            .Should().NotContain(t => t.Contains("evil.example"));
    }

    [Fact]
    public void A_secret_in_a_hypothesis_is_redacted_before_it_reaches_Teams()
    {
        var json = CardJson(TeamsBotCards.Alert(Incident(Found("the pod logged password=hunter2hunter2 at start")), Links));

        json.Should().NotContain("hunter2hunter2");
    }

    [Fact]
    public void A_board_row_carries_the_diagnosis_in_one_short_line()
    {
        var json = CardJson(TeamsBotCards.Board([Incident(Found(new string('h', 1000)))], 1, Links, Now));

        var line = System.Text.RegularExpressions.Regex.Match(json, "Diagnosis: (h+)\\.\\.\\. \\(0\\.85\\)");
        line.Success.Should().BeTrue("the row names the diagnosis and its confidence");
        line.Groups[1].Value.Length.Should().Be(180, "the row's line is cut; the fuller hypothesis is under Details");
    }

    [Fact]
    public void An_investigation_that_grounded_nothing_says_so()
    {
        var json = CardJson(TeamsBotCards.Alert(
            Incident(new TeamsDiagnosis { Termination = TerminationReason.StepBudgetExhausted, ModelId = "true-relevance" }),
            Links));

        json.Should().Contain("No grounded diagnosis: the investigation ended StepBudgetExhausted (in-process, true-relevance)");
    }

    [Fact]
    public void An_incident_nobody_investigated_has_no_diagnosis_section()
    {
        CardJson(TeamsBotCards.Alert(Incident(diagnosis: null), Links)).Should().NotContain("Diagnosis");
    }

    [Fact]
    public void Forty_diagnosed_rows_still_fit_the_board()
    {
        var incidents = Enumerable.Range(0, 40)
            .Select(i => Incident(Found(new string('d', 700)) with { Summary = new string('s', 700) }) with { Id = Guid.NewGuid() })
            .ToList();

        var board = TeamsBotCards.BoardWithin(incidents, 40, Links, Now);

        Encoding.Unicode.GetByteCount(board.ToJsonString()).Should().BeLessThanOrEqualTo(TeamsBotCards.MaxBoardBytes);
    }
}
