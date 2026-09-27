using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.CodeFix;

namespace Hephaisto.Agent.CodeFix;

/// <summary>
/// Polls the open code-fix attempts: collects finished Jobs, enforces deadlines, expires plans nobody
/// decided on, and cancels everything when the switch goes down.
/// </summary>
/// <remarks>
/// <para>
/// A poll rather than a watch: there are at most a handful of coder Jobs, each lives for minutes, and
/// the database is the source of truth for which ones matter. A missed watch event would leave an
/// attempt Planning forever; a missed poll is caught by the next one.
/// </para>
/// <para>
/// <b>The switch is checked first on every pass.</b> Flipping <c>codeFixMode: off</c> - or pulling the
/// agent's emergency stop - deletes running coder Jobs within one poll interval. A code-fix mode below
/// Pr cancels an implement Job the same way: the mode that allowed the approval is gone.
/// </para>
/// </remarks>
public sealed class CodeFixJobWatcher(
    IServiceScopeFactory scopes,
    IOptionsMonitor<CodeFixOptions> options,
    IClock clock,
    ILogger<CodeFixJobWatcher> logger) : BackgroundService
{
    /// <summary>An attempt still Eligible after this never got its Job - a crash between save and launch.</summary>
    private static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Code-fix watcher pass failed; retrying next interval.");
            }

            var interval = options.CurrentValue.PollInterval;

            try
            {
                await Task.Delay(interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass. Public so the integration tests can drive it without a timer.</summary>
    public async Task PassAsync(CancellationToken ct)
    {
        var open = CodeFixStates.Open.ToArray();
        List<Guid> ids;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();
            ids = await db.CodeFixAttempts
                .Where(a => open.Contains(a.State))
                .OrderBy(a => a.CreatedAt)
                .Select(a => a.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        if (ids.Count == 0)
            return;

        CodeFixModeResolution mode;

        await using (var scope = scopes.CreateAsyncScope())
        {
            mode = await scope.ServiceProvider.GetRequiredService<ICodeFixSwitch>().ResolveAsync(ct).ConfigureAwait(false);
        }

        // One scope per attempt: a failure on one must not poison the change tracker for the rest.
        foreach (var id in ids)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();
                var coordinator = scope.ServiceProvider.GetRequiredService<CodeFixCoordinator>();
                var attempt = await db.CodeFixAttempts.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);

                if (attempt is null || !attempt.State.IsOpen())
                    continue;

                if (mode.Effective == CodeFixMode.Off)
                {
                    await coordinator.CancelAsync(attempt, $"code-fix mode is Off ({mode.DecidedBy})", ct).ConfigureAwait(false);
                    continue;
                }

                if (attempt.State == CodeFixState.Implementing && mode.Effective != CodeFixMode.Pr)
                {
                    await coordinator.CancelAsync(attempt, $"code-fix mode dropped to {mode.Effective} ({mode.DecidedBy})", ct).ConfigureAwait(false);
                    continue;
                }

                switch (attempt.State)
                {
                    case CodeFixState.Planning or CodeFixState.Implementing:
                        await coordinator.CollectAsync(attempt, ct).ConfigureAwait(false);
                        break;

                    case CodeFixState.PlanReady when clock.UtcNow - (attempt.PlanReadyAt ?? attempt.CreatedAt) > options.CurrentValue.ApprovalTimeout:
                        await coordinator.ExpireAsync(attempt, ct).ConfigureAwait(false);
                        break;

                    case CodeFixState.Eligible when clock.UtcNow - attempt.CreatedAt > StrandedAfter:
                        await coordinator.RelaunchAsync(attempt, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Code-fix watcher could not process attempt {AttemptId}.", id);
            }
        }
    }
}
