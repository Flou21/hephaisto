using Microsoft.EntityFrameworkCore;
using Npgsql;

using Hephaisto.Core.Domain;

namespace Hephaisto.IntegrationTests;

/// <summary>
/// <c>work_items</c> in a real Postgres: the migration that makes it, and the partial unique
/// index that is the last word on "at most one taken work item per issue".
/// </summary>
/// <remarks>
/// The poller reads before it writes, and two passes - or one pass on either side of a restart -
/// read the same nothing. What keeps that from becoming two rows is not code that can be unit
/// tested: it is an index with a WHERE clause, and only Postgres can say what it lets through.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class WorkItemPersistenceTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private const string Repo = "octo/shop";
    private const string Other = "octo/api";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Postgres_lets_one_taken_work_item_per_issue_through_and_any_number_of_ended_ones()
    {
        await pg.ResetAsync();

        await using (var db = pg.CreateContext())
        {
            db.WorkItems.AddRange(
                Item(Repo, 7, WorkItemState.Cancelled),
                Item(Repo, 7, WorkItemState.Cancelled),
                Item(Repo, 7, WorkItemState.Done),
                Item(Repo, 7, WorkItemState.Taken),
                Item(Repo, 8, WorkItemState.Taken),
                Item(Other, 7, WorkItemState.Taken));

            await db.SaveChangesAsync(Ct);
        }

        await using (var db = pg.CreateContext())
        {
            db.WorkItems.Add(Item(Repo, 7, WorkItemState.Taken));

            var act = () => db.SaveChangesAsync(Ct);

            (await act.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }

        await using (var db = pg.CreateContext())
        {
            // And an ended one cannot be made taken again beside the one that is.
            var ended = await db.WorkItems.FirstAsync(w => w.Repository == Repo && w.Number == 7 && w.State == WorkItemState.Done, Ct);
            ended.State = WorkItemState.Taken;

            var act = () => db.SaveChangesAsync(Ct);

            await act.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task The_migration_made_the_table_and_the_index_is_the_partial_one()
    {
        await using var db = pg.CreateContext();

        (await db.Database.GetAppliedMigrationsAsync(Ct)).Should().Contain(m => m.EndsWith("_WorkItems", StringComparison.Ordinal));
        (await db.Database.GetPendingMigrationsAsync(Ct)).Should().BeEmpty();

        var index = await db.Database
            .SqlQuery<string>($"""select indexdef as "Value" from pg_indexes where indexname = 'ux_work_items_one_taken_per_issue'""")
            .SingleAsync(Ct);

        index.Should().StartWith("CREATE UNIQUE INDEX")
            .And.Contain("(source, repository, number)")
            .And.Contain("WHERE (state = 'Taken'::text)");

        var columns = await db.Database
            .SqlQuery<string>($"""select column_name::text as "Value" from information_schema.columns where table_name = 'work_items'""")
            .ToListAsync(Ct);

        columns.Should().BeEquivalentTo(
        [
            "id", "source", "repository", "number", "node_id", "url", "title", "type", "author_login", "author_id",
            "body", "labels", "state", "state_reason", "taken_at", "closed_at", "status_comment_id", "updated_at",
        ]);
    }

    [Fact]
    public async Task A_work_item_comes_back_as_it_was_written()
    {
        await pg.ResetAsync();

        var written = Item(Repo, 7, WorkItemState.Cancelled);
        written.Type = "Bug";
        written.Labels = ["bug", "area:checkout"];
        written.StatusCommentId = 1759743015123L;
        written.Body = "line one\nline two with \"quotes\" and <tags>";

        await using (var db = pg.CreateContext())
        {
            db.WorkItems.Add(written);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = pg.CreateContext())
        {
            var read = await db.WorkItems.AsNoTracking().SingleAsync(Ct);

            read.Should().BeEquivalentTo(written);
            read.StatusCommentId.Should().Be(1759743015123L, "GitHub's comment ids are beyond 32 bits");

            // Stored as its name: a row that needs the enum's numbering to be read is a puzzle.
            (await db.Database.SqlQuery<string>($"""select state as "Value" from work_items""").SingleAsync(Ct)).Should().Be("Cancelled");
        }
    }

    private static WorkItem Item(string repository, int number, WorkItemState state) => new()
    {
        Repository = repository,
        Number = number,
        NodeId = $"I_{number}",
        Url = $"https://github.com/{repository}/issues/{number}",
        Title = $"issue {number}",
        AuthorLogin = "reporter",
        AuthorId = 3003,
        Body = $"body of {number}",
        State = state,
        StateReason = state == WorkItemState.Cancelled ? "the issue was closed" : null,
        TakenAt = Now,
        ClosedAt = state == WorkItemState.Taken ? null : Now,
        UpdatedAt = Now,
    };
}
