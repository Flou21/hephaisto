using Microsoft.AspNetCore.Http.HttpResults;

using Hephaisto.ServiceDefaults;

namespace Hephaisto.Agent.Web;

public static class VersionEndpoints
{
    /// <summary>
    /// <c>GET /api/version</c> - what is actually running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately touches nothing: no database, no Kubernetes call, no LLM. The version is
    /// read from an assembly attribute stamped at build time, so this route answers whether
    /// or not the rest of the process is working.
    /// </para>
    /// <para>
    /// That is the whole reason it is not part of <c>/api/status</c>, which queries Postgres
    /// and therefore returns 500 in exactly the situation where "which build is this?" is the
    /// first thing anyone wants to know.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/version", GetVersion).WithName("GetVersion");

        return app;
    }

    private static Ok<VersionView> GetVersion() =>
        TypedResults.Ok(new VersionView
        {
            Version = BuildInfo.Version,
            Commit = BuildInfo.Commit,
        });
}

/// <summary>The running build. See <see cref="BuildInfo"/> for why this is read from the assembly.</summary>
public sealed record VersionView
{
    /// <summary>Semantic version, without build metadata - the same string as the image tag.</summary>
    public required string Version { get; init; }

    /// <summary>The commit it was built from, or <c>unknown</c>.</summary>
    public required string Commit { get; init; }
}
