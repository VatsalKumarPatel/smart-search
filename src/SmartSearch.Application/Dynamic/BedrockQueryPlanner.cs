using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartSearch.Application.Dynamic;

/// <summary>
/// Thin seam over Bedrock InvokeModel so the planner can be unit-tested without AWS.
/// Infrastructure implementation (AWSSDK.BedrockRuntime):
///   var res = await client.InvokeModelAsync(new InvokeModelRequest {
///       ModelId = modelId, ContentType = "application/json", Accept = "application/json",
///       Body = new MemoryStream(Encoding.UTF8.GetBytes(body)) }, ct);
///   return await new StreamReader(res.Body).ReadToEndAsync(ct);
/// </summary>
public interface IBedrockInvoker
{
    Task<string> InvokeAsync(string modelId, string jsonBody, CancellationToken ct);
}

public sealed class PlannerOptions
{
    /// <summary>A Claude model or inference profile ID enabled in your Bedrock account (a Sonnet-class model plans more reliably than Haiku).</summary>
    public string ModelId { get; set; } = "";
    public int MaxTokens { get; set; } = 2000;
    /// <summary>One retry where the model sees the validator errors and fixes its plan.</summary>
    public int MaxRepairAttempts { get; set; } = 1;
    /// <summary>Adds cache_control to the system prompt. Enable only for models that support prompt caching on Bedrock.</summary>
    public bool UsePromptCache { get; set; }
}

public sealed record PlannerResult(SearchPlan? Plan, string? RawPlanJson, string? ToolUseId, int InputTokens, int OutputTokens, string? Error);

public interface IQueryPlanner
{
    Task<PlannerResult> PlanAsync(string question, string systemPrompt, JsonObject toolSchema, CancellationToken ct);
    Task<PlannerResult> RepairAsync(string question, string systemPrompt, JsonObject toolSchema, PlannerResult previous, IReadOnlyList<string> errors, CancellationToken ct);
}

/// <summary>Anthropic Messages API on Bedrock with a forced tool call. Only the question and the catalog are sent; never data.</summary>
public sealed class BedrockQueryPlanner(IBedrockInvoker bedrock, PlannerOptions options) : IQueryPlanner
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public Task<PlannerResult> PlanAsync(string question, string systemPrompt, JsonObject toolSchema, CancellationToken ct) =>
        SendAsync(systemPrompt, toolSchema, [UserText(question)], ct);

    public Task<PlannerResult> RepairAsync(string question, string systemPrompt, JsonObject toolSchema, PlannerResult previous, IReadOnlyList<string> errors, CancellationToken ct)
    {
        var messages = new List<JsonObject>
        {
            UserText(question),
            new()
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = previous.ToolUseId,
                    ["name"] = PlanPrompt.ToolName,
                    ["input"] = JsonNode.Parse(previous.RawPlanJson ?? "{}")
                })
            },
            new()
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = previous.ToolUseId,
                    ["is_error"] = true,
                    ["content"] = "The plan was rejected:\n- " + string.Join("\n- ", errors) + "\nCall the tool again with a corrected plan."
                })
            }
        };
        return SendAsync(systemPrompt, toolSchema, messages, ct);
    }

    private async Task<PlannerResult> SendAsync(string systemPrompt, JsonObject toolSchema, List<JsonObject> messages, CancellationToken ct)
    {
        JsonNode system = options.UsePromptCache
            ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = systemPrompt, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } })
            : JsonValue.Create(systemPrompt)!;

        var body = new JsonObject
        {
            ["anthropic_version"] = "bedrock-2023-05-31",
            ["max_tokens"] = options.MaxTokens,
            ["temperature"] = 0,
            ["system"] = system,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m).ToArray()),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = PlanPrompt.ToolName,
                ["description"] = "Return the query plan that answers the user's question.",
                ["input_schema"] = toolSchema.DeepClone()
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = PlanPrompt.ToolName }
        };

        var raw = await bedrock.InvokeAsync(options.ModelId, body.ToJsonString(), ct);
        return Parse(raw);
    }

    internal static PlannerResult Parse(string raw)
    {
        var root = JsonNode.Parse(raw)!;
        var inTok = root["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        var outTok = root["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;

        var toolUse = root["content"]?.AsArray().FirstOrDefault(c => c?["type"]?.GetValue<string>() == "tool_use");
        if (toolUse?["input"] is not { } input)
            return new PlannerResult(null, null, null, inTok, outTok, "model did not return a plan");

        var json = input.ToJsonString();
        try
        {
            // tolerate a bare single plan ({"kind":...,"entity":...}) by wrapping it as one part
            var plan = input["parts"] is null && input["entity"] is not null
                ? new SearchPlan { Parts = [JsonSerializer.Deserialize<QueryPlan>(json, Json)!] }
                : JsonSerializer.Deserialize<SearchPlan>(json, Json);
            return new PlannerResult(plan, json, toolUse["id"]?.GetValue<string>(), inTok, outTok, plan is null ? "empty plan" : null);
        }
        catch (JsonException ex)
        {
            return new PlannerResult(null, json, toolUse["id"]?.GetValue<string>(), inTok, outTok, "plan is not valid JSON for the schema: " + ex.Message);
        }
    }

    private static JsonObject UserText(string text) => new() { ["role"] = "user", ["content"] = text };
}
