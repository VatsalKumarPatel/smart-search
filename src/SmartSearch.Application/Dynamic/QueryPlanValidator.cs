using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartSearch.Application.Dynamic;

/// <summary>
/// Checks a model-produced QueryPlan against the semantic catalog and the user's permissions,
/// fills in safe defaults (time window, limit, default columns) and records every assumption
/// so the UI can show it ("Showing the last 12 months").
/// Nothing reaches the SQL compiler unless it passes here.
/// </summary>
public sealed partial class QueryPlanValidator
{
    private const int MaxDepth = 6, MaxNodes = 40, MaxInValues = 100, MaxExistsDepth = 2,
                      MaxGroupBy = 3, MaxAggregates = 5, MaxSort = 3, MaxSelect = 20;

    /// <summary>Validates every part on its own, so one bad or forbidden part does not block the others.</summary>
    public SearchValidation ValidateSearch(SearchPlan plan, SemanticCatalog catalog, Func<string, bool> hasPermission, DateTime today)
    {
        var global = new List<string>();
        if (plan.Clarify is null && plan.Parts.Count == 0) global.Add("return at least one part, or clarify");
        if (plan.Parts.Count > SearchPlan.MaxParts) global.Add($"at most {SearchPlan.MaxParts} parts");

        var parts = plan.Parts.Take(SearchPlan.MaxParts)
            .Select((p, i) => new PartValidation(i, p.Title ?? $"Part {i + 1}", Validate(p, catalog, hasPermission, today)))
            .ToList();
        return new SearchValidation(parts, global);
    }

    public ValidationResult Validate(QueryPlan plan, SemanticCatalog catalog, Func<string, bool> hasPermission, DateTime today)
    {
        var ctx = new Ctx(catalog, hasPermission, today.Date);

        if (plan.Kind == PlanKind.Clarify)
        {
            if (plan.Clarify is null || string.IsNullOrWhiteSpace(plan.Clarify.Question))
                ctx.Error("clarify needs a question");
            return ctx.Result(null);
        }

        var root = catalog.Entity(plan.Entity);
        if (root is null) { ctx.Error($"unknown entity '{plan.Entity}'"); return ctx.Result(null); }
        ctx.RequireEntity(root);

        var vp = new ValidatedPlan { Plan = plan, Root = root, Kind = plan.Kind };

        // ---- filters
        if (plan.Where is not null)
            ValidateNode(plan.Where, root, isRoot: true, depth: 1, existsDepth: 0, ctx, vp);

        // ---- time window (protects 20 years of history)
        ResolveTimeRange(plan, root, ctx, vp);

        if (plan.Kind == PlanKind.Rows) ValidateRows(plan, root, ctx, vp);
        else ValidateAggregate(plan, root, ctx, vp);

        return ctx.Result(vp);
    }

    // ------------------------------------------------------------------ rows

