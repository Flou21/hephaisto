using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaisto.Agent.Options;
using Hephaisto.Agent.Persistence;
using Hephaisto.Agent.Web;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Notifications;
using Hephaisto.Core.Safety;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Hephaisto.Agent.Notifications.TeamsBot;

/// <summary>
/// What a Bot Framework token has to be before a click is believed.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft's rules for a request to a bot, and nothing looser: issued by
/// <c>https://api.botframework.com</c>, for this bot's App ID, in date, signed by a key from the
/// published set - and that key <b>endorsed for the channel</b> the request claims to come from.
/// The endorsement is the check that is easy to leave out, because every generic JWT library
/// passes a token without it: the key set is shared by every Bot Framework channel, and a key
/// endorsed only for another channel must not be able to click a Teams button.
/// </para>
/// <para>
/// Two more rules need the request body and are in <see cref="TeamsBotActionHandler"/>: the
/// token's <c>serviceurl</c> claim must name the service the activity says it came from, and the
/// activity's tenant must be this bot's.
/// </para>
/// </remarks>
public static class BotFrameworkTokens
{
    /// <summary>The authentication scheme. Its own, never the console's and never a default.</summary>
    public const string Scheme = "BotFramework";

    /// <summary>The authorization policy the route carries.</summary>
    public const string Policy = "teams.bot";

    /// <summary>The claim naming the Bot Connector service the token was issued for.</summary>
    public const string ServiceUrlClaim = "serviceurl";

