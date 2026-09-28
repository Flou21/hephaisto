using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hephaisto.Agent.Persistence;

/// <summary>
/// <c>/readyz</c> says ready only when the database answers (#136).
/// </summary>
/// <remarks>
/// <para>
/// Readiness, not liveness: a database blip must not restart the pod that is trying to report it,
/// and <c>/healthz</c> stays "the process is up". But an agent that cannot write cannot take an
/// alert either, and until v0.10.0 <c>/readyz</c> said ready regardless - so the Service kept
/// routing Alertmanager's deliveries to a pod that answered 200 and dropped them. Unready, the pod
/// leaves the Service, a delivery is refused, and Alertmanager retries it.
/// </para>
/// <para>
/// Two seconds. A readiness probe that waits out a hung connection is a probe that times out, and
/// the kubelet reads a timeout as the answer anyway.
/// </para>
/// </remarks>
public sealed class PostgresReadinessCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HephaistoDbContext>();

            return await db.Database.CanConnectAsync(timeout.Token).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("the database does not answer");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("the database does not answer", ex);
        }
    }
}