    private static void ValidateRows(QueryPlan plan, EntityDef root, Ctx ctx, ValidatedPlan vp)
    {
        if (plan.GroupBy.Count > 0 || plan.Aggregates.Count > 0)
            ctx.Error("groupBy / aggregates are only allowed when kind is 'aggregate'");

        if (plan.Select.Count > MaxSelect) ctx.Error($"select at most {MaxSelect} fields");

        foreach (var s in plan.Select.Take(MaxSelect))
            if (ResolveInRootScope(s, root, ctx, vp, "select") is { } f)
            {
                if (!f.IsSelectable) ctx.Error($"{s} cannot be selected");
                else vp.AddSelect(f);
            }

        if (vp.Select.Count == 0)
            foreach (var f in root.Fields.Values.Where(f => f.IsDefault && !f.IsRestricted).OrderBy(f => f.SortOrder))
                vp.AddSelect(f);

        // fields the title / subtitle / statement templates need, so cards and links always render
        foreach (var key in TemplateKeys(root))
            if (ResolveAlias(key, root, ctx.Catalog) is { } f && !f.IsRestricted)
            {
                vp.MarkJoin(f, root, ctx.Catalog);
                if (vp.AddSelect(f)) vp.TemplateOnly.Add(f.Alias);
            }

        if (plan.LatestPerClient)
        {
            if (root.ClientIdColumn is null) ctx.Error($"latestPerClient needs an entity that belongs to a client; {root.Code} does not");
            else vp.LatestPerClient = true;
        }

        foreach (var s in plan.OrderBy.Take(MaxSort))
            if (ResolveInRootScope(s.Field, root, ctx, vp, "orderBy") is { } f)
            {
                if (!f.IsSortable) { ctx.Error($"{s.Field} cannot be sorted"); continue; }
                vp.OrderBy.Add(new ResolvedSort(f, null, IsDesc(s.Dir)));
                if (vp.AddSelect(f)) vp.TemplateOnly.Add(f.Alias);   // latestPerClient sorts by output alias
            }

        if (vp.OrderBy.Count == 0 && vp.TimeField is not null)
        {
            vp.OrderBy.Add(new ResolvedSort(vp.TimeField, null, Desc: true));
            if (vp.AddSelect(vp.TimeField)) vp.TemplateOnly.Add(vp.TimeField.Alias);
        }

        vp.Limit = Math.Clamp(plan.Limit ?? root.DefaultLimit, 1, root.MaxLimit);
        if (plan.Limit > root.MaxLimit) vp.Notes.Add($"Showing the first {root.MaxLimit} results");
    }

    // ------------------------------------------------------------------ aggregates

    private static void ValidateAggregate(QueryPlan plan, EntityDef root, Ctx ctx, ValidatedPlan vp)
    {
        if (plan.LatestPerClient) ctx.Error("latestPerClient is not allowed with aggregates");
        if (plan.GroupBy.Count > MaxGroupBy) ctx.Error($"groupBy at most {MaxGroupBy} fields");

        foreach (var g in plan.GroupBy.Take(MaxGroupBy))
            if (ResolveInRootScope(g, root, ctx, vp, "groupBy") is { } f)
            {
                if (!f.IsSelectable || f.DataType == "personName") ctx.Error($"cannot group by {g}");
                else vp.GroupBy.Add(f);
            }

        List<AggregateSpec> aggs = plan.Aggregates.Count == 0 ? [new AggregateSpec { Fn = "count", Alias = "count" }] : plan.Aggregates;
        if (aggs.Count > MaxAggregates) ctx.Error($"at most {MaxAggregates} aggregates");

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in aggs.Take(MaxAggregates))
        {
            if (!AliasPattern().IsMatch(a.Alias) || a.Alias.StartsWith("__") || !aliases.Add(a.Alias))
            { ctx.Error($"aggregate alias '{a.Alias}' must be a unique simple name"); continue; }

            FieldDefinition? f = null;
            if (a.Field is not null && (f = ResolveInRootScope(a.Field, root, ctx, vp, "aggregate")) is null) continue;

            var ok = a.Fn switch
            {
                "count" => true,
                "countDistinct" => f is not null && f.DataType != "personName",
                "sum" or "avg" => f is not null && f.DataType is "int" or "decimal",
                "min" or "max" => f is not null && f.DataType is "int" or "decimal" or "date" or "datetime",
                _ => false
            };
            if (!ok) { ctx.Error($"aggregate '{a.Fn}' is not valid{(f is null ? "" : $" for {f.Ref}")}"); continue; }
            vp.Aggregates.Add(new ResolvedAggregate(a.Fn, f, a.Alias));
        }

