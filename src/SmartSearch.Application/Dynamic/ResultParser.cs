using System.Data.Common;
using SmartSearch.Application.Views;
using SmartSearch.Contracts;

namespace SmartSearch.Application.Dynamic;

public sealed record DynamicRows(IReadOnlyList<ResultItemData> Items, bool HasMore);

/// <summary>
/// Reads the compiled query's result set into the same ResultItemData the predefined
/// capabilities return, so the rest of the pipeline (composer, links, views) is unchanged.
/// </summary>
public static class ResultParser
{
    public const string AggregateEntityType = "Aggregate";

    public static async Task<DynamicRows> ReadAsync(DbDataReader reader, CompiledQuery query, ValidatedPlan plan, CancellationToken ct)
    {
        var ordinals = query.Columns.Select(c => (Column: c, Ordinal: reader.GetOrdinal(c.Alias))).ToList();
        var items = new List<ResultItemData>(query.Limit);
        var hasMore = false;
        var rank = 0;

        while (await reader.ReadAsync(ct))
        {
            if (items.Count == query.Limit) { hasMore = true; break; }   // we asked for limit + 1

            var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            long key = 0;
            long? clientId = null;

            foreach (var (col, i) in ordinals)
            {
                var value = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                if (col.Alias == SqlCompiler.KeyAlias) key = value is null ? 0 : Convert.ToInt64(value);
                else if (col.Alias == SqlCompiler.ClientAlias) clientId = value is null ? null : Convert.ToInt64(value);
                else fields[col.Alias] = value;
            }

            items.Add(query.IsAggregate
                ? new ResultItemData(AggregateEntityType, ++rank, null, fields)
                : new ResultItemData(plan.Root.LinkEntityType ?? plan.Root.Code, key, clientId, fields));
        }

        return new DynamicRows(items, hasMore);
    }
}

/// <summary>
/// Builds the FieldMap (what to show) on the fly for a dynamic query, from the catalog:
/// title / subtitle / statement come from the root entity, columns from the plan's select list.
/// The Razor views do not know or care that the query was dynamic.
/// </summary>
public static class DynamicTemplateBuilder
{
    public static FieldMap Build(ValidatedPlan vp, SemanticCatalog catalog)
    {
        if (vp.Kind == PlanKind.Aggregate)
        {
            return new FieldMap
            {
                Title = vp.GroupBy.Count == 0
                    ? $"All {Plural(vp.Root.DisplayName).ToLowerInvariant()}"
                    : string.Join(" · ", vp.GroupBy.Select(g => $"{{{g.Alias}}}")),
                Fields = vp.Aggregates.Select(a => new FieldDef
                {
                    Key = a.Alias,
                    Label = Humanize(a.Alias),
                    Type = a.Field?.DataType is "date" ? "date" : a.Field?.DataType is "datetime" ? "datetime" : "number"
                }).ToList()
            };
        }

        var root = vp.Root;
        var visible = vp.Select.Where(f => !vp.TemplateOnly.Contains(f.Alias)).ToList();

        return new FieldMap
        {
            Title = root.TitleTemplate,
            Subtitle = root.SubtitleTemplate,
            Statement = root.StatementTemplate,
            Fields = visible
                .Where(f => !IsStatus(f))
                .Select(f => new FieldDef
                {
                    Key = f.Alias,
                    // prefix joined entity fields so "Status" from Client vs Visit is clear
                    Label = f.EntityCode.Equals(root.Code, StringComparison.OrdinalIgnoreCase) ? f.DisplayName : $"{f.DisplayName}",
                    Type = f.DataType switch
                    {
                        "date" => "date",
                        "datetime" => "datetime",
                        "int" or "decimal" => "number",
                        "personName" => "person",
                        _ => "text"
                    }
                }).ToList(),
            Badges = vp.Select.Where(IsStatus).Select(f => new BadgeDef { Key = f.Alias }).ToList(),
            PrimaryLink = root.PrimaryLink,
            SecondaryLinks = root.SecondaryLinks.ToList()
        };
    }

    private static bool IsStatus(FieldDefinition f) =>
        f.Code.EndsWith("Status", StringComparison.OrdinalIgnoreCase) && f.AllowedValues.Count > 0;

    internal static string Plural(string word) =>
        word.EndsWith("sis", StringComparison.OrdinalIgnoreCase) ? word[..^2] + "es"
        : word.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? word
        : word + "s";

    private static string Humanize(string alias)
    {
        var s = alias.Replace('_', ' ');
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