    public static TokenValidationParameters Parameters(string appId, string issuer) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = appId,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],

        // Microsoft's own guidance for Bot Framework tokens: five minutes of clock skew.
        ClockSkew = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Whether the key that signed the token is endorsed for <paramref name="channel"/>.
    /// </summary>
    /// <remarks>
    /// A key without an <c>endorsements</c> list is endorsed for nothing, and a token without a
    /// key id cannot be matched to one: both are refusals, not defaults.
    /// </remarks>
    public static bool IsEndorsed(JsonWebKeySet? keys, string? kid, string channel)
    {
        if (keys is null || string.IsNullOrEmpty(kid))
        {
            return false;
        }

        return keys.Keys
            .Where(k => string.Equals(k.Kid, kid, StringComparison.Ordinal))
            .Any(k => Endorsements(k).Contains(channel, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The channels a key is endorsed for, whatever shape the key document parsed into.</summary>
    internal static IReadOnlyList<string> Endorsements(JsonWebKey key)
    {
        if (!key.AdditionalData.TryGetValue("endorsements", out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            string single => [single],
            JsonElement { ValueKind: JsonValueKind.Array } array =>
                [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)],
            JsonElement { ValueKind: JsonValueKind.String } text => [text.GetString()!],
            System.Collections.IEnumerable many => [.. many.Cast<object?>().Select(o => o?.ToString()).OfType<string>()],
            _ => [],
        };
    }
}

/// <summary>
/// Who is in the team, by Microsoft Entra object id. A click is only believed from a member.
/// </summary>
public interface ITeamsMemberDirectory
{
    /// <summary>
    /// The member with that object id. A successful result with a null value means Teams
    /// answered and nobody in the team has it; an unsuccessful one means nobody could tell.
    /// </summary>
    Task<TeamsBotResult<TeamsMember>> FindAsync(string aadObjectId, CancellationToken ct);
}

/// <summary>
/// The team's roster, read through the bot and remembered briefly.
/// </summary>
/// <remarks>
/// Only a member found is remembered, for <see cref="TeamsBotActionsOptions.MembershipCacheDuration"/>:
/// that bounds how long somebody removed from the team can still click. A miss is never
/// remembered, so somebody just added can click at once.
/// </remarks>
public sealed class TeamsMemberDirectory(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptionsMonitor<NotificationOptions> options) : ITeamsMemberDirectory
{
    private readonly ConcurrentDictionary<string, (TeamsMember Member, DateTimeOffset Until)> known =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<TeamsBotResult<TeamsMember>> FindAsync(string aadObjectId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aadObjectId);

        var now = clock.UtcNow;

        if (known.TryGetValue(aadObjectId, out var hit) && now < hit.Until)
        {
            return new TeamsBotResult<TeamsMember>(System.Net.HttpStatusCode.OK, hit.Member, null);
        }

        await using var scope = scopes.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<ITeamsBotClient>();

        var found = await client.FindMemberByObjectIdAsync(aadObjectId, ct).ConfigureAwait(false);

        if (found.Ok && found.Value is { } member)
        {
            known[aadObjectId] = (member, now + options.CurrentValue.TeamsBot.Actions.MembershipCacheDuration);
        }
        else
        {
            known.TryRemove(aadObjectId, out _);
        }

        return found;
    }
}

/// <summary>What a click can change, and the card that shows the change.</summary>
public interface ITeamsActionTarget
{
    Task<LifecycleResult> AcknowledgeAsync(Guid incidentId, string actor, CancellationToken ct);

    Task<LifecycleResult> AssignToAsync(Guid incidentId, string actor, CancellationToken ct);

    Task<ReinvestigateResult> ReinvestigateAsync(Guid incidentId, string actor, CancellationToken ct);

    Task<LifecycleResult> CloseAsync(Guid incidentId, string actor, string reason, CancellationToken ct);

    /// <summary>The alert card as it is now, or null when the incident no longer exists.</summary>
    Task<JsonObject?> CardAsync(Guid incidentId, CancellationToken ct);
}

/// <summary>
/// The console's own calls, so a click and a console button cannot disagree about what
/// acknowledging, assigning, re-investigating or closing means - the same forbidden-actor rule,
/// the same state machine, the same audit row. Nothing here writes state itself.
/// </summary>
public sealed class TeamsActionTarget(
    IncidentQueries queries,
    IServiceScopeFactory scopes,
    IOptionsMonitor<NotificationOptions> options) : ITeamsActionTarget
{
    public Task<LifecycleResult> AcknowledgeAsync(Guid incidentId, string actor, CancellationToken ct) =>
        queries.AcknowledgeIncidentAsync(incidentId, actor, ct);

    public Task<LifecycleResult> AssignToAsync(Guid incidentId, string actor, CancellationToken ct) =>
        queries.AssignIncidentAsync(incidentId, actor, actor, ct);

    public Task<ReinvestigateResult> ReinvestigateAsync(Guid incidentId, string actor, CancellationToken ct) =>
        queries.RequestReinvestigationAsync(incidentId, actor, ct);

    public Task<LifecycleResult> CloseAsync(Guid incidentId, string actor, string reason, CancellationToken ct) =>
        queries.CloseIncidentAsync(incidentId, actor, reason, ct);

    public async Task<JsonObject?> CardAsync(Guid incidentId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var o = options.CurrentValue;
        var incidents = sp.GetRequiredService<TeamsBotIncidents>();
        var db = sp.GetRequiredService<HephaistoDbContext>();

        var known = await incidents.ByIdAsync([incidentId], ct).ConfigureAwait(false);

        if (!known.TryGetValue(incidentId, out var incident))
        {
            return null;
        }

        var board = await db.TeamsBotMessages.AsNoTracking()
            .Where(m => m.Kind == TeamsBotMessageKind.Board
                && m.State == TeamsBotMessageState.Live
                && m.Recipient == (o.TeamsBot.ChannelId ?? string.Empty))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.ActivityId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var activity = TeamsBotCards.Alert(incident, TeamsBotLinks.Alert(o, board));

        return activity["attachments"]?[0]?["content"]?.DeepClone() as JsonObject;
    }
}

/// <summary>What the route answers: an HTTP status, and the body when there is one.</summary>
public sealed record TeamsActionAnswer(int Status, JsonNode? Body, string? Refusal = null)
{
    public static TeamsActionAnswer Refused(int status, string why) => new(status, null, why);
}

/// <summary>
/// A click on an alert's button, from a token already validated to the Bot Framework's rules.
/// </summary>
/// <remarks>
/// <para>
/// Everything here fails closed and changes nothing on the way: a token for another service, an
/// activity from another tenant, a clicker who is not in the team, or a roster nobody could read
/// is a refusal before any incident is touched. Each refusal is one log line at Warning, because
/// a click that failed silently is a person who thinks they acknowledged something.
/// </para>
/// <para>
/// <b>The actor is the roster's, not the click's.</b> An invoke activity carries a display name
/// the sender chose; the only thing believed from it is the object id, and that only because the
/// token says Microsoft sent it. The name recorded is what the team's member list says.
/// </para>
/// <para>
/// <b>Who may close is decided here, not on the card.</b> A card is the same for everybody who
/// reads it, so the Close button is in front of people who may not press it. The object id that
/// was just found in the roster is looked up in <see cref="TeamsBotActionsOptions.Approvers"/>;
/// somebody who is not on it is told so in words, because a click that failed silently is a
/// person who thinks they closed something.
/// </para>
/// </remarks>
public sealed class TeamsBotActionHandler(
    IOptionsMonitor<NotificationOptions> options,
    ITeamsMemberDirectory members,
    ITeamsActionTarget target,
    ILogger<TeamsBotActionHandler> logger)
{
    private const string InvokeName = "adaptiveCard/action";

    public async Task<TeamsActionAnswer> HandleAsync(ClaimsPrincipal caller, JsonObject? activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (activity is null)
        {
            return Refuse(400, "the request carried no activity");
        }

        var bot = options.CurrentValue.TeamsBot;

        // The token was issued for one Bot Connector service; the activity must say it came from
        // that one. Otherwise a token lifted from one service's traffic answers for another.
        var claimed = caller.FindFirst(BotFrameworkTokens.ServiceUrlClaim)?.Value;
        var serviceUrl = Text(activity, "serviceUrl");

        if (!SameUrl(claimed, serviceUrl))
        {
            return Refuse(403, "the token's serviceurl claim does not name the activity's serviceUrl");
        }

        var tenant = activity["channelData"]?["tenant"]?["id"] is JsonValue t && t.TryGetValue<string>(out var id) ? id : null;

        if (string.IsNullOrWhiteSpace(bot.TenantId)
            || !string.Equals(tenant, bot.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(403, "the activity is from another tenant");
        }

        if (!string.Equals(Text(activity, "type"), "invoke", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Text(activity, "name"), InvokeName, StringComparison.Ordinal))
        {
            // Teams also tells a bot it was installed, or that somebody wrote to it. None of that
            // is a click, and none of it changes anything here.
            return new TeamsActionAnswer(200, null);
        }

        var objectId = activity["from"]?["aadObjectId"] is JsonValue o && o.TryGetValue<string>(out var oid) ? oid : null;

        if (string.IsNullOrWhiteSpace(objectId))
        {
            return Refuse(403, "the click names no Microsoft Entra object id");
        }

        var member = await members.FindAsync(objectId, ct).ConfigureAwait(false);

        if (!member.Ok)
        {
            // Fail closed. Not knowing whether somebody is in the team is not a yes.
            return Refuse(503, $"the team's member list could not be read: {member.Detail}");
        }

        if (member.Value is not { } person)
        {
            return Refuse(403, "the clicker is not a member of the team");
        }

        var action = activity["value"]?["action"];
        var verb = action?["verb"] is JsonValue v && v.TryGetValue<string>(out var verbText) ? verbText : null;
        var incident = action?["data"]?["incidentId"] is JsonValue i && i.TryGetValue<string>(out var idText)
            && Guid.TryParse(idText, out var parsed) ? parsed : (Guid?)null;

        if (verb is null || !TeamsBotVerbs.All.Contains(verb, StringComparer.Ordinal))
        {
            return Error($"'{verb}' is not something a button here can do");
        }

        if (incident is not { } incidentId)
        {
            return Error("the button named no incident");
        }

        // The approver role, for the verbs that carry it in the console. Asked of the object id
        // the roster just vouched for, and before anything the click says is read.
        if (TeamsBotVerbs.Approver.Contains(verb, StringComparer.Ordinal) && !bot.Actions.IsApprover(objectId))
        {
            return Decline(
                person.Actor,
                verb,
                incidentId,
                $"{Doing(verb)} needs the approver role, and you are not in notifications.teamsBot.actions.approvers.");
        }

        switch (verb)
        {
            case TeamsBotVerbs.Acknowledge:
                return await LifecycleAsync(
                    person.Actor,
                    verb,
                    incidentId,
                    await target.AcknowledgeAsync(incidentId, person.Actor, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);

            case TeamsBotVerbs.AssignToMe:
                return await LifecycleAsync(
                    person.Actor,
                    verb,
                    incidentId,
                    await target.AssignToAsync(incidentId, person.Actor, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);

            case TeamsBotVerbs.Reinvestigate:
                return await ReinvestigateAsync(person.Actor, incidentId, ct).ConfigureAwait(false);

            case TeamsBotVerbs.Close:
                // What the person typed into the card. A person wrote it, in a client this process
                // does not control, so it is cleaned like any other text from outside and cut to
                // what the card would have allowed.
                var reason = UntrustedText.Clean(Text(action?["data"], TeamsBotVerbs.ReasonInput) ?? string.Empty).Trim();

                if (reason.Length == 0)
                {
                    return Decline(person.Actor, verb, incidentId, "Give a reason: it is written to the incident.");
                }

                if (reason.Length > TeamsBotCards.MaxReasonLength)
                {
                    reason = reason[..TeamsBotCards.MaxReasonLength];
                }

                return await LifecycleAsync(
                    person.Actor,
                    verb,
                    incidentId,
                    await target.CloseAsync(incidentId, person.Actor, reason, ct).ConfigureAwait(false),
                    ct).ConfigureAwait(false);

            default:
                // TeamsBotVerbs.All and this switch are the same list; a test holds them together.
                return Error($"'{verb}' is not something a button here can do");
        }
    }

    private async Task<TeamsActionAnswer> LifecycleAsync(
        string actor,
        string verb,
        Guid incidentId,
        LifecycleResult result,
        CancellationToken ct)
    {
        logger.LogInformation(
            "A Teams click by {Actor}: {Verb} on incident {IncidentId} - {Outcome}.",
            actor, verb, incidentId, result.Outcome);

        switch (result.Outcome)
        {
            case LifecycleOutcome.Applied:
                return await CardOrMessageAsync(incidentId, "Done.", ct).ConfigureAwait(false);

            case LifecycleOutcome.NotFound:
                return Message("That incident no longer exists.");

            case LifecycleOutcome.IllegalState:
                return await CardOrMessageAsync(
                    incidentId,
                    $"Nothing changed: {result.Detail ?? "the incident is not in a state this applies to"}",
                    ct).ConfigureAwait(false);

            default:
                return Refuse(403, result.Detail ?? "the actor may not do this");
        }
    }

    /// <summary>
    /// Another investigation, through the console's own door: its named-requester rule, its
    /// kill-switch check and its queue. Each way it can decline is said in words - the same reason
    /// the console gives each its own status code.
    /// </summary>
    private async Task<TeamsActionAnswer> ReinvestigateAsync(string actor, Guid incidentId, CancellationToken ct)
    {
        var result = await target.ReinvestigateAsync(incidentId, actor, ct).ConfigureAwait(false);

        logger.LogInformation(
            "A Teams click by {Actor}: {Verb} on incident {IncidentId} - {Outcome}.",
            actor, TeamsBotVerbs.Reinvestigate, incidentId, result.Outcome);

        switch (result.Outcome)
        {
            case ReinvestigateOutcome.Queued:
                return await CardOrMessageAsync(incidentId, "Another investigation is queued.", ct).ConfigureAwait(false);

            case ReinvestigateOutcome.NotFound:
                return Message("That incident no longer exists.");

            case ReinvestigateOutcome.AlreadyRunning:
                return Message($"Nothing changed: {result.Detail ?? "an investigation is already running for this incident."}");

            case ReinvestigateOutcome.Disabled:
                return Message($"Nothing changed, the agent is switched off: {result.Detail ?? "its mode is Off."}");

            case ReinvestigateOutcome.IllegalState:
                return Message($"Nothing changed: {result.Detail ?? "the incident is not in a state another investigation can start from."}");

            case ReinvestigateOutcome.QueueFull:
                // Not "nothing changed": the incident is marked Investigating and nothing is
                // working it yet. Saying otherwise would be the one untrue answer here.
                return Message($"Not started yet: {result.Detail ?? "the investigation queue is full."}");

            default:
                return Refuse(403, result.Detail ?? "the actor may not do this");
        }
    }

    private async Task<TeamsActionAnswer> CardOrMessageAsync(Guid incidentId, string fallback, CancellationToken ct)
    {
        var card = await target.CardAsync(incidentId, ct).ConfigureAwait(false);

        return card is null
            ? Message(fallback)
            : new TeamsActionAnswer(200, new JsonObject
            {
                ["statusCode"] = 200,
                ["type"] = "application/vnd.microsoft.card.adaptive",
                ["value"] = card,
            });
    }

    private static TeamsActionAnswer Message(string text) => new(200, new JsonObject
    {
        ["statusCode"] = 200,
        ["type"] = "application/vnd.microsoft.activity.message",
        ["value"] = text,
    });

    private TeamsActionAnswer Error(string message)
    {
        logger.LogWarning("A Teams click was refused: {Reason}.", message);

        return new TeamsActionAnswer(200, new JsonObject
        {
            ["statusCode"] = 400,
            ["type"] = "application/vnd.microsoft.error",
            ["value"] = new JsonObject { ["code"] = "BadRequest", ["message"] = message },
        });
    }

    /// <summary>
    /// A refusal the person reads: a member of the team asked for something they may not have, or
    /// left out what it needs. Answered in words, logged like every other refusal, nothing changed.
    /// </summary>
    private TeamsActionAnswer Decline(string actor, string verb, Guid incidentId, string why)
    {
        logger.LogWarning(
            "A Teams click by {Actor} was refused: {Verb} on incident {IncidentId} - {Reason} Nothing was changed.",
            actor, verb, incidentId, why);

        return Message($"{why} Nothing was changed.");
    }

    private static string Doing(string verb) => verb switch
    {
        TeamsBotVerbs.Close => "Closing",
        _ => $"'{verb}'",
    };

    private TeamsActionAnswer Refuse(int status, string why)
    {
        logger.LogWarning("A Teams click was refused ({Status}): {Reason}. Nothing was changed.", status, why);

        return TeamsActionAnswer.Refused(status, why);
    }

    private static string? Text(JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool SameUrl(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Registration, the route and the port guard.</summary>
public static class TeamsBotActionsExtensions
{
    /// <summary>The route Microsoft is told to call: the bot's messaging endpoint.</summary>
    public const string Route = "/api/teams/messages";

    /// <summary>A click is a small JSON document. Anything larger is not one.</summary>
    private const int MaxBodyBytes = 256 * 1024;

    public static IServiceCollection AddHephaistoTeamsBotActions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var notifications = configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()
            ?? new NotificationOptions();
        var bot = notifications.TeamsBot;
        var actions = bot.Actions;

        if (!actions.Enabled)
        {
            return services;
        }

        // Refused at startup rather than at the first click, which would otherwise be the first
        // anybody learned of it - somebody pressing a button that does nothing.
        if (!bot.IsConfigured)
        {
            throw new InvalidOperationException(
                "Notifications:TeamsBot:Actions:Enabled is true but the Teams bot is not configured "
                + "(TenantId, AppId, ClientSecret, ChannelId). There is no bot for a button to belong to.");
        }

        var web = configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();

        if (actions.Port is <= 0 or > 65535
            || actions.Port == web.MainPort
            || (web.WebhookPort > 0 && actions.Port == web.WebhookPort))
        {
            throw new InvalidOperationException(
                $"Notifications:TeamsBot:Actions:Port is {actions.Port}. It must be a port of its own - "
                + $"not the console's ({web.MainPort}) and not the webhook's ({web.WebhookPort}) - so that "
                + "exposing it to Microsoft exposes nothing else.");
        }

        // An approver is a Microsoft Entra object id, in the one form a click carries it. Anything
        // else - a mail address, a display name, a GUID in braces - would never match, and a list
        // that silently names nobody reads exactly like one that works until somebody has to close
        // something.
        foreach (var approver in actions.Approvers)
        {
            if (!Guid.TryParseExact(approver?.Trim(), "D", out _))
            {
                throw new InvalidOperationException(
                    $"Notifications:TeamsBot:Actions:Approvers names '{approver}', which is not a Microsoft Entra "
                    + "object id (a GUID, xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx). A click is matched by its "
                    + "from.aadObjectId, so an address or a name here would map nobody to the approver role.");
            }
        }

        // With auth off this is the only scheme, and ASP.NET Core would otherwise make a lone
        // scheme the default for every request - so a console request carrying any bearer token
        // would be run past the Bot Framework's key set. It is only ever meant for one route.
        AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);

        services.AddAuthentication()
            .AddJwtBearer(BotFrameworkTokens.Scheme, jwt =>
            {
                jwt.MetadataAddress = actions.OpenIdMetadataUrl;
                jwt.RequireHttpsMetadata = actions.RequireHttpsMetadata;

                // Keep the claim names as Microsoft wrote them: `serviceurl` is read below by name.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = BotFrameworkTokens.Parameters(bot.AppId!, actions.Issuer);

                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var kid = (context.SecurityToken as JsonWebToken)?.Kid;
                        var manager = context.Options.ConfigurationManager;
                        var config = manager is null
                            ? null
                            : await manager.GetConfigurationAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);

                        if (!BotFrameworkTokens.IsEndorsed(config?.JsonWebKeySet, kid, actions.Channel))
                        {
                            context.Fail($"the signing key '{kid}' is not endorsed for the {actions.Channel} channel");
                        }
                    },
                    OnAuthenticationFailed = context =>
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<TeamsBotActionHandler>>()
                            .LogWarning(
                                "A Teams click was refused (401): {Reason}. Nothing was changed.",
                                context.Exception.Message);

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(BotFrameworkTokens.Policy, policy => policy
                .AddAuthenticationSchemes(BotFrameworkTokens.Scheme)
                .RequireAuthenticatedUser());

        services.TryAddSingleton<ITeamsMemberDirectory, TeamsMemberDirectory>();
        services.TryAddSingleton<ITeamsActionTarget, TeamsActionTarget>();
        services.TryAddSingleton<TeamsBotActionHandler>();

        return services;
    }

    /// <summary>
    /// On the actions port, answer the one route and nothing else - not the console, not the
    /// API, not a static file. Before anything else in the pipeline, so nothing can be reached
    /// around it.
    /// </summary>
    public static WebApplication UseTeamsBotActionsPort(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var actions = app.Services.GetRequiredService<IOptions<NotificationOptions>>().Value.TeamsBot.Actions;

        if (!actions.Enabled)
        {
            return app;
        }

        var port = actions.Port;

        app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort == port
                && !(HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals(Route, StringComparison.Ordinal)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        return app;
    }

    internal static void MapTeamsBotActions(this IEndpointRouteBuilder app, int port)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("TeamsBotMessages")
            .RequireAuthorization(BotFrameworkTokens.Policy)
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.Connection.LocalPort == port ? await next(context) : Results.NotFound());
    }

    private static async Task<IResult> HandleAsync(HttpContext http, TeamsBotActionHandler handler, CancellationToken ct)
    {
        if (http.Request.ContentLength is > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Enforced by the server while reading, so a body without a Content-Length is held to it too.
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        JsonObject? activity;

        try
        {
            activity = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct).ConfigureAwait(false) as JsonObject;
        }
        catch (JsonException)
        {
            activity = null;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var answer = await handler.HandleAsync(http.User, activity, ct).ConfigureAwait(false);

        return answer.Body is null
            ? Results.StatusCode(answer.Status)
            : Results.Json(answer.Body, statusCode: answer.Status);
    }
}