        foreach (var s in plan.OrderBy.Take(MaxSort))
        {
            if (vp.Aggregates.FirstOrDefault(x => x.Alias.Equals(s.Field, StringComparison.OrdinalIgnoreCase)) is { } agg)
                vp.OrderBy.Add(new ResolvedSort(null, agg.Alias, IsDesc(s.Dir)));
            else if (FieldRef.TryParse(s.Field, out var r) && vp.GroupBy.FirstOrDefault(g => g.Ref.Equals(r.ToString(), StringComparison.OrdinalIgnoreCase)) is { } g)
                vp.OrderBy.Add(new ResolvedSort(g, null, IsDesc(s.Dir)));
            else
                ctx.Error($"orderBy '{s.Field}' must be a groupBy field or an aggregate alias");
        }
        if (vp.OrderBy.Count == 0 && vp.Aggregates.Count > 0)
            vp.OrderBy.Add(new ResolvedSort(null, vp.Aggregates[0].Alias, Desc: true));

        vp.Limit = Math.Clamp(plan.Limit ?? 50, 1, 500);
    }

    // ------------------------------------------------------------------ filters

    private static void ValidateNode(FilterNode n, EntityDef scope, bool isRoot, int depth, int existsDepth, Ctx ctx, ValidatedPlan vp)
    {
        if (depth > MaxDepth) { ctx.Error("filter is nested too deeply"); return; }
        if (++vp.NodeCount > MaxNodes) { if (vp.NodeCount == MaxNodes + 1) ctx.Error("filter has too many conditions"); return; }

        var shapes = (n.And is not null ? 1 : 0) + (n.Or is not null ? 1 : 0) + (n.Not is not null ? 1 : 0)
                   + (n.Field is not null ? 1 : 0) + (n.Exists is not null ? 1 : 0) + (n.NotExists is not null ? 1 : 0);
        if (shapes != 1) { ctx.Error("each filter node must have exactly one of and / or / not / field / exists / notExists"); return; }

        if (n.And is not null) { foreach (var c in n.And) ValidateNode(c, scope, isRoot, depth + 1, existsDepth, ctx, vp); return; }
        if (n.Or is not null)  { foreach (var c in n.Or)  ValidateNode(c, scope, isRoot, depth + 1, existsDepth, ctx, vp); return; }
        if (n.Not is not null) { ValidateNode(n.Not, scope, isRoot, depth + 1, existsDepth, ctx, vp); return; }

        var sub = n.Exists ?? n.NotExists;
        if (sub is not null)
        {
            if (existsDepth >= MaxExistsDepth) { ctx.Error("exists is nested too deeply"); return; }
            var target = ctx.Catalog.Entity(sub.Entity);
            var rel = target is null ? null : ctx.Catalog.Relation(scope.Code, target.Code);
            if (target is null || rel is null) { ctx.Error($"{scope.Code} has no relation to '{sub.Entity}'"); return; }
            ctx.RequireEntity(target);
            if (sub.MinCount is < 1 or > 1000) ctx.Error("minCount must be between 1 and 1000");
            if (sub.Where is not null) ValidateNode(sub.Where, target, isRoot: false, depth + 1, existsDepth + 1, ctx, vp);
            return;
        }

        // ---- leaf condition
        var field = isRoot
            ? ResolveInRootScope(n.Field!, scope, ctx, vp, "filter")
            : ResolveInEntity(n.Field!, scope, ctx);
        if (field is null) return;
        if (!field.IsFilterable) { ctx.Error($"{field.Ref} cannot be filtered"); return; }

        var op = n.Op ?? "";
        if (!FilterOps.ByType.TryGetValue(field.DataType, out var allowed) || !allowed.Contains(op))
        { ctx.Error($"operator '{op}' is not allowed for {field.Ref} ({field.DataType})"); return; }

        if (op == FilterOps.NameMatches)
        {
            var name = n.Value is { ValueKind: JsonValueKind.String } v ? v.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 80) { ctx.Error("nameMatches needs a name of 2-80 characters"); return; }
            if (!isRoot) { ctx.Error("nameMatches is only allowed outside exists"); return; }
            vp.NameFilters.Add(n);
            return;
        }

        if (!PlanValues.TryCheck(field, op, n.Value, ctx.Today, MaxInValues, out var error))
            ctx.Error($"{field.Ref} {op}: {error}");

        if (isRoot && field.DataType is "date" or "datetime")
            vp.DateFieldsFiltered.Add(field.Ref);
    }

    // ------------------------------------------------------------------ time window

    private static void ResolveTimeRange(QueryPlan plan, EntityDef root, Ctx ctx, ValidatedPlan vp)
    {
        var fieldCode = plan.TimeRange?.Field is { } tf && FieldRef.TryParse(tf, out var r)
            ? (r.Entity.Equals(root.Code, StringComparison.OrdinalIgnoreCase) ? r.Field : null)
            : root.DefaultDateField;
        var timeField = fieldCode is null ? null : root.Field(fieldCode);
        if (plan.TimeRange?.Field is not null && (timeField is null || timeField.DataType is not ("date" or "datetime")))
        { ctx.Error($"timeRange field '{plan.TimeRange.Field}' must be a date field of {root.Code}"); return; }
        if (timeField is null) return;

        vp.TimeField = timeField;

        if (plan.TimeRange?.From is { } from)
        {
            if (DateValueResolver.TryResolve(from, ctx.Today, out var d, out var e)) vp.From = d; else ctx.Error($"timeRange.from: {e}");
        }
        if (plan.TimeRange?.To is { } to)
        {
            if (DateValueResolver.TryResolve(to, ctx.Today, out var d, out var e)) vp.To = d; else ctx.Error($"timeRange.to: {e}");
        }
        if (vp.From is { } f1 && vp.To is { } t1 && t1 <= f1) ctx.Error("timeRange.to must be after timeRange.from");

        var filteredDirectly = vp.DateFieldsFiltered.Contains(timeField.Ref);
        // a named person ("last bill for vatsal") narrows to a few clients, so search their full history
        var pinnedToPeople = vp.NameFilters.Count > 0;
        if (root.RequiresTimeRange && vp.From is null && !filteredDirectly && !pinnedToPeople)
        {
            vp.From = ctx.Today.AddMonths(-root.DefaultLookbackMonths);
            vp.Notes.Add($"Showing the last {root.DefaultLookbackMonths} months. Ask for a date range to look further back.");
        }

        if (vp.From is { } from2)
        {
            var earliest = (vp.To ?? ctx.Today).AddMonths(-root.MaxLookbackMonths);
            if (from2 < earliest)
            {
                vp.From = earliest;
                vp.Notes.Add($"Limited to {root.MaxLookbackMonths / 12} years of history.");
            }
        }
    }

    // ------------------------------------------------------------------ field resolution

    /// <summary>Root entity field, or a field of an entity the root points to (ManyToOne, one hop).</summary>
    private static FieldDefinition? ResolveInRootScope(string text, EntityDef root, Ctx ctx, ValidatedPlan vp, string where)
    {
        if (!FieldRef.TryParse(text, out var r)) { ctx.Error($"{where}: '{text}' must look like Entity.Field"); return null; }

        if (r.Entity.Equals(root.Code, StringComparison.OrdinalIgnoreCase))
            return CheckField(root.Field(r.Field), r, ctx);

        var target = ctx.Catalog.Entity(r.Entity);
        var rel = target is null ? null : ctx.Catalog.Relation(root.Code, target.Code);
        if (target is null || rel is null || !rel.IsManyToOne)
        {
            ctx.Error($"{where}: {r} is not reachable from {root.Code}. Use exists / notExists for one-to-many data.");
            return null;
        }
        ctx.RequireEntity(target);
        var f = CheckField(target.Field(r.Field), r, ctx);
        if (f is not null) vp.Joins.Add(target.Code);
        return f;
    }

    private static FieldDefinition? ResolveInEntity(string text, EntityDef entity, Ctx ctx)
    {
        if (!FieldRef.TryParse(text, out var r) || !r.Entity.Equals(entity.Code, StringComparison.OrdinalIgnoreCase))
        { ctx.Error($"inside exists on {entity.Code}, fields must be {entity.Code}.<Field> (got '{text}')"); return null; }
        return CheckField(entity.Field(r.Field), r, ctx);
    }

    private static FieldDefinition? CheckField(FieldDefinition? f, FieldRef r, Ctx ctx)
    {
        if (f is null) { ctx.Error($"unknown field {r}"); return null; }
        if (f.IsRestricted) { ctx.Error($"{r} is restricted and cannot be used"); return null; }
        return f;
    }

    /// <summary>Template placeholder Visit_VisitDate → field definition.</summary>
    private static FieldDefinition? ResolveAlias(string alias, EntityDef root, SemanticCatalog catalog)
    {
        var i = alias.IndexOf('_');
        if (i <= 0) return null;
        var entity = catalog.Entity(alias[..i]);
        if (entity is null) return null;
        if (!entity.Code.Equals(root.Code, StringComparison.OrdinalIgnoreCase)
            && catalog.Relation(root.Code, entity.Code) is not { IsManyToOne: true }) return null;
        return entity.Field(alias[(i + 1)..]);
    }

    private static IEnumerable<string> TemplateKeys(EntityDef e) =>
        new[] { e.TitleTemplate, e.SubtitleTemplate, e.StatementTemplate }
            .Where(t => t is not null)
            .SelectMany(t => Placeholder().Matches(t!).Select(m => m.Groups[1].Value))
            .Distinct();

    private static bool IsDesc(string dir) => dir.Equals("desc", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{([A-Za-z0-9]+_[A-Za-z0-9]+)(?::[a-z]+)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,30}$")]
    private static partial Regex AliasPattern();

    // ------------------------------------------------------------------ context

    private sealed class Ctx(SemanticCatalog catalog, Func<string, bool> hasPermission, DateTime today)
    {
        public SemanticCatalog Catalog { get; } = catalog;
        public DateTime Today { get; } = today;
        public List<string> Errors { get; } = [];
        private int _permissionErrors;
        private readonly HashSet<string> _checked = [];

        public void Error(string message) { if (Errors.Count < 20) Errors.Add(message); }

        public void RequireEntity(EntityDef e)
        {
            if (!_checked.Add(e.Code)) return;
            if (e.RequiredPermission is { } p && !hasPermission(p)) { Error($"you do not have access to {e.DisplayName} data"); _permissionErrors++; }
            if (e.IsConsentRestricted && !hasPermission("SmartSearch.ConsentRestricted")) { Error($"{e.DisplayName} data needs consent-restricted access"); _permissionErrors++; }
        }

        public ValidationResult Result(ValidatedPlan? plan) =>
            new(Errors.Count == 0 ? plan : null, Errors, PermissionOnly: Errors.Count > 0 && Errors.Count == _permissionErrors);
    }
}

