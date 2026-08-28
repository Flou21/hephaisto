using Hephaisto.Agent.Llm;
using Hephaisto.Core.Abstractions;
using Hephaisto.Core.Domain;

namespace Hephaisto.Agent.Investigations;

/// <summary>
/// Accumulates the ordered step log of one investigation in memory.
/// </summary>
/// <remarks>
/// <para>
/// In memory, not straight to Postgres, and that is the point. The steps, the findings and
/// the <c>llm_usage</c> row all have to commit together — <c>LlmBudgetService.Enlist</c>
/// stages the usage row without saving precisely so the caller can do that in one
/// transaction. Writing each step as it happens would spread one investigation across a
/// dozen commits, and a crash in the middle would leave an audit trail that says the agent
/// did half of something.
/// </para>
/// <para>
/// The single <see cref="_ordinal"/> shared between model turns and tool calls is what makes
/// the log render in the order things actually happened.
/// </para>
/// </remarks>
public sealed class InvestigationRecorder(
    Guid investigationId,
    IClock clock,
    TimeSpan blobRetention,
    Action<InvestigationRecorder, string?>? onProgress = null)
    : IInvestigationRecorder
{
    /// <summary>
    /// Fired as each step is recorded, so a live view moves while the loop runs.
    /// </summary>
    /// <remarks>
    /// It has to be here rather than in the runner's turn loop. Tool calls are executed by
    /// <c>FunctionInvokingChatClient</c> <i>inside</i> a single <c>GetResponseAsync</c>, so a
    /// turn that makes six queries reports nothing until all six are done - which for this
    /// model is minutes of a console showing "step 0". The recorder is the only place that
    /// sees each one as it happens.
    ///
    /// Optional, and swallowing nothing: it is a reporting side-channel, and a fault in it
    /// must not fail the investigation it is describing.
    /// </remarks>
    private void Progress(string? activity)
    {
        if (onProgress is null)
        {
            return;
        }

        try
        {
            onProgress(this, activity);
        }
        catch
        {
            // Deliberately ignored. See the remarks above.
        }
    }

    private readonly Lock _gate = new();
    private readonly List<InvestigationStep> _steps = [];
    private readonly List<EvidenceBlob> _blobs = [];

    private int _ordinal;

    public Guid InvestigationId => investigationId;

    public IReadOnlyList<InvestigationStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    public IReadOnlyList<EvidenceBlob> Blobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _blobs];
            }
        }
    }

    public long TotalInputTokens
    {
        get
        {
            lock (_gate)
            {
                return _steps.Sum(s => s.InputTokens);
            }
        }
    }

    public long TotalOutputTokens
    {
        get
        {
            lock (_gate)
            {
                return _steps.Sum(s => s.OutputTokens);
            }
        }
    }

    public decimal TotalCostUsd
    {
        get
        {
            lock (_gate)
            {
                return _steps.Sum(s => s.CostUsd);
            }
        }
    }

    public int ToolCallCount
    {
        get
        {
            lock (_gate)
            {
                return _steps.Count(s => s.Kind == StepKind.ToolCall);
            }
        }
    }

    public InvestigationStep RecordLlmTurn(
        string? modelId,
        long inputTokens,
        long outputTokens,
        decimal costUsd,
        long durationMs,
        string? error)
    {
        var step = new InvestigationStep
        {
            InvestigationId = investigationId,
            Kind = StepKind.LlmTurn,
            ToolServer = "internal",
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CostUsd = costUsd,
            DurationMs = durationMs,
            Failed = error is not null,
            Error = error,
            At = clock.UtcNow,
        };

        lock (_gate)
        {
            step.Ordinal = ++_ordinal;
            _steps.Add(step);
        }

        Progress(null);

        return step;
    }

    public InvestigationStep BeginToolCall(string toolName, string server, string? argumentsJson)
    {
        var step = new InvestigationStep
        {
            InvestigationId = investigationId,
            Kind = StepKind.ToolCall,
            ToolName = toolName,
            ToolServer = server,
            Arguments = argumentsJson,
            At = clock.UtcNow,
        };

        lock (_gate)
        {
            step.Ordinal = ++_ordinal;
            _steps.Add(step);
        }

        Progress(toolName);

        return step;
    }

    public void CompleteToolCall(
        InvestigationStep step,
        string resultDigest,
        string? rawResult,
        bool truncated,
        int resultBytes,
        long durationMs,
        string? error)
    {
        ArgumentNullException.ThrowIfNull(step);

        step.ResultDigest = resultDigest;
        step.ResultTruncated = truncated;
        step.ResultBytes = resultBytes;
        step.DurationMs = durationMs;
        step.Failed = error is not null;
        step.Error = error;

        // Only truncated results get a blob. An untruncated digest already *is* the whole
        // result, so storing it twice would double a table that is already the largest thing
        // in the database and whose retention is the reason blobs expire at 30 days.
        if (truncated && !string.IsNullOrEmpty(rawResult))
        {
            var now = clock.UtcNow;

            var blob = new EvidenceBlob
            {
                InvestigationId = investigationId,
                ContentType = "text/plain",
                Content = rawResult,
                CreatedAt = now,
                ExpiresAt = now + blobRetention,
            };

            step.RawBlobId = blob.Id;

            lock (_gate)
            {
                _blobs.Add(blob);
            }
        }
    }
}
