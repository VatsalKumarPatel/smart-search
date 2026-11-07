using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SmartSearch.Application.Dynamic;

public sealed record PlanExample(string Question, string PlanJson, IReadOnlyList<string> Entities);

public interface IPlanExampleStore
{
    /// <summary>Verified rows from ss.SemExample touching any of these entities (or semantically close to the question).</summary>
    Task<IReadOnlyList<PlanExample>> FindAsync(string question, IReadOnlyCollection<string> entities, int take, CancellationToken ct);
}

/// <summary>
/// Picks the part of the catalog that is relevant to the question, so the prompt stays small
/// even when the catalog grows to dozens of entities.
/// Start: synonyms + names. Later: add Titan embeddings over entity / field descriptions.
/// </summary>
public static partial class SchemaRetriever
{
    public static IReadOnlyList<EntityDef> Select(string question, SemanticCatalog catalog, int max = 6)
    {
        var q = " " + NonWord().Replace(question.ToLowerInvariant(), " ") + " ";
        var picked = new List<string>();

        void Add(string code)
        {
            if (catalog.Entity(code) is { } e && !picked.Contains(e.Code, StringComparer.OrdinalIgnoreCase)) picked.Add(e.Code);
        }

        foreach (var s in catalog.Synonyms)
            if (q.Contains(" " + s.Term.ToLowerInvariant() + " ", StringComparison.Ordinal))
                Add(s.TargetType == "Entity" ? s.Target : s.Target.Split('.', '=')[0]);

        foreach (var e in catalog.Entities.Values.Where(e => e.IsEnabled))
            if (q.Contains(" " + e.DisplayName.ToLowerInvariant(), StringComparison.Ordinal)) Add(e.Code);

        Add("Client");                                   // people are almost always involved
        foreach (var code in picked.ToList())            // one hop of neighbours for joins / exists
            foreach (var r in catalog.Relations.Where(r => r.From.Equals(code, StringComparison.OrdinalIgnoreCase)))
                if (picked.Count < max) Add(r.To);

        return picked.Take(max).Select(c => catalog.Entity(c)!).ToList();
    }

    [GeneratedRegex(@"[^a-z0-9\-]+")]
    private static partial Regex NonWord();
}

/// <summary>Builds the system prompt and the tool schema Bedrock must fill in.</summary>
public static class PlanPrompt
{
    public const string ToolName = "build_query_plan";

    public static string System(IReadOnlyList<EntityDef> entities, SemanticCatalog catalog, IReadOnlyList<PlanExample> examples, DateTime today)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You turn questions from staff of a behavioral-health EHR into a JSON query plan.");
        sb.AppendLine($"Call the tool {ToolName} exactly once. Never write SQL. Today is {today.ToString("yyyy-MM-dd (dddd)", CultureInfo.InvariantCulture)}.");
        sb.AppendLine();
        sb.AppendLine("""
            Rules:
            - Use ONLY the entities and fields listed below, written as Entity.Field.
            - "entity" is the thing the user wants a list of (or wants to count).
            - Fields of a related entity marked many-to-one can be used directly in select / where / orderBy / groupBy.
            - One-to-many data (e.g. a client's visits or diagnoses) ONLY through "exists" / "notExists".
              "more than 3" = minCount 4. Fields inside exists must belong to that exists entity.
            - Person names: {"field":"Client.FullName","op":"nameMatches","value":"<name exactly as typed>"}.
              Never correct the spelling; the system handles misspellings and suggestions.
            - Dates: prefer relative values {"relative":"-90d"} (d, w, m, y) or {"startOf":"month","offset":-1}.
              Use "timeRange" for the main date window of the entity; "to" is exclusive.
            - "latest / most recent ... for each client" or "latest visit for <name>": set latestPerClient true.
            - "how many", "count", "per", "by", "total", "average" => kind "aggregate" with groupBy + aggregates.
            - Use listed allowed values exactly (case included). Do not invent codes or values.
            - Return {"parts":[...]}: ONE part per distinct thing asked for. "last medication for vatsal and his
              last billing details" = two parts (one on the medication entity, one on the billing entity).
              Give every part a short "title" (e.g. "Last medication"). Repeat the person's nameMatches filter
              in every part that is about that person.
            - "last / latest / most recent" for a named person: latestPerClient true, orderBy the main date desc.
            - If the question cannot be answered with these entities, or two readings would give different
              results, return {"clarify":{...}} with one short question and 2-4 options instead of parts. Do not guess.
            - Keep select short: what a person needs to recognise the row (5 fields or fewer).
            """);
        sb.AppendLine("Catalog:");

        foreach (var e in entities)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nEntity {e.Code}: {e.Description}");
            if (e.DefaultDateField is not null) sb.Append(CultureInfo.InvariantCulture, $" Main date: {e.Code}.{e.DefaultDateField}.");
            if (e.RequiresTimeRange) sb.Append(CultureInfo.InvariantCulture, $" Large table: without a date filter only the last {e.DefaultLookbackMonths} months are searched.");
            sb.AppendLine();