public sealed record ValidationResult(ValidatedPlan? Plan, IReadOnlyList<string> Errors, bool PermissionOnly = false)
{
    public bool IsValid => Errors.Count == 0;
    /// <summary>Errors the model can fix (not "you have no access").</summary>
    public bool NeedsRepair => !IsValid && !PermissionOnly;
}

public sealed record PartValidation(int Index, string Title, ValidationResult Result);

public sealed record SearchValidation(IReadOnlyList<PartValidation> Parts, IReadOnlyList<string> GlobalErrors)
{
    public bool NeedsRepair => GlobalErrors.Count > 0 || Parts.Any(p => p.Result.NeedsRepair);

    public IReadOnlyList<string> RepairErrors =>
        GlobalErrors.Concat(Parts.Where(p => p.Result.NeedsRepair)
                                 .SelectMany(p => p.Result.Errors.Select(e => $"part {p.Index + 1} ({p.Title}): {e}")))
                    .ToList();
}

/// <summary>A plan that passed validation, with everything resolved to catalog definitions.</summary>
public sealed class ValidatedPlan
{
    public required QueryPlan Plan { get; init; }
    public required EntityDef Root { get; init; }
    public required PlanKind Kind { get; init; }

    public List<FieldDefinition> Select { get; } = [];
    /// <summary>Aliases selected only so templates / sorting work; not shown as their own column.</summary>
    public HashSet<string> TemplateOnly { get; } = [];
    public HashSet<string> Joins { get; } = new(StringComparer.OrdinalIgnoreCase);   // ManyToOne entities to join
    public List<FieldDefinition> GroupBy { get; } = [];
    public List<ResolvedAggregate> Aggregates { get; } = [];
    public List<ResolvedSort> OrderBy { get; } = [];
    public List<FilterNode> NameFilters { get; } = [];
    public bool LatestPerClient { get; set; }
    public int Limit { get; set; }

