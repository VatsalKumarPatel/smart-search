using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartSearch.Application.Dynamic;

/// <summary>
/// The query plan Bedrock produces for questions no predefined intent covers.
/// It is a small JSON language over the SEMANTIC CATALOG (Entity.Field names), never SQL.
/// The validator checks it, and SqlCompiler turns it into parameterized SQL.
///
/// Example: "clients with a depression diagnosis not seen in 90 days"
/// {
///   "kind": "rows",
///   "entity": "Client",
///   "select": ["Client.FirstName", "Client.LastName"],
///   "where": { "and": [
///       { "exists":    { "entity": "Diagnosis", "where": { "field": "Diagnosis.Code", "op": "startsWith", "value": "F32" } } },
///       { "notExists": { "entity": "Visit", "where": { "field": "Visit.VisitDate", "op": "gte", "value": { "relative": "-90d" } } } } ] }
/// }
/// </summary>
public sealed class QueryPlan
{
    /// <summary>Section heading when a question has several parts, e.g. "Last medication".</summary>
    [JsonPropertyName("title")]           public string? Title { get; init; }
    [JsonPropertyName("kind")]            public PlanKind Kind { get; init; } = PlanKind.Rows;
    [JsonPropertyName("entity")]          public string Entity { get; init; } = "";
    [JsonPropertyName("select")]          public List<string> Select { get; init; } = [];
    [JsonPropertyName("where")]           public FilterNode? Where { get; init; }
    [JsonPropertyName("timeRange")]       public TimeRangeSpec? TimeRange { get; init; }

    /// <summary>Keep only the newest row per client (e.g. "latest visit for each client").</summary>
    [JsonPropertyName("latestPerClient")] public bool LatestPerClient { get; init; }

    [JsonPropertyName("groupBy")]         public List<string> GroupBy { get; init; } = [];
    [JsonPropertyName("aggregates")]      public List<AggregateSpec> Aggregates { get; init; } = [];
    [JsonPropertyName("orderBy")]         public List<SortSpec> OrderBy { get; init; } = [];
    [JsonPropertyName("limit")]           public int? Limit { get; init; }

    /// <summary>Set when the question is ambiguous; nothing is queried.</summary>
    [JsonPropertyName("clarify")]         public ClarifySpec? Clarify { get; init; }
}

/// <summary>
/// What Bedrock returns: one part per thing the user asked for.
/// "last medication for vatsal and his last billing details" → two parts (Medication, Charge),
/// each repeating the same nameMatches filter. Names are resolved once and shared.
/// </summary>
public sealed class SearchPlan
{
    [JsonPropertyName("parts")]   public List<QueryPlan> Parts { get; init; } = [];

    /// <summary>Set instead of parts when the question is ambiguous.</summary>
    [JsonPropertyName("clarify")] public ClarifySpec? Clarify { get; init; }

    public const int MaxParts = 4;
}

[JsonConverter(typeof(JsonStringEnumConverter<PlanKind>))]
public enum PlanKind { Rows, Aggregate, Clarify }

/// <summary>
/// One node of the filter tree. Exactly ONE shape must be used per node:
///   { "and": [...] } | { "or": [...] } | { "not": {...} }
///   { "field": "Visit.Status", "op": "eq", "value": "No show" }
///   { "exists": {...} } | { "notExists": {...} }
/// </summary>
public sealed class FilterNode
{
    [JsonPropertyName("and")]       public List<FilterNode>? And { get; init; }
    [JsonPropertyName("or")]        public List<FilterNode>? Or { get; init; }
    [JsonPropertyName("not")]       public FilterNode? Not { get; init; }

    [JsonPropertyName("field")]     public string? Field { get; init; }
    [JsonPropertyName("op")]        public string? Op { get; init; }
    [JsonPropertyName("value")]     public JsonElement? Value { get; init; }

    [JsonPropertyName("exists")]    public SubQuerySpec? Exists { get; init; }
    [JsonPropertyName("notExists")] public SubQuerySpec? NotExists { get; init; }

    /// <summary>Filled by the NameMatcher for op "nameMatches" before compiling. Never from the model.</summary>
    [JsonIgnore] public IReadOnlyList<long>? ResolvedClientIds { get; set; }
}

public sealed class SubQuerySpec
{
    [JsonPropertyName("entity")]   public string Entity { get; init; } = "";
    [JsonPropertyName("where")]    public FilterNode? Where { get; init; }

    /// <summary>"more than 3 no-shows" = minCount 4. Null means at least one.</summary>
    [JsonPropertyName("minCount")] public int? MinCount { get; init; }
}

/// <summary>Applies to the root entity's date field (or Field when given).</summary>
public sealed class TimeRangeSpec
{
    [JsonPropertyName("field")] public string? Field { get; init; }
    /// <summary>Inclusive. "2024-01-01" | {"relative":"-90d"} | {"startOf":"month","offset":-1} | "today"</summary>
    [JsonPropertyName("from")]  public JsonElement? From { get; init; }
    /// <summary>Exclusive. Same formats as From.</summary>
    [JsonPropertyName("to")]    public JsonElement? To { get; init; }
}

public sealed class AggregateSpec
{
    /// <summary>count | countDistinct | sum | avg | min | max</summary>
    [JsonPropertyName("fn")]    public string Fn { get; init; } = "count";
    /// <summary>Entity.Field; optional for count.</summary>
    [JsonPropertyName("field")] public string? Field { get; init; }
    [JsonPropertyName("alias")] public string Alias { get; init; } = "count";
}

public sealed class SortSpec
{
    /// <summary>Entity.Field, or an aggregate alias.</summary>
    [JsonPropertyName("field")] public string Field { get; init; } = "";
    [JsonPropertyName("dir")]   public string Dir { get; init; } = "asc";
}

public sealed class ClarifySpec
{
    [JsonPropertyName("question")] public string Question { get; init; } = "";
    [JsonPropertyName("options")]  public List<string> Options { get; init; } = [];
}

public static class FilterOps
{
    public const string Eq = "eq", Neq = "neq", Gt = "gt", Gte = "gte", Lt = "lt", Lte = "lte",
                        In = "in", NotIn = "notIn", Between = "between",
                        Contains = "contains", StartsWith = "startsWith",
                        IsNull = "isNull", IsNotNull = "isNotNull",
                        NameMatches = "nameMatches";

    /// <summary>Operators allowed per catalog DataType.</summary>
    public static readonly IReadOnlyDictionary<string, HashSet<string>> ByType = new Dictionary<string, HashSet<string>>
    {
        ["text"]       = [Eq, Neq, In, NotIn, Contains, StartsWith, IsNull, IsNotNull],
        ["code"]       = [Eq, Neq, In, NotIn, StartsWith, IsNull, IsNotNull],
        ["int"]        = [Eq, Neq, Gt, Gte, Lt, Lte, In, NotIn, Between, IsNull, IsNotNull],
        ["decimal"]    = [Eq, Neq, Gt, Gte, Lt, Lte, Between, IsNull, IsNotNull],
        ["date"]       = [Eq, Gt, Gte, Lt, Lte, Between, IsNull, IsNotNull],
        ["datetime"]   = [Eq, Gt, Gte, Lt, Lte, Between, IsNull, IsNotNull],
        ["bool"]       = [Eq, IsNull, IsNotNull],
        ["personName"] = [NameMatches],
    };
}
