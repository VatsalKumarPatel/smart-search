using SmartSearch.Application.Views;
using SmartSearch.Contracts;
using SmartSearch.Contracts.ViewModels;

namespace SmartSearch.Application.Dynamic;

/// <summary>
/// Turns a DynamicSearchResult into the same response shape the predefined intents produce.
/// One part → a normal response. Several parts → an outer response with one section per part,
/// each section composed by ViewModelComposer (so it gets its own layout, links and statements).
/// </summary>
public static class DynamicResponseMapper
{
    public const string IntentCode = "Dynamic";

    public static async Task<SmartSearchResponse> MapAsync(DynamicSearchResult r, SearchContext ctx, string question,
        ResultLayout? requestedLayout, ViewModelComposer composer, CancellationToken ct)
    {
        switch (r.Outcome)
        {
            case DynamicOutcome.Clarify:
                return new SmartSearchResponse
                {
                    QueryId = ctx.QueryId, Query = question, IntentCode = IntentCode, Layout = ResultLayout.Empty,
                    Message = new SearchMessage(MessageKind.Info, r.Clarify?.Question ?? "Can you be more specific?"),
                    // each option re-asks the question with the choice added
                    FollowUps = (r.Clarify?.Options ?? []).Select(o => new FollowUpSuggestion($"{question} ({o})", "#")).ToList()
                };

            case DynamicOutcome.NameSuggestions or DynamicOutcome.NameNotFound:
                return new SmartSearchResponse
                {
                    QueryId = ctx.QueryId, Query = question, IntentCode = IntentCode, Layout = ResultLayout.Empty,
                    NameResolution = r.NameResolution
                };

            case DynamicOutcome.Rejected:
                return new SmartSearchResponse
                {
                    QueryId = ctx.QueryId, Query = question, IntentCode = IntentCode, Layout = ResultLayout.Empty,
                    Message = new SearchMessage(MessageKind.Warning, r.Message ?? "Something went wrong.")
                };
        }

        if (r.Sections.Count == 1)
            return await SectionAsync(r.Sections[0], ctx, question, requestedLayout, composer, ct) with { SectionTitle = null };

        var sections = new List<SmartSearchResponse>();
        foreach (var s in r.Sections)
            sections.Add(await SectionAsync(s, ctx, question, layout: null, composer, ct));

        return new SmartSearchResponse
        {
            QueryId = ctx.QueryId,
            Query = question,
            IntentCode = IntentCode,
            InterpretedAs = string.Join(" and ", r.Sections.Select(s => s.Title.ToLowerInvariant())),
            Layout = ResultLayout.Empty,
            Sections = sections,
            TotalCount = sections.Sum(s => s.TotalCount)
        };
    }

    private static async Task<SmartSearchResponse> SectionAsync(DynamicSection s, SearchContext ctx, string question,
        ResultLayout? layout, ViewModelComposer composer, CancellationToken ct)
    {
        if (s.Outcome != SectionOutcome.Results)
            return new SmartSearchResponse
            {
                QueryId = ctx.QueryId, Query = question, IntentCode = IntentCode, SectionTitle = s.Title,
                InterpretedAs = s.InterpretedAs, Layout = ResultLayout.Empty,
                Message = new SearchMessage(s.Outcome == SectionOutcome.NoAccess ? MessageKind.Info : MessageKind.Warning, s.Message ?? "")
            };

        var composed = await composer.ComposeAsync(new ComposeRequest(
            ctx, question, IntentCode, s.InterpretedAs, s.Items, s.Items.Count, layout,
            Paging: null, NameResolution: null, FollowUps: [],
            TemplateOverride: s.Template, Notes: s.Notes, HasMore: s.HasMore), ct);

        // inside a multi-part answer each section keeps its own layout; the toggle applies to single answers only
        return composed with { SectionTitle = s.Title, AvailableLayouts = layout is null ? [] : composed.AvailableLayouts };
    }
}
