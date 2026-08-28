using System.Globalization;

using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Components;

/// <summary>
/// Presentation-only formatting shared by the pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every state and severity carries a glyph and a word, never colour alone.</b> The
/// intended reader is someone woken at 3am, reading a dense table on whatever monitor is in
/// the room, possibly colour-blind, definitely not at their best. Colour is a second
/// channel here, never the only one - a row whose meaning disappears on a badly calibrated
/// screen is worse than a plain one, because it looks like it is telling you something.
/// </para>
/// <para>
/// ASCII glyphs rather than emoji: emoji render at unpredictable sizes and break the
/// monospace grid the tables depend on, and the pod's base image carries no colour font
/// anyway.
/// </para>
/// </remarks>
public static class Display
{
    public static string StateGlyph(IncidentState state) => state switch
    {
        IncidentState.Detected => "*",
        IncidentState.Triaging => "?",
        IncidentState.Suppressed => "-",
        IncidentState.Investigating => "~",
        IncidentState.AwaitingApproval => "!",
        IncidentState.Acting => ">",
        IncidentState.Verifying => "=",
        IncidentState.Resolved => "+",
        IncidentState.Escalated => "^",
        IncidentState.Expired => ".",
        _ => "?",
    };

    /// <summary>Maps to a CSS class, never to an inline colour: the palette lives in one
    /// stylesheet so a contrast fix is one edit.</summary>
    public static string StateClass(IncidentState state) => state switch
    {
        IncidentState.Detected => "st-detected",
        IncidentState.Triaging => "st-triaging",
        IncidentState.Suppressed => "st-suppressed",
        IncidentState.Investigating => "st-investigating",
        IncidentState.AwaitingApproval => "st-awaiting",
        IncidentState.Acting => "st-acting",
        IncidentState.Verifying => "st-verifying",
        IncidentState.Resolved => "st-resolved",
        IncidentState.Escalated => "st-escalated",
        IncidentState.Expired => "st-expired",
        _ => "st-detected",
    };

    public static string SeverityGlyph(Severity severity) => severity switch
    {
        Severity.Critical => "!!",
        Severity.Warning => "!",
        _ => "i",
    };

    public static string SeverityClass(Severity severity) => severity switch
    {
        Severity.Critical => "sev-critical",
        Severity.Warning => "sev-warning",
        _ => "sev-info",
    };

    public static string RiskClass(RiskTier risk) => risk switch
    {
        RiskTier.Critical => "risk-critical",
        RiskTier.High => "risk-high",
        RiskTier.Medium => "risk-medium",
        _ => "risk-low",
    };

    public static string DecisionGlyph(PolicyDecision decision) => decision switch
    {
        PolicyDecision.Allow => "+",
        PolicyDecision.RequireApproval => "!",
        _ => "x",
    };

    public static string DecisionClass(PolicyDecision decision) => decision switch
    {
        PolicyDecision.Allow => "dec-allow",
        PolicyDecision.RequireApproval => "dec-approval",
        _ => "dec-deny",
    };

    /// <summary>Compact and monotonic: 4s, 3m12s, 2h05m, 3d04h. Never "a few minutes ago" -
    /// during an incident the difference between 9 and 14 minutes is the whole question.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d{span.Hours:D2}h"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes}m{span.Seconds:D2}s"
                    : $"{span.TotalSeconds:F1}s";
    }

    public static string Millis(long ms) =>
        ms >= 1000 ? $"{ms / 1000.0:F2}s" : $"{ms}ms";

    /// <summary>UTC, always, with the offset spelled out. Local time on a page read from two
    /// timezones is how two people compare timestamps and reach different conclusions.</summary>
    public static string Timestamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string TimeOnly(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Bytes(int bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} kB",
        _ => $"{bytes} B",
    };

    public static string Tokens(long tokens) =>
        tokens >= 1000 ? $"{tokens / 1000.0:F1}k" : tokens.ToString(CultureInfo.InvariantCulture);

    /// <summary>Six decimals. A single step costs fractions of a cent and rounding it to two
    /// makes every row read $0.00, which is exactly the number nobody can act on.</summary>
    public static string Usd(decimal usd) => $"${usd.ToString("F6", CultureInfo.InvariantCulture)}";

    public static string Percent(double ratio) =>
        (ratio * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    /// <summary>Short id for a dense table. The full value is always in a title attribute -
    /// a truncated identifier you cannot recover is a dead end.</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>Only ever used with the full text one expander away.</summary>
    public static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty
        : text.Length <= max ? text
        : string.Concat(text.AsSpan(0, max), "…");
}
