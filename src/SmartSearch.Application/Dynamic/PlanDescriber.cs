using System.Globalization;
using System.Text.Json;

namespace SmartSearch.Application.Dynamic;

/// <summary>
/// Turns a validated plan back into plain English for the "Showing ..." line, built from the
/// plan itself (not from the model's own words), so users see exactly what was queried and
/// can rephrase if it is wrong.
///   "Visits where Visit status is No show and Provider contains "Shah", from 01-Oct-2026 to 31-Oct-2026, newest first"
/// </summary>
public static class PlanDescriber
{
    private const string DateFormat = "dd-MMM-yyyy";

    public static string Describe(ValidatedPlan vp, SemanticCatalog catalog, DateTime today)
    {
        var root = vp.Root;
        var parts = new List<string>();

        if (vp.Kind == PlanKind.Aggregate)
        {
            var measures = string.Join(", ", vp.Aggregates.Select(a => a.Fn switch
            {
                "count" => $"number of {DynamicTemplateBuilder.Plural(root.DisplayName).ToLowerInvariant()}",
                "countDistinct" => $"number of different {a.Field!.DisplayName.ToLowerInvariant()}",
                _ => $"{a.Fn} of {a.Field!.DisplayName.ToLowerInvariant()}"
            }));
            parts.Add(char.ToUpperInvariant(measures[0]) + measures[1..]);
            if (vp.GroupBy.Count > 0) parts[0] += " by " + string.Join(" and ", vp.GroupBy.Select(g => g.DisplayName.ToLowerInvariant()));
        }
        else
        {
            parts.Add(vp.LatestPerClient
                ? $"Latest {root.DisplayName.ToLowerInvariant()} per client"
                : DynamicTemplateBuilder.Plural(root.DisplayName));
        }

        if (vp.Plan.Where is { } w) parts.Add("where " + Node(w, catalog, today));

        if (vp.TimeField is not null && (vp.From is not null || vp.To is not null))
        {
            var label = vp.TimeField.DisplayName.ToLowerInvariant();
            parts.Add((vp.From, vp.To) switch
            {
                ({ } f, { } t) => $"{label} from {f.ToString(DateFormat, CultureInfo.InvariantCulture)} to {t.AddDays(-1).ToString(DateFormat, CultureInfo.InvariantCulture)}",
                ({ } f, null)  => $"{label} since {f.ToString(DateFormat, CultureInfo.InvariantCulture)}",
                (null, { } t)  => $"{label} before {t.ToString(DateFormat, CultureInfo.InvariantCulture)}",
                _ => ""
            });
        }

        if (vp.OrderBy.FirstOrDefault() is { } s)
        {
            var dt = s.Field?.DataType is "date" or "datetime";
            parts.Add(s.Field is not null && dt ? (s.Desc ? "newest first" : "oldest first")
                    : $"sorted by {(s.Field?.DisplayName ?? s.AggregateAlias)!.ToLowerInvariant()}{(s.Desc ? " (high to low)" : "")}");
        }

        return string.Join(", ", parts.Where(p => p.Length > 0));
    }

    private static string Node(FilterNode n, SemanticCatalog catalog, DateTime today)
    {
        if (n.And is not null) return string.Join(" and ", n.And.Select(c => Wrap(c, catalog, today)));
        if (n.Or is not null)  return string.Join(" or ", n.Or.Select(c => Wrap(c, catalog, today)));
        if (n.Not is not null) return "not (" + Node(n.Not, catalog, today) + ")";

        if ((n.Exists ?? n.NotExists) is { } sub)
        {
            var e = catalog.Entity(sub.Entity)!;
            var name = e.DisplayName.ToLowerInvariant();
            var head = n.NotExists is not null
                ? (sub.MinCount is > 1 ? $"fewer than {sub.MinCount} {DynamicTemplateBuilder.Plural(name)}" : $"no {name}")
                : (sub.MinCount is > 1 ? $"at least {sub.MinCount} {DynamicTemplateBuilder.Plural(name)}" : $"a {name}");
            return sub.Where is null ? $"has {head}" : $"has {head} where {Node(sub.Where, catalog, today)}";
        }

        FieldRef.TryParse(n.Field, out var r);
        var f = catalog.Entity(r.Entity)?.Field(r.Field);
        var label = f?.DisplayName ?? r.ToString();
        return n.Op switch
        {
            FilterOps.NameMatches => $"{label.ToLowerInvariant()} matches \"{Val(n.Value, today)}\"",
            FilterOps.IsNull => $"{label} is empty",
            FilterOps.IsNotNull => $"{label} is set",
            FilterOps.Eq => $"{label} is {Val(n.Value, today)}",
            FilterOps.Neq => $"{label} is not {Val(n.Value, today)}",
            FilterOps.Gt => $"{label} after {Val(n.Value, today)}",
            FilterOps.Gte => $"{label} on or after {Val(n.Value, today)}",
            FilterOps.Lt => $"{label} before {Val(n.Value, today)}",
            FilterOps.Lte => $"{label} on or before {Val(n.Value, today)}",
            FilterOps.Between => $"{label} between {Val(n.Value?[0], today)} and {Val(n.Value?[1], today)}",
            FilterOps.In => $"{label} is one of {Val(n.Value, today)}",
            FilterOps.NotIn => $"{label} is not one of {Val(n.Value, today)}",
            FilterOps.Contains => $"{label} contains \"{Val(n.Value, today)}\"",
            FilterOps.StartsWith => $"{label} starts with \"{Val(n.Value, today)}\"",
            _ => label
        };
    }

    private static string Wrap(FilterNode n, SemanticCatalog c, DateTime today) =>
        n.And is not null || n.Or is not null ? "(" + Node(n, c, today) + ")" : Node(n, c, today);

    private static string Val(JsonElement? v, DateTime today)
    {
        if (v is not { } e) return "";
        if (e.ValueKind == JsonValueKind.Array) return string.Join(", ", e.EnumerateArray().Select(x => Val(x, today)));
        if (e.ValueKind == JsonValueKind.Object && DateValueResolver.TryResolve(e, today, out var d, out _))
            return d.ToString(DateFormat, CultureInfo.InvariantCulture);
        if (e.ValueKind == JsonValueKind.String) return e.GetString()!;
        return e.GetRawText();
    }
}
