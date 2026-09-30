using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Hephaisto.Core.Abstractions;

namespace Hephaisto.Agent.Investigations.Jobs;

/// <summary>One Job investigation's standing on the investigator port.</summary>
public sealed class InvestigationJobSession
{
    public required Guid InvestigationId { get; init; }

    public required Guid IncidentId { get; init; }

    /// <summary>By name. Exactly the runner's tools, already wrapped and bound to its recorder.</summary>
    public required IReadOnlyDictionary<string, AIFunction> Tools { get; init; }

    public required InvestigationRunner.ConclusionHolder Conclusion { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    internal void CountCall() => Interlocked.Increment(ref _calls);
}

/// <summary>
/// The live Job investigations, keyed by the SHA-256 of each one's bearer token.
/// </summary>
/// <remarks>
/// <para>
/// <b>In memory, on purpose.</b> A session is exactly as durable as the in-process loop it stands in
/// for: an agent restart forgets it, the orphaned Job's next call is refused, its driver ends, and
/// <c>StrandedIncidentRequeue</c> re-runs the incident as it always has. A table would have to
/// answer what a half-run Job investigation means after a restart; this answers "nothing".
/// </para>
/// <para>
/// The token is never stored, only its hash, and it is valid for one investigation until its
/// deadline. Holding it lets a caller run that investigation's read-only tools and conclude it -
/// no more than the model inside the in-process loop can do.
/// </para>
/// </remarks>
public sealed class InvestigationJobSessions(IClock clock)
{
    private readonly ConcurrentDictionary<string, InvestigationJobSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Registers a session and returns its bearer token - the only time the token exists in clear.</summary>
    public string Open(InvestigationJobSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Sweep();

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        _sessions[Hash(token)] = session;
        return token;
    }

    public bool TryGet(string? token, out InvestigationJobSession session)
    {
        session = null!;

        if (string.IsNullOrEmpty(token) || token.Length > 256)
            return false;

        if (!_sessions.TryGetValue(Hash(token), out var found))
            return false;

        if (clock.UtcNow >= found.ExpiresAt)
        {
            _sessions.TryRemove(Hash(token), out _);
            return false;
        }

        session = found;
        return true;
    }

    public void Close(Guid investigationId)
    {
        foreach (var (key, s) in _sessions)
        {
            if (s.InvestigationId == investigationId)
                _sessions.TryRemove(key, out _);
        }
    }

    /// <summary>Sessions that have not expired: the Job slots in use.</summary>
    public int ActiveCount
    {
        get
        {
            var now = clock.UtcNow;
            return _sessions.Values.Count(s => now < s.ExpiresAt);
        }
    }

    private void Sweep()
    {
        var now = clock.UtcNow;

        foreach (var (key, s) in _sessions)
        {
            if (now >= s.ExpiresAt)
                _sessions.TryRemove(key, out _);
        }
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
