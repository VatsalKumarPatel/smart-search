using System.Globalization;
using System.Text.RegularExpressions;
using SmartSearch.Contracts;
using SmartSearch.Contracts.ViewModels;

namespace SmartSearch.Application.Views;

/// <summary>
/// Turns raw capability results + a ViewTemplate (FieldMap) + links into the
/// SmartSearchResponse the views render. No HTML here.
/// </summary>
public sealed partial class ViewModelComposer(
    IViewTemplateStore templates,
    ILayoutSelector layoutSelector,
    IEnumerable<ILinkProvider> linkProviders)
{
    private const string DateFormat = "dd-MMM-yyyy";
    private const string DateTimeFormat = "dd-MMM-yyyy h:mm tt";

    private readonly Dictionary<string, ILinkProvider> _links =
        linkProviders.ToDictionary(p => p.EntityType, StringComparer.OrdinalIgnoreCase);

    public async Task<SmartSearchResponse> ComposeAsync(ComposeRequest req, CancellationToken ct)
    {
        if (req.Items.Count == 0)
        {
            return new SmartSearchResponse
            {
                QueryId = req.Context.QueryId,
                Query = req.Query,
                InterpretedAs = req.InterpretedAs,
                IntentCode = req.IntentCode,
                Layout = ResultLayout.Empty,
                NameResolution = req.NameResolution,
                FollowUps = req.FollowUps
            };
        }

        var layout = await layoutSelector.SelectAsync(req.Context, req.IntentCode, req.Items.Count, req.RequestedLayout, ct);
        var entityType = req.Items[0].EntityType;
        var map = await templates.GetAsync(req.Context.TenantId, req.IntentCode, entityType, layout, ct);

        var items = req.Items.Select((raw, i) => BuildItem(raw, i + 1, map, req.Context)).ToList();

        return new SmartSearchResponse
        {
            QueryId = req.Context.QueryId,
            Query = req.Query,
            InterpretedAs = req.InterpretedAs,
            IntentCode = req.IntentCode,
            Layout = layout,
            AvailableLayouts = req.Items.Count == 1
                ? [ResultLayout.Panel, ResultLayout.Cards]
                : [ResultLayout.Cards, ResultLayout.List],
            Columns = map.Fields.Where(f => !f.PanelOnly)
                                .Select(f => new ResultColumn(f.Key, f.Label, ParseType(f.Type), f.Sortable))
                                .ToList(),
            Items = items,
            TotalCount = req.TotalCount,
            Paging = req.Paging,
            NameResolution = req.NameResolution,
            FollowUps = req.FollowUps
        };
    }

    private ResultItem BuildItem(ResultItemData raw, int rank, FieldMap map, SearchContext ctx)
    {
        var fields = map.Fields.Select(f => new ResultField(
            f.Key,
            f.Label,
            Format(raw.Fields.GetValueOrDefault(f.Key), ParseType(f.Type)),
            ParseType(f.Type),
            SortValue(raw.Fields.GetValueOrDefault(f.Key)),
            f.PanelOnly ? LayoutVisibility.Panel
                : f.ListOnly ? LayoutVisibility.List
                : LayoutVisibility.All)).ToList();

        var badges = map.Badges
            .Select(b => (def: b, value: raw.Fields.GetValueOrDefault(b.Key)?.ToString()))
            .Where(x => !string.IsNullOrWhiteSpace(x.value))
            .Select(x => new Badge(x.value!, ParseTone(x.def.ToneMap.GetValueOrDefault(x.value!))))
            .ToList();

        var links = _links.TryGetValue(raw.EntityType, out var provider)
            ? provider.GetLinks(raw, ctx).ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ResultLink>();

        var title = Fill(map.Title, raw.Fields);

        return new ResultItem
        {
            EntityType = raw.EntityType,
            EntityId = raw.EntityId,
            ClientId = raw.ClientId,
            Rank = rank,
            Title = title,
            Subtitle = map.Subtitle is null ? null : Fill(map.Subtitle, raw.Fields),
            Initials = Initials(title),
            Fields = fields,
            Badges = badges,
            // links the user may not open were already dropped by the provider
            PrimaryLink = map.PrimaryLink is not null ? links.GetValueOrDefault(map.PrimaryLink) : null,
            SecondaryLinks = map.SecondaryLinks.Select(k => links.GetValueOrDefault(k))
                                               .OfType<ResultLink>().ToList()
        };
    }

    /// <summary>Replaces {Field} and {Field:date|datetime} placeholders. Missing parts are trimmed away.</summary>
    private static string Fill(string template, IReadOnlyDictionary<string, object?> data)
    {
        var text = Placeholder().Replace(template, m =>
        {
            var value = data.GetValueOrDefault(m.Groups["key"].Value);
            var type = m.Groups["fmt"].Success ? ParseType(m.Groups["fmt"].Value) : FieldType.Text;
            return Format(value, type) ?? "";
        });
        // tidy separators left behind by empty values: "DOB  · MRN 12" -> "MRN 12"
        return EmptySegment().Replace(text, "").Trim(' ', '·');
    }

    private static string? Format(object? value, FieldType type) => value switch
    {
        null => null,
        DateTime dt when type == FieldType.DateTime => dt.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString(DateFormat, CultureInfo.InvariantCulture),
        DateTimeOffset dto when type == FieldType.DateTime => dto.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString(DateFormat, CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(DateFormat, CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static string? SortValue(object? value) => value switch
    {
        null => null,
        DateTime dt => dt.ToString("O"),
        DateTimeOffset dto => dto.ToString("O"),
        DateOnly d => d.ToString("O"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static FieldType ParseType(string type) =>
        Enum.TryParse<FieldType>(type, ignoreCase: true, out var t) ? t : FieldType.Text;

    private static BadgeTone ParseTone(string? tone) =>
        Enum.TryParse<BadgeTone>(tone, ignoreCase: true, out var t) ? t : BadgeTone.Neutral;

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    [GeneratedRegex(@"\{(?<key>[A-Za-z0-9_]+)(:(?<fmt>[a-z]+))?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\b[A-Z]{2,4}\s+(?=·|$)")]   // a label like "DOB " or "MRN " whose value was empty
    private static partial Regex EmptySegment();
}

/// <summary>Input to the composer, produced by the pipeline.</summary>
public sealed record ComposeRequest(
    SearchContext Context,
    string Query,
    string IntentCode,
    string? InterpretedAs,
    IReadOnlyList<ResultItemData> Items,
    int TotalCount,
    ResultLayout? RequestedLayout,
    PagingInfo? Paging,
    NameResolution? NameResolution,
    IReadOnlyList<FollowUpSuggestion> FollowUps);

public interface IViewTemplateStore
{
    /// <summary>Tenant template if present, otherwise system default (TenantId 0). Cached in memory.</summary>
    Task<FieldMap> GetAsync(int tenantId, string intentCode, string entityType, ResultLayout layout, CancellationToken ct);
}

public interface ILayoutSelector
{
    /// <summary>Requested layout (toggle) → user preference → ss.LayoutRule by result count.</summary>
    Task<ResultLayout> SelectAsync(SearchContext ctx, string intentCode, int resultCount, ResultLayout? requested, CancellationToken ct);
}