            foreach (var f in e.Fields.Values.Where(f => !f.IsRestricted).OrderBy(f => f.SortOrder))
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {f.Ref} ({f.DataType}) {f.DisplayName}");
                if (!string.IsNullOrWhiteSpace(f.Description)) sb.Append(CultureInfo.InvariantCulture, $" - {f.Description}");
                if (f.AllowedValues.Count > 0) sb.Append(CultureInfo.InvariantCulture, $". Values: {string.Join(" | ", f.AllowedValues)}");
                sb.AppendLine();
            }

            foreach (var r in catalog.Relations.Where(r => r.From == e.Code && entities.Any(x => x.Code == r.To)))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  -> {r.To} ({(r.IsManyToOne ? "many-to-one" : "one-to-many, use exists")})");
        }

        if (examples.Count > 0)
        {
            sb.AppendLine("\nExamples:");
            foreach (var ex in examples)
                sb.AppendLine(CultureInfo.InvariantCulture, $"Q: {ex.Question}\nPlan: {ex.PlanJson}\n");
        }
        return sb.ToString();
    }

    /// <summary>JSON schema for the tool input. Entity and field names are enums, which keeps the model inside the catalog.</summary>
    public static JsonObject ToolSchema(IReadOnlyList<EntityDef> entities)
    {
        var fieldRefs = new JsonArray(entities.SelectMany(e => e.Fields.Values.Where(f => !f.IsRestricted).Select(f => (JsonNode)f.Ref)).ToArray());
        var entityCodes = new JsonArray(entities.Select(e => (JsonNode)e.Code).ToArray());
        var ops = new JsonArray(FilterOps.ByType.Values.SelectMany(x => x).Distinct().Select(o => (JsonNode)o).ToArray());

        JsonObject FieldEnum() => new() { ["type"] = "string", ["enum"] = fieldRefs.DeepClone() };
        JsonObject DateValue() => new()
        {
            ["description"] = "\"YYYY-MM-DD\" | \"today\" | {\"relative\":\"-90d\"} | {\"startOf\":\"month\",\"offset\":-1}"
        };

        var part = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("title", "kind", "entity"),
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Short section heading, e.g. \"Last medication\"" },
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("rows", "aggregate") },
                ["entity"] = new JsonObject { ["type"] = "string", ["enum"] = entityCodes },
                ["select"] = new JsonObject { ["type"] = "array", ["items"] = FieldEnum(), ["maxItems"] = 8 },
                ["where"] = new JsonObject { ["$ref"] = "#/$defs/filter" },
                ["timeRange"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["field"] = FieldEnum(), ["from"] = DateValue(), ["to"] = DateValue() }
                },
                ["latestPerClient"] = new JsonObject { ["type"] = "boolean" },
                ["groupBy"] = new JsonObject { ["type"] = "array", ["items"] = FieldEnum(), ["maxItems"] = 3 },
                ["aggregates"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("fn", "alias"),
                        ["properties"] = new JsonObject
                        {
                            ["fn"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("count", "countDistinct", "sum", "avg", "min", "max") },
                            ["field"] = FieldEnum(),
                            ["alias"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z][A-Za-z0-9_]{0,30}$" }
                        }
                    }
                },
                ["orderBy"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = 3,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("field"),
                        ["properties"] = new JsonObject
                        {
                            ["field"] = new JsonObject { ["type"] = "string", ["description"] = "Entity.Field, or an aggregate alias" },
                            ["dir"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("asc", "desc") }
                        }
                    }
                },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500 }
            }
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["parts"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = SearchPlan.MaxParts,
                    ["items"] = part
                },
                ["clarify"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Use INSTEAD of parts when the question is ambiguous.",
                    ["properties"] = new JsonObject
                    {
                        ["question"] = new JsonObject { ["type"] = "string" },
                        ["options"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = 4 }
                    }
                }
            },
            ["$defs"] = new JsonObject
            {
                ["filter"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Exactly one of: and | or | not | field+op(+value) | exists | notExists",
                    ["properties"] = new JsonObject
                    {
                        ["and"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/filter" } },
                        ["or"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/filter" } },
                        ["not"] = new JsonObject { ["$ref"] = "#/$defs/filter" },
                        ["field"] = FieldEnum(),
                        ["op"] = new JsonObject { ["type"] = "string", ["enum"] = ops },
                        ["value"] = new JsonObject { ["description"] = "string | number | boolean | date value | array (in, notIn, between)" },
                        ["exists"] = new JsonObject { ["$ref"] = "#/$defs/sub" },
                        ["notExists"] = new JsonObject { ["$ref"] = "#/$defs/sub" }
                    }
                },
                ["sub"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("entity"),
                    ["properties"] = new JsonObject
                    {
                        ["entity"] = new JsonObject { ["type"] = "string", ["enum"] = entityCodes.DeepClone() },
                        ["where"] = new JsonObject { ["$ref"] = "#/$defs/filter" },
                        ["minCount"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }
                    }
                }
            }
        };
    }
}
