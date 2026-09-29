using System.Text.RegularExpressions;
using Hephaisto.Core;

namespace Hephaisto.Agent.Mcp;

/// <summary>
/// The MCP endpoint (#157): where a coding agent or an MCP gateway asks about incidents.
/// </summary>
/// <remarks>
/// <para>
/// Off by default, on a port of its own, and never without a credential: a token from a Secret
/// (<see cref="Tokens"/>), or the identity provider's bearer token when sign-in is on. Its
/// callers are models by construction, so what it offers is a fixed list of tools that read and
/// six that change something a person would - never an approval, a re-arm or a mode.
/// </para>
/// <para>
/// <b>A shared token stands for nobody in particular.</b> A gateway holds one token for all of its
/// users and sends it on every call, so Hephaisto cannot see the person. Such a token acts as
/// itself (<c>mcp/&lt;name&gt;</c>); a person a model names is recorded as claimed, never as the
/// actor. A <c>person</c> token is one person's and acts as its <see cref="McpTokenOptions.Subject"/>.
/// </para>
/// </remarks>
public sealed partial class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>The route. Fixed: a gateway is configured with it, and nothing gains from moving it.</summary>
    public const string Route = "/mcp";

    /// <summary>The shortest token accepted. A token is this endpoint's whole authentication.</summary>
    public const int MinimumTokenLength = 32;

    public bool Enabled { get; set; }

    /// <summary>The endpoint's own port. 8080, 8081 and 8082 are the console's, the webhook's and Teams'.</summary>
    public int Port { get; set; } = 8083;

    /// <summary>One per consumer, so each can be told apart in the audit trail and revoked alone.</summary>
    public List<McpTokenOptions> Tokens { get; set; } = [];

    /// <summary>With sign-in on, accept the identity provider's bearer tokens as well.</summary>
    public bool AcceptIdentityProviderTokens { get; set; } = true;

    /// <summary>
    /// The longest answer, in characters. Under the 40,000 a gateway in front of production cuts
    /// at, with room for its own framing - an answer cut mid-JSON is worse than none.
    /// </summary>
    public int MaxResponseChars { get; set; } = 32_000;

    /// <summary>Per token (or per signed-in user). A leaked token should not be a firehose.</summary>
    public int RequestsPerMinutePerToken { get; set; } = 120;

    /// <summary>
    /// Every reason this configuration must not start, as sentences. Empty when it may.
    /// </summary>
    /// <param name="otherPorts">The ports that are taken, by name: the console's, the webhook's, Teams'.</param>
    /// <param name="signInOn">Whether the identity provider's tokens could be accepted at all.</param>
    /// <param name="webhookToken">The webhook's token, which must not double as one of these.</param>
    public IReadOnlyList<string> Refusals(
        IReadOnlyDictionary<string, int> otherPorts,
        bool signInOn,
        string? webhookToken)
    {
        ArgumentNullException.ThrowIfNull(otherPorts);

        var refusals = new List<string>();

        if (!Enabled)
        {
            return refusals;
        }

        if (Tokens.Count == 0 && !(signInOn && AcceptIdentityProviderTokens))
        {
            refusals.Add(
                "Mcp:Enabled is true but no caller could ever get in: there is no Mcp:Tokens entry, and "
                + "sign-in is off (or Mcp:AcceptIdentityProviderTokens is false). The endpoint is never "
                + "open to nobody - add a token.");
        }

        if (Port is <= 0 or > 65535)
        {
            refusals.Add($"Mcp:Port {Port} is not a port.");
        }

        foreach (var (name, port) in otherPorts)
        {
            if (port > 0 && port == Port)
            {
                refusals.Add(
                    $"Mcp:Port {Port} is {name}'s port. The endpoint needs a port of its own, so that "
                    + "admitting a gateway to it admits nothing else.");
            }
        }

        if (MaxResponseChars < 4_000)
        {
            refusals.Add($"Mcp:MaxResponseChars {MaxResponseChars} is too small to hold one incident's overview.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var values = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < Tokens.Count; i++)
        {
            var token = Tokens[i];
            var label = string.IsNullOrWhiteSpace(token.Name) ? $"Mcp:Tokens:{i}" : $"the MCP token '{token.Name}'";

            if (!NamePattern().IsMatch(token.Name ?? string.Empty))
            {
                refusals.Add(
                    $"Mcp:Tokens:{i}:Name '{token.Name}' is not a name: lowercase letters, digits and dashes, "
                    + "at most 32 characters. It is what the audit trail calls the token.");
            }
            else if (!names.Add(token.Name ?? string.Empty))
            {
                refusals.Add($"{label} is configured twice. Each consumer gets its own token.");
            }

            if (!McpTokenOptions.Roles.Contains(token.Role, StringComparer.Ordinal))
            {
                refusals.Add($"{label} has role '{token.Role}'; it must be reader or approver.");
            }

            if (!McpTokenOptions.Kinds.Contains(token.Kind, StringComparer.Ordinal))
            {
                refusals.Add($"{label} has kind '{token.Kind}'; it must be shared or person.");
            }

            if (token.Kind == McpTokenOptions.Person && string.IsNullOrWhiteSpace(token.Subject))
            {
                refusals.Add($"{label} is a person token with no Subject: whose actions would it record?");
            }

            if (token.Kind == McpTokenOptions.Person && !string.IsNullOrWhiteSpace(token.Subject) && IsReserved(token.Subject))
            {
                refusals.Add(
                    $"{label} would act as '{token.Subject}', a name reserved for the agent, the model or a "
                    + "shared token. A token may never write in the model's name, nor pass for another token.");
            }

            var value = token.Value?.Trim();

            if (string.IsNullOrEmpty(value))
            {
                refusals.Add($"{label} has no value. It comes from the Secret named by secrets.mcp.");
                continue;
            }

            if (value.Length < MinimumTokenLength)
            {
                refusals.Add(
                    $"{label} is {value.Length} characters, shorter than {MinimumTokenLength}. A token is the "
                    + "endpoint's whole authentication; generate one with `openssl rand -hex 24`.");
            }

            if (!values.Add(value))
            {
                refusals.Add($"{label} has the same value as another token. Each consumer gets its own.");
            }

            if (!string.IsNullOrEmpty(webhookToken) && string.Equals(value, webhookToken.Trim(), StringComparison.Ordinal))
            {
                refusals.Add(
                    $"{label} is the webhook's token. Whoever may post an alert must not thereby be able "
                    + "to read every incident, and the reverse.");
            }
        }

        return refusals;
    }

    /// <summary>
    /// A name no person may act as here: the agent's and the model's own
    /// (<see cref="IncidentStateMachine.IsForbiddenGranter"/>), anything under <c>hephaisto/</c>, and
    /// anything under <c>mcp/</c>, which is how a shared token is recorded.
    /// </summary>
    internal static bool IsReserved(string actor) =>
        IncidentStateMachine.IsForbiddenGranter(actor)
        || actor.Trim().StartsWith("hephaisto/", StringComparison.OrdinalIgnoreCase)
        || actor.Trim().StartsWith(McpCaller.SharedPrefix, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$")]
    private static partial Regex NamePattern();
}

/// <summary>One consumer's credential.</summary>
public sealed class McpTokenOptions
{
    public const string Reader = "reader";
    public const string Approver = "approver";
    public const string Shared = "shared";
    public const string Person = "person";

    internal static readonly string[] Roles = [Reader, Approver];
    internal static readonly string[] Kinds = [Shared, Person];

    /// <summary>What the audit trail calls it: <c>mcp/&lt;name&gt;</c> for a shared token.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>reader</c> or <c>approver</c>. Approver adds closing and re-investigating.</summary>
    public string Role { get; set; } = Reader;

    /// <summary><c>shared</c> (a gateway's, for many people) or <c>person</c> (one person's).</summary>
    public string Kind { get; set; } = Shared;

    /// <summary>For a person token: the person, as the console would name them.</summary>
    public string? Subject { get; set; }

    /// <summary>False makes the token read-only: no write is listed or callable.</summary>
    public bool MayWrite { get; set; } = true;

    /// <summary>The secret. From the chart's Secret, never from values.</summary>
    public string? Value { get; set; }
}
