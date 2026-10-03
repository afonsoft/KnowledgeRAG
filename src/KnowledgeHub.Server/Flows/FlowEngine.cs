using System.Diagnostics;
using System.Text.Json.Nodes;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Server.Flows;

/// <summary>Handler for one flow step type.</summary>
public interface IFlowStepHandler
{
    /// <summary>Step type name (tool | llm | http | knowledge | condition |
    /// foreach | transform | output | fail).</summary>
    string Type { get; }

    /// <summary>Execute the step; the returned node becomes
    /// <c>steps.&lt;id&gt;.output</c>.</summary>
    Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct);
}

/// <summary>Sequential executor for UI-defined agent flows.</summary>
public interface IFlowEngine
{
    /// <summary>Validate + bind inputs, then execute the step list. Never throws
    /// for step failures — they land in the returned trace. A
    /// <paramref name="resume"/> state rebuilds the context of a suspended run:
    /// steps whose ids are already in StepOutputs are skipped.</summary>
    Task<FlowRunResult> ExecuteAsync(
        FlowDefinitionDto definition,
        JsonObject? inputs,
        IServiceProvider services,
        Func<FlowStreamEvent, CancellationToken, ValueTask>? sink,
        FlowResumeState? resume,
        CancellationToken ct);
}

/// <summary>
/// Sequential engine over the <c>FlowDefinitionDto</c> step list with nested
/// sub-lists for <c>condition</c> branches and <c>foreach</c> bodies
/// (AnythingLLM linear model + Dify branching essentials). Enforces the
/// <c>Flow:*</c> caps and per-step timeouts.
/// </summary>
public sealed class FlowEngine(
    IEnumerable<IFlowStepHandler> handlers,
    IConfiguration configuration) : IFlowEngine
{
    private const string StatusFailed = "failed";
    private const string StepIdKey = "stepId";

    private readonly Dictionary<string, IFlowStepHandler> _handlers =
        handlers.ToDictionary(h => h.Type, StringComparer.OrdinalIgnoreCase);
    private readonly FlowLimits _limits = FlowLimits.FromConfiguration(configuration);

    public async Task<FlowRunResult> ExecuteAsync(
        FlowDefinitionDto definition,
        JsonObject? inputs,
        IServiceProvider services,
        Func<FlowStreamEvent, CancellationToken, ValueTask>? sink,
        FlowResumeState? resume,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new FlowExecContext { Services = services, Limits = _limits, Sink = sink };

        if (resume is not null)
        {
            Restore(ctx, resume);
        }
        else
        {
            var inputError = BindInputs(definition, inputs, ctx);
            if (inputError is not null)
            {
                return new FlowRunResult
                {
                    Status = StatusFailed,
                    Error = inputError,
                    Steps = ctx.Trace,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }
        }

        JsonNode? output = null;
        string? error = null;
        try
        {
            await ExecuteStepsAsync(definition.Steps, ctx, ct);
            // Last <output> step wins; otherwise last executed step output.
            output = ctx.Vars["__flow_output"] ?? LastOutput(ctx.Trace);
        }
        catch (FlowSuspendException ex)
        {
            await EmitAsync(ctx, FlowStreamEvent.Waiting, new JsonObject
            {
                [StepIdKey] = ex.Pending.StepId,
                ["message"] = ex.Pending.Message,
            }, CancellationToken.None);
            return new FlowRunResult
            {
                Status = "waiting_approval",
                Steps = ctx.Trace,
                DurationMs = sw.ElapsedMilliseconds,
                Pending = ex.Pending,
                ResumeState = Snapshot(ctx, ex.Pending),
            };
        }
        catch (FlowAbortException ex)
        {
            error = ex.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error = "cancelled";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new FlowRunResult
        {
            Status = error is null ? "done" : StatusFailed,
            Output = CapOutput(output, _limits),
            Error = error,
            Steps = ctx.Trace,
            DurationMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>Freeze the live context into a serialisable resume state.</summary>
    private static FlowResumeState Snapshot(FlowExecContext ctx, PendingApproval pending) => new()
    {
        Vars = ctx.Vars,
        StepOutputs = new Dictionary<string, JsonNode?>(ctx.StepOutputs, StringComparer.Ordinal),
        StepErrors = new Dictionary<string, string?>(ctx.StepErrors, StringComparer.Ordinal),
        ExecutedCount = ctx.ExecutedCount,
        IterationsCount = ctx.IterationsCount,
        Trace = ctx.Trace.Select(r => new FlowStepResultDto(
            r.StepId, r.StepType, r.Name, r.Status, r.Output, r.Error, r.DurationMs)
        { Attempts = r.Attempts }).ToList(),
        PendingStepId = pending.StepId,
    };

    /// <summary>Rebuild a context from a persisted resume state.</summary>
    private static void Restore(FlowExecContext ctx, FlowResumeState state)
    {
        if (state.Vars is not null)
            foreach (var (k, v) in state.Vars)
                ctx.Vars[k] = v?.DeepClone();
        if (state.StepOutputs is not null)
        {
            foreach (var (k, v) in state.StepOutputs)
                ctx.StepOutputs[k] = v?.DeepClone();
            ctx.RestoredStepIds = new HashSet<string>(state.StepOutputs.Keys, StringComparer.Ordinal);
        }
        if (state.StepErrors is not null)
            foreach (var (k, v) in state.StepErrors)
                ctx.StepErrors[k] = v;
        ctx.ExecutedCount = state.ExecutedCount;
        ctx.IterationsCount = state.IterationsCount;
        if (state.Trace is not null)
            foreach (var r in state.Trace)
                ctx.Trace.Add(new FlowStepResult
                {
                    StepId = r.StepId,
                    StepType = r.StepType,
                    Name = r.Name,
                    Status = r.Status,
                    Output = r.Output,
                    Error = r.Error,
                    DurationMs = r.DurationMs,
                    Attempts = r.Attempts,
                });
    }

    /// <summary>Execute a (possibly nested) step list into the shared ctx trace.</summary>
    internal async Task ExecuteStepsAsync(
        IReadOnlyList<FlowStepDto> stepList,
        FlowExecContext ctx,
        CancellationToken ct)
    {
        foreach (var step in stepList)
        {
            ct.ThrowIfCancellationRequested();
            // Resumed run: a step id restored from the suspended run already
            // completed before the suspension — skip instead of re-executing
            // (re-running would double side effects like tool calls). Each
            // restored id is consumed once so steps that legitimately repeat
            // inside a foreach later in THIS run are not skipped.
            if (ctx.RestoredStepIds?.Remove(step.Id) == true)
            {
                await EmitAsync(ctx, FlowStreamEvent.StepSkip, new JsonObject
                {
                    [StepIdKey] = step.Id,
                    ["stepType"] = step.Type,
                }, ct);
                continue;
            }
            if (ctx.ExecutedCount >= ctx.Limits.MaxSteps)
                throw new FlowAbortException($"step budget exceeded (Flow:MaxSteps={ctx.Limits.MaxSteps})");
            ctx.ExecutedCount++;

            var result = new FlowStepResult
            {
                StepId = step.Id,
                StepType = step.Type,
                Name = step.Name,
                Status = "done",
            };
            ctx.Trace.Add(result);
            await EmitAsync(ctx, FlowStreamEvent.StepStart, new JsonObject
            {
                [StepIdKey] = step.Id,
                ["stepType"] = step.Type,
                ["name"] = step.Name,
            }, ct);

            await ExecuteStepAsync(step, ctx, result, ct);
        }
    }

    private async Task ExecuteStepAsync(
        FlowStepDto step,
        FlowExecContext ctx,
        FlowStepResult result,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var attempt = 1;
        try
        {
            if (!_handlers.TryGetValue(step.Type, out var handler))
                throw new FlowStepException($"unknown step type '{step.Type}'");

            // F3: per-step retry — config.retry {attempts (≤5), backoffMs}.
            // Retries apply to ordinary step failures and timeouts; a
            // suspension request (approval step) is never retried.
            var (attempts, backoffMs) = RetryOf(step);
            JsonNode? stepOutput = null;
            for (; attempt <= attempts; attempt++)
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(ctx.Limits.StepTimeoutSeconds));
                    stepOutput = await handler.ExecuteAsync(step, ctx, this, timeoutCts.Token);
                    break;
                }
                catch (FlowSuspendException) { throw; }
                catch (Exception) when (attempt < attempts && !ct.IsCancellationRequested)
                {
                    if (backoffMs > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(backoffMs), ct);
                }
            }
            result.Attempts = attempt;

            if (step.Type == "output")
                ctx.Vars["__flow_output"] = stepOutput;

            ctx.StepOutputs[step.Id] = stepOutput;
            result.Output = CapOutput(stepOutput, ctx.Limits);

            // HITL: an approval step records its request, then the run
            // suspends here — FlowService turns this into a ToolApproval
            // plus a persisted resume state.
            if (ctx.PendingApproval is { } pending)
                throw new FlowSuspendException(pending);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Status = StatusFailed;
            result.Error = $"step timed out after {ctx.Limits.StepTimeoutSeconds}s";
            ctx.StepErrors[step.Id] = result.Error;
            if (!ContinueOnError(step))
                throw new FlowAbortException($"step '{step.Id}' failed: {result.Error}");
        }
        catch (FlowSuspendException)
        {
            result.Status = "waiting";
            throw;
        }
        catch (Exception ex)
        {
            result.Status = StatusFailed;
            result.Error = ex.Message;
            result.Attempts = attempt;
            ctx.StepErrors[step.Id] = ex.Message;
            if (!ContinueOnError(step))
                throw new FlowAbortException($"step '{step.Id}' failed: {ex.Message}");
        }
        finally
        {
            result.DurationMs = sw.ElapsedMilliseconds;
            await EmitAsync(ctx, FlowStreamEvent.StepEnd, new JsonObject
            {
                [StepIdKey] = step.Id,
                ["stepType"] = step.Type,
                ["status"] = result.Status,
                ["error"] = result.Error,
                ["durationMs"] = result.DurationMs,
            }, CancellationToken.None);
        }
    }

    private static bool ContinueOnError(FlowStepDto step) =>
        step.Config?.TryGetPropertyValue("continueOnError", out var v) == true
        && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

    /// <summary>Per-step retry policy: config.retry {attempts, backoffMs} —
    /// attempts counts total tries (default 1), capped at 5 like Dify's
    /// bounded failure strategy.</summary>
    private static (int Attempts, int BackoffMs) RetryOf(FlowStepDto step)
    {
        var (attempts, backoff) = (1, 0);
        if (step.Config?.TryGetPropertyValue("retry", out var r) == true && r is JsonObject ro)
        {
            if (ro["attempts"] is JsonValue av && av.TryGetValue<int>(out var a) && a > 0)
                attempts = Math.Min(a, 5);
            if (ro["backoffMs"] is JsonValue bv && bv.TryGetValue<int>(out var b) && b > 0)
                backoff = b;
        }
        return (attempts, backoff);
    }

    private static async Task EmitAsync(
        FlowExecContext ctx, string type, JsonNode? data, CancellationToken ct)
    {
        if (ctx.Sink is not null)
            await ctx.Sink(new FlowStreamEvent(type, data), ct);
    }

    private static JsonNode? LastOutput(List<FlowStepResult> steps) =>
        steps.LastOrDefault(s => s.Status == "done" && s.Output is not null)?.Output;

    private static JsonNode? CapOutput(JsonNode? output, FlowLimits limits)
    {
        if (output is null) return null;
        var json = output.ToJsonString();
        if (json.Length <= limits.MaxOutputBytes) return output;
        return JsonValue.Create(new JsonObject
        {
            ["truncated"] = true,
            ["preview"] = json[..Math.Min(json.Length, 4096)],
        });
    }

    /// <summary>Required inputs must be present; defaults fill missing values;
    /// undeclared inputs are ignored (agents may pass extra).</summary>
    private static string? BindInputs(FlowDefinitionDto def, JsonObject? inputs, FlowExecContext ctx)
    {
        foreach (var input in def.Inputs)
        {
            var value = inputs?[input.Name];
            if (value is null)
            {
                if (input.Required && input.Default is null)
                    return $"missing required input '{input.Name}'";
                if (input.Default is not null)
                    value = input.Default.DeepClone();
            }
            if (value is not null)
                ctx.Vars[input.Name] = value.DeepClone();
        }
        // Undeclared extras (e.g. trigger metadata, webhook event payload)
        // are still exposed under vars.* so steps can reference them.
        if (inputs is not null)
            foreach (var (k, v) in inputs)
                if (!ctx.Vars.ContainsKey(k))
                    ctx.Vars[k] = v?.DeepClone();
        return null;
    }
}

/// <summary>Non-fatal run abort (step failure without continueOnError, caps).</summary>
public sealed class FlowAbortException(string message) : Exception(message);

/// <summary>Step-level validation failure (bad config etc.).</summary>
public sealed class FlowStepException(string message) : Exception(message);