    public FieldDefinition? TimeField { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>Assumptions to show the user, e.g. "Showing the last 12 months".</summary>
    public List<string> Notes { get; } = [];

    internal int NodeCount;
    internal HashSet<string> DateFieldsFiltered { get; } = [];

    internal bool AddSelect(FieldDefinition f)
    {
        if (Select.Any(s => s.Ref == f.Ref)) return false;
        Select.Add(f);
        return true;
    }

    internal void MarkJoin(FieldDefinition f, EntityDef root, SemanticCatalog catalog)
    {
        if (!f.EntityCode.Equals(root.Code, StringComparison.OrdinalIgnoreCase)) Joins.Add(f.EntityCode);
    }
}

public sealed record ResolvedSort(FieldDefinition? Field, string? AggregateAlias, bool Desc);
public sealed record ResolvedAggregate(string Fn, FieldDefinition? Field, string Alias);

/// <summary>Checks and converts filter values to typed .NET values. Shared by validator and compiler.</summary>
public static class PlanValues
{
    public static bool TryCheck(FieldDefinition f, string op, JsonElement? value, DateTime today, int maxIn, out string? error)
    {
        error = null;
        switch (op)
        {
            case FilterOps.IsNull or FilterOps.IsNotNull:
                return true;
            case FilterOps.In or FilterOps.NotIn:
                if (value is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0) { error = "needs a non-empty list"; return false; }
                if (arr.GetArrayLength() > maxIn) { error = $"at most {maxIn} values"; return false; }
                foreach (var item in arr.EnumerateArray())
                    if (!TryConvert(f, item, today, out _, out error)) return false;
                return true;
            case FilterOps.Between:
                if (value is not { ValueKind: JsonValueKind.Array } b || b.GetArrayLength() != 2) { error = "needs [from, to]"; return false; }
                return TryConvert(f, b[0], today, out _, out error) && TryConvert(f, b[1], today, out _, out error);
            default:
                if (value is null) { error = "needs a value"; return false; }
                return TryConvert(f, value.Value, today, out _, out error);
        }
    }

    public static bool TryConvert(FieldDefinition f, JsonElement v, DateTime today, out object? result, out string? error)
    {
        result = null;
        error = null;
        switch (f.DataType)
        {
            case "text" or "code":
                if (v.ValueKind != JsonValueKind.String) { error = "needs text"; return false; }
                var s = v.GetString()!.Trim();
                if (s.Length is 0 or > 200) { error = "text must be 1-200 characters"; return false; }
                result = s;
                return true;
            case "int":
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) { result = l; return true; }
                error = "needs a whole number"; return false;
            case "decimal":
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) { result = d; return true; }
                error = "needs a number"; return false;
            case "bool":
                if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) { result = v.GetBoolean(); return true; }
                error = "needs true or false"; return false;
            case "date" or "datetime":
                if (DateValueResolver.TryResolve(v, today, out var dt, out error)) { result = dt; return true; }
                return false;
            default:
                error = "not filterable this way"; return false;
        }
    }
}
