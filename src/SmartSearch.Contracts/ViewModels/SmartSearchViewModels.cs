namespace SmartSearch.Contracts.ViewModels;

/// <summary>
/// The ONLY contract the Razor views depend on.
/// When the final design arrives, change the .cshtml / .css files only.
/// Add properties here only if the new design needs data that does not exist yet.
/// Every string is already formatted for display (dates, names, labels).
/// </summary>
public sealed record SmartSearchResponse
{
    public Guid QueryId { get; init; }
    public string Query { get; init; } = "";
    public string? InterpretedAs { get; init; }          // "Latest visit for clients matching \"vatsal\""
    public string? IntentCode { get; init; }

    public ResultLayout Layout { get; init; }
    public IReadOnlyList<ResultLayout> AvailableLayouts { get; init; } = [];

    /// <summary>Column definitions for List layout (same order as each item's Fields).</summary>
    public IReadOnlyList<ResultColumn> Columns { get; init; } = [];
    public IReadOnlyList<ResultItem> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public PagingInfo? Paging { get; init; }

    public NameResolution? NameResolution { get; init; }

    /// <summary>Follow-up prompt chips, e.g. "Show all visits for Vatsal Patel".</summary>
    public IReadOnlyList<FollowUpSuggestion> FollowUps { get; init; } = [];

    /// <summary>Set when the search failed; views show a friendly message.</summary>
    public SearchMessage? Message { get; init; }
}

/// <summary>
/// Statements = one sentence per result with links inside the sentence,
/// e.g. "Vatsal Patel was seen for Psychiatric eval by Dr. Shah on 02-Nov-2026".
/// </summary>
public enum ResultLayout { Panel, Cards, List, Statements, Empty }

public sealed record ResultColumn(string Key, string Label, FieldType Type, bool Sortable = false);

public sealed record ResultItem
{
    public required string EntityType { get; init; }     // "Visit", "Client"
    public required long EntityId { get; init; }
    public long? ClientId { get; init; }
    public int Rank { get; init; }

    public required string Title { get; init; }          // "Vatsal Patel"
    public string? Subtitle { get; init; }               // "DOB 12-Mar-1988 · MRN 100245"
    public string? Initials { get; init; }               // "VP" - for avatar circle

    /// <summary>All fields for Panel. Cards / List use the ones where ShowIn includes that layout.</summary>
    public IReadOnlyList<ResultField> Fields { get; init; } = [];
    public IReadOnlyList<Badge> Badges { get; init; } = [];

    public ResultLink? PrimaryLink { get; init; }
    public IReadOnlyList<ResultLink> SecondaryLinks { get; init; } = [];

    /// <summary>
    /// The result as a sentence, split into segments. Filled when the ViewTemplate has a "statement".
    /// Views render segments in order; a segment with a Link becomes an anchor.
    /// Never contains raw HTML, so Razor encodes every piece.
    /// </summary>
    public IReadOnlyList<StatementSegment> Statement { get; init; } = [];
}

/// <summary>One piece of a statement sentence.</summary>
public sealed record StatementSegment(string Text, SegmentStyle Style = SegmentStyle.Plain, ResultLink? Link = null);

public enum SegmentStyle { Plain, Strong, Muted }

public sealed record ResultField(
    string Key,
    string Label,
    string? Value,          // display string, "—" handled by the view when null
    FieldType Type,
    string? SortValue = null,  // ISO date / raw value, for client-side sort in List
    LayoutVisibility ShowIn = LayoutVisibility.All);

public enum FieldType { Text, Date, DateTime, Person, Badge, Number }

[Flags]
public enum LayoutVisibility { Panel = 1, Cards = 2, List = 4, All = Panel | Cards | List }

public sealed record Badge(string Text, BadgeTone Tone);

public enum BadgeTone { Neutral, Info, Success, Warning, Danger }

public sealed record ResultLink(
    string Key,             // "VisitDetail", "ClientChart" - used for click tracking
    string Label,           // "Open visit"
    string Url,             // already permission-checked, includes ?src=smartsearch&q={queryId}
    string? Icon = null);   // optional icon name for the design system

public sealed record PagingInfo(int Page, int PageSize, int TotalPages)
{
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}

public sealed record NameResolution
{
    public NameResolutionStatus Status { get; init; }
    public string Input { get; init; } = "";              // what the user typed, e.g. "vatsl"
    public IReadOnlyList<NameSuggestion> Suggestions { get; init; } = [];
}

public enum NameResolutionStatus { NotNeeded, Matched, Suggest, NotFound }

public sealed record NameSuggestion(
    long ClientId,
    string DisplayName,     // "Vatsal Patel"
    string? Detail,         // "DOB 12-Mar-1988 · MRN 100245"
    double Score,
    string RerunUrl);       // re-runs the same question pinned to this ClientId

public sealed record FollowUpSuggestion(string Text, string Url);

public sealed record SearchMessage(MessageKind Kind, string Text);

public enum MessageKind { Info, Warning, Error }
