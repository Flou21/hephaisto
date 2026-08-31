using Microsoft.Extensions.AI;

namespace Hephaisto.Agent.Llm;

/// <summary>
/// The provider seam. Two implementations: Gemini, and anything speaking the OpenAI wire
/// format - hosted DeepSeek or OpenRouter, or a local Ollama serving open weights.
/// </summary>
/// <remarks>
/// <para>
/// The indirection buys two things. First, swapping provider is a configuration change rather
/// than a code change - relevant because a model provider is itself a dependency that has
/// outages, and this agent is what you want working during one. Note the limit of that claim:
/// the provider and model are read when the singleton factory is constructed, so changing
/// either takes a <b>pod restart</b>, not a live ConfigMap edit. Second, and more useful day
/// to day, the whole loop can be driven by a fake in tests: nothing above this interface knows
/// whether there is a network behind it.
/// </para>
/// <para>
/// <b>A client is built per investigation, not once per process.</b> The chain has to include
/// that investigation's <see cref="InvestigationBudget"/> as its innermost link, and a
/// singleton client would share one budget across every concurrent incident. Building the
/// chain is cheap - the provider SDK client underneath it is the singleton.
/// </para>
/// </remarks>
public interface IChatClientFactory
{
    string ProviderName { get; }

    string InvestigationModelId { get; }

    string PlanningModelId { get; }

    /// <summary>
    /// Phase 1: read-only tools, budget-enforced. The returned client invokes tools itself
    /// (<c>UseFunctionInvocation</c>), so one call runs the whole tool loop.
    /// </summary>
    IChatClient CreateInvestigationClient(
        InvestigationBudget budget,
        IInvestigationRecorder? recorder = null,
        Guid? incidentId = null);

    /// <summary>
    /// Phase 2: <b>no tools at all</b>. The returned client has no function-invocation link
    /// in its chain, so a tool passed in <c>ChatOptions</c> by mistake would be declared to
    /// the model but could never be executed. That is the belt to the schema's braces: the
    /// phase that produces actions cannot reach anything.
    /// </summary>
    IChatClient CreatePlanningClient(
        InvestigationBudget budget,
        IInvestigationRecorder? recorder = null,
        Guid? incidentId = null);
}
