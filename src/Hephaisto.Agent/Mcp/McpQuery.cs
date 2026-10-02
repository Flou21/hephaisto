using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hephaisto.Agent.Persistence;
using Hephaisto.Core.Domain;
using ModelContextProtocol;

namespace Hephaisto.Agent.Mcp;

/// <summary>What search_incidents and count_incidents narrow by, after the caller's words were read.</summary>
/// <remarks>
/// <see cref="AssignedTo"/> is already resolved: <c>me</c> became the caller's person, or was
/// refused for a shared token, before this was built. <c>nobody</c> stays, and means unassigned.
/// </remarks>
public sealed record McpIncidentFilter
{
    public const string Nobody = "nobody";

    public string? State { get; init; }

    public string? Severity { get; init; }

    public string? Cluster { get; init; }

    public string? Namespace { get; init; }

    /// <summary>Exact, or a prefix when it ends in <c>*</c>.</summary>
    public string? AlertName { get; init; }

    public string? Workload { get; init; }

    public string? Kind { get; init; }

    public string? AssignedTo { get; init; }

    public bool? Acknowledged { get; init; }

    public DateTimeOffset? OpenedAfter { get; init; }

    public DateTimeOffset? OpenedBefore { get; init; }

    /// <summary>Applies every filter to a query over incidents.</summary>
    public IQueryable<Incident> Apply(IQueryable<Incident> incidents)
    {
        ArgumentNullException.ThrowIfNull(incidents);

        incidents = ApplyState(incidents);

        if (McpQuery.Severities(Severity) is { Count: > 0 } severities)
        {
            incidents = incidents.Where(i => severities.Contains(i.Severity));
        }

        if (McpQuery.Kinds(Kind) is { Count: > 0 } kinds)
        {
            incidents = incidents.Where(i => kinds.Contains(i.Kind));
        }

        if (Trimmed(Cluster) is { } cluster)
        {
            incidents = incidents.Where(i => i.Target.Cluster == cluster);
        }

        if (Trimmed(Namespace) is { } ns)
        {
            incidents = incidents.Where(i => i.Target.Namespace == ns);
        }

        if (Trimmed(AlertName) is { } alert)
        {
            if (alert.EndsWith('*'))
            {
                var prefix = alert.TrimEnd('*');
                incidents = incidents.Where(i => i.AlertName != null && i.AlertName.StartsWith(prefix));
            }
            else
            {
                incidents = incidents.Where(i => i.AlertName == alert);
            }
        }

        if (Trimmed(Workload) is { } workload)
        {
            incidents = incidents.Where(i => i.Target.Name == workload || i.Target.OwnerName == workload);
        }

        if (Trimmed(AssignedTo) is { } assignee)
        {
            incidents = string.Equals(assignee, Nobody, StringComparison.OrdinalIgnoreCase)
                ? incidents.Where(i => i.AssignedTo == null)
                : incidents.Where(i => i.AssignedTo == assignee);
        }

        if (Acknowledged is { } acknowledged)
        {
            incidents = acknowledged
                ? incidents.Where(i => i.AcknowledgedBy != null)
                : incidents.Where(i => i.AcknowledgedBy == null);
        }

        if (OpenedAfter is { } after)
        {
            incidents = incidents.Where(i => i.OpenedAt >= after);
        }

        if (OpenedBefore is { } before)
        {
            incidents = incidents.Where(i => i.OpenedAt < before);
        }

        return incidents;
    }

    /// <summary>The filters that are set, in words, for an audit row and a confirmation.</summary>
    public string Describe()
    {
        var parts = new List<string>();

        void Add(string name, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
            {
                parts.Add($"{name} {text}");
            }
        }

        Add("state", Trimmed(State));
        Add("severity", Trimmed(Severity));
        Add("cluster", Trimmed(Cluster));
        Add("namespace", Trimmed(Namespace));
        Add("alertName", Trimmed(AlertName));
        Add("workload", Trimmed(Workload));
        Add("kind", Trimmed(Kind));
        Add("assignedTo", Trimmed(AssignedTo));
        Add("acknowledged", Acknowledged);
        Add("openedAfter", OpenedAfter?.ToString("O", CultureInfo.InvariantCulture));
        Add("openedBefore", OpenedBefore?.ToString("O", CultureInfo.InvariantCulture));

        return parts.Count == 0 ? "every open incident" : string.Join(", ", parts);
    }

