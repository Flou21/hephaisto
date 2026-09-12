namespace Hephaisto.Agent.Options;

/// <summary>
/// OIDC for the console and the API (backlog #110).
/// </summary>
/// <remarks>
/// <para>
/// <b>Before this there was no authentication of any kind.</b> No <c>AddAuthentication</c>, no
/// <c>RequireAuthorization</c>. Anyone who could reach port 8080 could read every incident -
/// which includes pod logs and ConfigMap contents the agent gathered from every namespace - and
/// could call <c>POST /api/incidents/{id}/actions/{actionId}/approve</c>.
/// </para>
/// <para>
/// <b>It also makes the audit trail mean something.</b> The integrity story was already careful:
/// the app serves as a non-owner Postgres role holding INSERT but not UPDATE or DELETE on
/// <c>audit_events</c>, enforced in the migration. And then <c>actor</c> recorded whatever name
/// somebody typed into a form. The immutability was real; the identity was not.
/// </para>
/// </remarks>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Off by default, and that is not a soft default - it is the only one that can be.
    /// </summary>
    /// <remarks>
    /// The eval harness, a developer running <c>dotnet run</c>, and every install that predates
    /// this all have no IdP. Defaulting to on would make each of them fail to start. Turning it
    /// on is a deliberate act with an authority to point at.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>The OIDC issuer, e.g. <c>https://keycloak.example/realms/hephaisto</c>.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>
    /// Read from a Secret, never a chart value - see <c>secrets.auth</c>.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The claim carrying roles. Keycloak puts them in <c>realm_access.roles</c> by default,
    /// which is not the standard <c>role</c> claim, so this is configurable rather than assumed.
    /// </summary>
    public string RolesClaim { get; set; } = "roles";

    /// <summary>
    /// The role required to read the console at all. Empty means any authenticated user.
    /// </summary>
    public string? ReaderRole { get; set; }

    /// <summary>
    /// The role required to approve an action, close an incident, or change the mode.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="ReaderRole"/> deliberately.</b> "Anyone who can log in may
    /// authorise a change to the cluster" is not a default worth shipping, and the set of people
    /// who should watch an incident is much larger than the set who should approve a
    /// <c>kubectl delete</c>. Empty means any authenticated user, which is the permissive choice
    /// and is logged as such at startup.
    /// </remarks>
    public string? ApproverRole { get; set; }

    /// <summary>
    /// Whether the IdP's metadata must be fetched over HTTPS. Default true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An explicit option rather than an inference.</b> An in-cluster issuer such as
    /// <c>http://keycloak.infra-auth:8080/realms/x</c> is plain HTTP and perfectly reasonable -
    /// the traffic never leaves the cluster - so this cannot simply be hardcoded true. It is
    /// equally not something to relax automatically when the authority happens to start with
    /// <c>http://</c>: that would silently accept a downgraded authority on an install that
    /// meant to use a public HTTPS one, and the whole point of this setting is that somebody
    /// decided.
    /// </para>
    /// <para>
    /// Turning it off is logged at startup, naming what it means: the discovery document and the
    /// signing keys are fetched in clear, so anything on that path can choose who the agent
    /// believes.
    /// </para>
    /// </remarks>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Whether <see cref="Enabled"/> is usable. An authority is the one thing with no default.
    /// </summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Authority);
}
