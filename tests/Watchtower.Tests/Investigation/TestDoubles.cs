using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Watchtower.Agent.Llm;
using Watchtower.Core.Abstractions;

namespace Watchtower.Tests.Investigations;

/// <summary>
/// A clock the test drives. Every budget in this layer is a window measured against
/// <see cref="IClock"/>, and testing a four-minute wall clock against a real one means either
/// sleeping for four minutes or not testing it.
/// </summary>
public sealed class TestClock(DateTimeOffset? start = null) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>, IOptions<T>
    where T : class
{
    public T CurrentValue { get; set; } = value;

    public T Value => CurrentValue;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// A scripted <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a fake at the <i>innermost</i> position rather than a fake of the whole
/// chain. The tests then exercise the real <c>FunctionInvokingChatClient</c> and the real
/// <see cref="BudgetGuardChatClient"/> - which is where the interesting behaviour lives,
/// since "one pass through the guard is one provider round trip" is a claim about how those
/// two compose. A fake of the outer client would assert nothing about that.
/// </para>
/// <para>
/// Each script entry sees the conversation so far, because a realistic conclusion has to cite
/// a step id that only exists at runtime - exactly as the real model does, by reading it out
/// of the tool result it was shown.
/// </para>
/// <para>
/// No network, ever. That is the point of the whole seam.
/// </para>
/// </remarks>
public sealed class FakeChatClient(params Func<int, IReadOnlyList<ChatMessage>, ChatResponse>[] script)
    : IChatClient
{
    private int _calls;

    public int Calls => _calls;

    public List<IReadOnlyList<ChatMessage>> Received { get; } = [];

    public List<ChatOptions?> ReceivedOptions { get; } = [];

    /// <summary>Set to make every call throw, for the fault path.</summary>
    public Exception? Throws { get; set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = messages.ToList();

        Received.Add(conversation);
        ReceivedOptions.Add(options);

        var index = _calls++;

        if (Throws is not null)
        {
            throw Throws;
        }

        var factory = index < script.Length ? script[index] : script[^1];

        return Task.FromResult(factory(index, conversation));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    // --- response builders ---

    public static ChatResponse Text(string text, long inputTokens = 100, long outputTokens = 20) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = "fake-model",
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };

    public static ChatResponse CallsTool(
        string callId,
        string toolName,
        IDictionary<string, object?> arguments,
        long inputTokens = 100,
        long outputTokens = 20) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, arguments)]))
        {
            ModelId = "fake-model",
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
        };

    /// <summary>
    /// Everything the conversation has seen, flattened. Used by scripts that need to quote a
    /// step id out of a tool result, the way the real model does.
    /// </summary>
    public static string Transcript(IReadOnlyList<ChatMessage> messages) =>
        string.Join('\n', messages.SelectMany(m => m.Contents).Select(c => c switch
        {
            TextContent text => text.Text,
            FunctionResultContent result => result.Result?.ToString() ?? string.Empty,
            _ => string.Empty,
        }));
}

/// <summary>
/// Builds the same chains <see cref="GeminiChatClientFactory"/> does, over a fake inner
/// client. Same link order, so what the tests exercise is the shape that actually ships.
/// </summary>
public sealed class FakeChatClientFactory(
    LlmPricing pricing,
    FakeChatClient investigation,
    FakeChatClient? planning = null) : IChatClientFactory
{
    public string ProviderName => "fake";

    public string InvestigationModelId => "fake-model";

    public string PlanningModelId => "fake-model";

    public FakeChatClient Investigation => investigation;

    public FakeChatClient Planning => planning ?? investigation;

    public IChatClient CreateInvestigationClient(
        InvestigationBudget budget,
        IInvestigationRecorder? recorder = null,
        Guid? incidentId = null) =>
        new ChatClientBuilder(investigation)
            .UseFunctionInvocation()
            .Use(inner => new BudgetGuardChatClient(inner, budget, pricing, "fake-model", recorder, incidentId))
            .Build();

    public IChatClient CreatePlanningClient(
        InvestigationBudget budget,
        IInvestigationRecorder? recorder = null,
        Guid? incidentId = null) =>
        // No UseFunctionInvocation link: phase 2 is structurally incapable of calling a tool.
        new ChatClientBuilder(Planning)
            .Use(inner => new BudgetGuardChatClient(inner, budget, pricing, "fake-model", recorder, incidentId))
            .Build();
}