    /// <summary>A short key for the filter, so a cursor cannot be replayed against another search.</summary>
    public string Key(string? text = null) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this) + "|" + text)))[..12];

    private IQueryable<Incident> ApplyState(IQueryable<Incident> incidents)
    {
        var state = Trimmed(State)?.ToLowerInvariant() ?? "any";

        switch (state)
        {
            case "open":
                return incidents.Where(i => HephaistoDbContext.OpenStates.Contains(i.State));
            case "closed":
            case "ended":
                return incidents.Where(i => !HephaistoDbContext.OpenStates.Contains(i.State) && i.State != IncidentState.Suppressed);
            case "any":
                return incidents.Where(i => i.State != IncidentState.Suppressed);
            default:
                if (Enum.TryParse<IncidentState>(State, ignoreCase: true, out var exact) && Enum.IsDefined(exact))
                {
                    return incidents.Where(i => i.State == exact);
                }

                throw new McpException(
                    $"state '{McpQuery.Echo(State)}' is not one of: open, closed, any, or an exact state ("
                    + string.Join(", ", Enum.GetNames<IncidentState>()) + ").");
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Parsing a caller's words, and the cursor that pages an answer.</summary>
public static class McpQuery
{
    public static IReadOnlyList<Severity> Severities(string? value) => Parse<Severity>(value, "severity");

    public static IReadOnlyList<SignalKind> Kinds(string? value) => Parse<SignalKind>(value, "kind");

    /// <summary>
    /// A time as an ISO 8601 instant, or as a duration back from now: <c>30m</c>, <c>24h</c>,
    /// <c>7d</c>, <c>2w</c>.
    /// </summary>
    public static DateTimeOffset? Time(string? value, DateTimeOffset now, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();

        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return at;
        }

        if (v.Length >= 2 && int.TryParse(v[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            TimeSpan? span = char.ToLowerInvariant(v[^1]) switch
            {
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(7 * n),
                _ => null,
            };

            if (span is { } s)
            {
                return now - s;
            }
        }

        throw new McpException($"{name} '{Echo(value)}' is neither an ISO 8601 time nor a duration like 30m, 24h, 7d or 2w.");
    }

    public static int Limit(int? value, int fallback, int max) => Math.Clamp(value ?? fallback, 1, max);

    /// <summary>An opaque cursor: where the last page ended, and which search it belongs to.</summary>
    public static string Cursor(string key, DateTimeOffset at, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"k:{Bind(key)}:{at.UtcTicks}:{id:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Cursor(string key, int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"o:{Bind(key)}:{offset}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The search a cursor belongs to, as twelve hex digits: nothing in it can be a separator.</summary>
    private static string Bind(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];

    public static (DateTimeOffset At, Guid Id)? ReadKeyset(string? cursor, string key)
    {
        if (Read(cursor, key) is not { } parts)
        {
            return null;
        }

        if (parts.Length == 4 && parts[0] == "k"
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && Guid.TryParseExact(parts[3], "N", out var id))
        {
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }

        throw Stale();
    }

    public static int ReadOffset(string? cursor, string key)
    {
        if (Read(cursor, key) is not { } parts)
        {
            return 0;
        }

        if (parts.Length == 3 && parts[0] == "o" && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return offset;
        }

        throw Stale();
    }

    /// <summary>What the caller sent, safe to repeat in an error: short and without markup.</summary>
    public static string Echo(string? value)
    {
        var v = Hephaisto.Core.Safety.UntrustedText.Clean(value ?? string.Empty);
        v = new string([.. v.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/' or '*' or ' ')]);
        return v.Length > 64 ? v[..64] + "..." : v;
    }

    private static string[]? Read(string? cursor, string key)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        string text;
        try
        {
            var b64 = cursor.Trim().Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
            text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            throw Stale();
        }

        var parts = text.Split(':');

        if (parts.Length < 3 || parts[1] != Bind(key))
        {
            throw Stale();
        }

        return parts;
    }

    private static McpException Stale() =>
        new("That cursor belongs to a different search, or is not one this server wrote. Repeat the search without a cursor.");

    private static List<T> Parse<T>(string? value, string name)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var result = new List<T>();

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<T>(part, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                throw new McpException($"{name} '{Echo(part)}' is not one of: {string.Join(", ", Enum.GetNames<T>())}.");
            }

            result.Add(parsed);
        }

        return result;
    }
}
