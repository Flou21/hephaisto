namespace Hephaisto.Core.Abstractions;

/// <summary>
/// Injected rather than calling DateTimeOffset.UtcNow, because half of Core's rules are
/// about time - cooldowns, quarantine, budget windows, revision age - and testing those
/// against a real clock means either sleeping or not testing them.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
