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
    /// for step failures — they land in the returned trace.</summary>
    Task<FlowRunResult> ExecuteAsync(
        FlowDefinitionDto definition,
        JsonObject? inputs,
        IServiceProvider services,
        Func<FlowStreamEvent, CancellationToken, ValueTask>? sink,
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
    private readonly Dictionary<string, IFlowStepHandler> _handlers =
        handlers.ToDictionary(h => h.Type, StringComparer.OrdinalIgnoreCase);
    private readonly FlowLimits _limits = FlowLimits.FromConfiguration(configuration);

    public async Task<FlowRunResult> ExecuteAsync(
        FlowDefinitionDto definition,
        JsonObject? inputs,
        IServiceProvider services,
        Func<FlowStreamEvent, CancellationToken, ValueTask>? sink,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new FlowExecContext { Services = services, Limits = _limits, Sink = sink };

        var inputError = BindInputs(definition, inputs, ctx);
        if (inputError is not null)
        {
            return new FlowRunResult
            {
                Status = "failed",
                Error = inputError,
                Steps = ctx.Trace,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        JsonNode? output = null;
        string? error = null;
        try
        {
            await ExecuteStepsAsync(definition.Steps, ctx, ct);
            // Last <output> step wins; otherwise last executed step output.
            output = ctx.Vars["__flow_output"] ?? LastOutput(ctx.Trace);
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
            Status = error is null ? "done" : "failed",
            Output = CapOutput(output, _limits),
            Error = error,
            Steps = ctx.Trace,
            DurationMs = sw.ElapsedMilliseconds,
        };
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
                ["stepId"] = step.Id,
                ["stepType"] = step.Type,
                ["name"] = step.Name,
            }, ct);

            var sw = Stopwatch.StartNew();
            try
            {
                if (!_handlers.TryGetValue(step.Type, out var handler))
                    throw new FlowStepException($"unknown step type '{step.Type}'");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(ctx.Limits.StepTimeoutSeconds));
                var stepOutput = await handler.ExecuteAsync(step, ctx, this, timeoutCts.Token);

                if (step.Type == "output")
                    ctx.Vars["__flow_output"] = stepOutput;

                ctx.StepOutputs[step.Id] = stepOutput;
                result.Output = CapOutput(stepOutput, ctx.Limits);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result.Status = "failed";
                result.Error = $"step timed out after {ctx.Limits.StepTimeoutSeconds}s";
                ctx.StepErrors[step.Id] = result.Error;
                if (!ContinueOnError(step))
                    throw new FlowAbortException($"step '{step.Id}' failed: {result.Error}");
            }
            catch (Exception ex)
            {
                result.Status = "failed";
                result.Error = ex.Message;
                ctx.StepErrors[step.Id] = ex.Message;
                if (!ContinueOnError(step))
                    throw new FlowAbortException($"step '{step.Id}' failed: {ex.Message}");
            }
            finally
            {
                result.DurationMs = sw.ElapsedMilliseconds;
                await EmitAsync(ctx, FlowStreamEvent.StepEnd, new JsonObject
                {
                    ["stepId"] = step.Id,
                    ["stepType"] = step.Type,
                    ["status"] = result.Status,
                    ["error"] = result.Error,
                    ["durationMs"] = result.DurationMs,
                }, CancellationToken.None);
            }
        }
    }

    private static bool ContinueOnError(FlowStepDto step) =>
        step.Config?.TryGetPropertyValue("continueOnError", out var v) == true
        && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

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
        return null;
    }
}

/// <summary>Non-fatal run abort (step failure without continueOnError, caps).</summary>
public sealed class FlowAbortException(string message) : Exception(message);

/// <summary>Step-level validation failure (bad config etc.).</summary>
public sealed class FlowStepException(string message) : Exception(message);
