using SmartSearch.Contracts.ViewModels;
using SmartSearch.Web.Controllers;

namespace SmartSearch.Web.Preview;

public sealed record PreviewScenario(string Key, string Label, string SampleQuery);

public static class PreviewScenarios
{
    public static readonly IReadOnlyList<PreviewScenario> All =
    [
        new("panel",  "Single result (Panel)",       "Get me latest visit for vatsal patel"),
        new("cards",  "Several results (Cards)",     "Get me latest visit for vatsal"),
        new("list",   "Many results (List)",         "Show visits for patel"),
        new("statements", "Sentences with links (Summary)", "Summarize recent visits for vatsal"),
        new("multi",  "Two questions in one (Medication + Billing)", "Provide me details for last medication for vatsal and his last billing details"),
        new("suggest","Misspelled name (Did you mean)", "Get me latest visit for vatsl"),
        new("empty",  "No results",                  "Latest visit for zzqx"),
        new("error",  "Error",                       "trigger error"),
    ];
}

/// <summary>Fake data for the design preview. Not used in production.</summary>
public static class PreviewData
{
    private static readonly Guid QueryId = Guid.Parse("7d3f6a2e-0000-4000-8000-000000000001");

    private sealed record FakeClient(long Id, string First, string Last, string Dob, string Mrn);
    private sealed record FakeVisit(long Id, FakeClient Client, string When, string Type, string Provider, string Program, string Location, string Status);

    private static readonly FakeClient[] Clients =
    [
        new(1042, "Vatsal",  "Patel",  "12-Mar-1988", "100245"),
        new(1187, "Vatsal",  "Shah",   "04-Jul-1992", "100871"),
        new(2210, "Vatsala", "Mehta",  "19-Jan-1979", "101552"),
        new(3305, "Ravi",    "Vatsal", "28-Nov-2001", "102310"),
    ];

    private static readonly string[] Providers = ["Dr. Anita Shah", "Dr. Mark Lewis", "Priya Nair, LCSW", "Dr. Omar Haddad"];
    private static readonly string[] Types = ["Individual therapy", "Psychiatric eval", "Medication management", "Group session"];
    private static readonly string[] Statuses = ["Signed", "Draft", "Completed", "No show"];

    public static SmartSearchResponse Build(string? scenario, SearchRequest req)
    {
        scenario = (scenario ?? Guess(req.Q)).ToLowerInvariant();

        if (req.ClientId is long pinned)   // user picked a did-you-mean suggestion
            return Visits(req, [Visit(Clients.First(c => c.Id == pinned), 0)], ResultLayout.Panel, "Latest visit for Vatsal Patel");

        return scenario switch
        {
            "panel"   => Visits(req, [Visit(Clients[0], 0)], req.Layout ?? ResultLayout.Panel, "Latest visit for clients matching \"vatsal patel\""),
            "cards"   => Visits(req, Clients.Select((c, i) => Visit(c, i)).ToList(), req.Layout ?? ResultLayout.Cards, "Latest visit for clients matching \"vatsal\""),
            "list"    => Visits(req, ManyVisits(), req.Layout ?? ResultLayout.List, "Visits for clients matching \"patel\"", paged: true),
            "statements" => Visits(req, ManyVisits().Take(6).ToList(), req.Layout ?? ResultLayout.Statements, "Recent visits for clients matching \"vatsal\""),
            "multi"   => Multi(req),
            "suggest" => Suggest(req),
            "empty"   => Empty(req),
            "error"   => new SmartSearchResponse
            {
                QueryId = QueryId, Query = req.Q ?? "", Layout = ResultLayout.Empty,
                Message = new SearchMessage(MessageKind.Error, "Smart search is unavailable right now. Please try again or use the regular client search.")
            },
            _ => Visits(req, Clients.Select((c, i) => Visit(c, i)).ToList(), req.Layout ?? ResultLayout.Cards, "Latest visit")
        };
    }

    private static string Guess(string? q)
    {
        q = (q ?? "").ToLowerInvariant();
        if (q.Contains("error")) return "error";
        if (q.Contains("vatsl") || q.Contains("watsal") || q.Contains("vastal")) return "suggest";
        if (q.Contains("zz")) return "empty";
        if (q.Contains("medication") && q.Contains("bill")) return "multi";
        if (q.Contains("summar")) return "statements";
        if (q.Contains("vatsal patel")) return "panel";
        if (q.Contains("patel") || q.Contains("visits")) return "list";
        return "cards";
    }

    private static FakeVisit Visit(FakeClient c, int i) =>
        new(88213 + i, c, $"0{2 + i % 7}-Nov-2026 {9 + i % 8}:30 AM", Types[i % 4], Providers[i % 4],
            i % 2 == 0 ? "Adult Outpatient" : "Youth Services", "Vadodara Clinic", Statuses[i % 4]);

    private static List<FakeVisit> ManyVisits() =>
        Enumerable.Range(0, 12).Select(i => Visit(Clients[i % 2 == 0 ? 0 : 1], i)).ToList();

    private static SmartSearchResponse Visits(SearchRequest req, IReadOnlyList<FakeVisit> visits, ResultLayout layout, string interpreted, bool paged = false)
    {
        var page = Math.Max(1, req.Page);
        const int pageSize = 8;
        var shown = paged ? visits.Skip((page - 1) * pageSize).Take(pageSize).ToList() : visits;

        return new SmartSearchResponse
        {
            QueryId = QueryId,
            Query = req.Q ?? "",
            InterpretedAs = interpreted,
            IntentCode = "GetLatestVisit",
            Layout = layout,
            AvailableLayouts = visits.Count == 1
                ? [ResultLayout.Panel, ResultLayout.Cards, ResultLayout.Statements]
                : [ResultLayout.Cards, ResultLayout.List, ResultLayout.Statements],
            Columns =
            [
                new("VisitDate", "Last visit", FieldType.DateTime, true),
                new("VisitType", "Visit type", FieldType.Text, true),
                new("ProviderName", "Provider", FieldType.Person, true),
            ],
            Items = shown.Select((v, i) => ToItem(v, i + 1)).ToList(),
            TotalCount = visits.Count,
            Paging = paged ? new PagingInfo(page, pageSize, (int)Math.Ceiling(visits.Count / (double)pageSize)) : null,
            NameResolution = new NameResolution { Status = NameResolutionStatus.Matched, Input = "vatsal" },
            FollowUps = visits.Count == 1
                ? [new("Show all visits for Vatsal Patel", "#"), new("Upcoming appointments for Vatsal Patel", "#")]
                : [new("Only visits from last month", "#")]
        };
    }

    private static ResultItem ToItem(FakeVisit v, int rank) => new()
    {
        EntityType = "Visit",
        EntityId = v.Id,
        ClientId = v.Client.Id,
        Rank = rank,
        Title = $"{v.Client.First} {v.Client.Last}",
        Subtitle = $"DOB {v.Client.Dob} · MRN {v.Client.Mrn}",
        Initials = $"{v.Client.First[0]}{v.Client.Last[0]}",
        Fields =
        [
            new("VisitDate", "Last visit", v.When, FieldType.DateTime, v.When),
            new("VisitType", "Visit type", v.Type, FieldType.Text, v.Type),
            new("ProviderName", "Provider", v.Provider, FieldType.Person, v.Provider),
            new("Program", "Program", v.Program, FieldType.Text, ShowIn: LayoutVisibility.Panel),
            new("Location", "Location", v.Location, FieldType.Text, ShowIn: LayoutVisibility.Panel),
        ],
        Badges = [new(v.Status, v.Status switch
        {
            "Signed" or "Completed" => BadgeTone.Success,
            "Draft" => BadgeTone.Warning,
            _ => BadgeTone.Danger
        })],
        // Same as ViewModelComposer output for:
        // "[{FirstName} {LastName}](ClientChart) was seen for [{VisitType}](VisitDetail) by **{ProviderName}** on {VisitDate:datetime} ~~at {Location}~~"
        Statement =
        [
            new($"{v.Client.First} {v.Client.Last}", SegmentStyle.Plain, new("ClientChart", "Client chart", $"/Client/{v.Client.Id}/Chart?src=smartsearch&q={QueryId}")),
            new(" was seen for "),
            new(v.Type, SegmentStyle.Plain, new("VisitDetail", "Open visit", $"/Client/{v.Client.Id}/Visit/{v.Id}?src=smartsearch&q={QueryId}")),
            new(" by "),
            new(v.Provider, SegmentStyle.Strong),
            new($" on {v.When} "),
            new($"at {v.Location}", SegmentStyle.Muted),
        ],
        PrimaryLink = new("VisitDetail", "Open visit", $"/Client/{v.Client.Id}/Visit/{v.Id}?src=smartsearch&q={QueryId}"),
        SecondaryLinks =
        [
            new("ClientChart", "Client chart", $"/Client/{v.Client.Id}/Chart?src=smartsearch&q={QueryId}"),
            new("ClientVisits", "All visits", $"/Client/{v.Client.Id}/Visits?src=smartsearch&q={QueryId}"),
        ]
    };

    /// <summary>One question, two domains: each part is its own section with its own layout and links.</summary>
    private static SmartSearchResponse Multi(SearchRequest req)
    {
        static ResultLink L(string key, string label, string url) => new(key, label, $"{url}?src=smartsearch&q={QueryId}");

        var med = new SmartSearchResponse
        {
            QueryId = QueryId, Query = req.Q ?? "", IntentCode = "Dynamic", SectionTitle = "Last medication",
            InterpretedAs = "Latest medication per client, where client name matches \"vatsal\", newest first",
            Layout = ResultLayout.Panel, TotalCount = 1,
            Items =
            [
                new ResultItem
                {
                    EntityType = "Medication", EntityId = 5501, ClientId = 1042, Rank = 1,
                    Title = "Vatsal Patel", Subtitle = "Sertraline 50 mg", Initials = "VP",
                    Fields =
                    [
                        new("Medication_DrugName", "Medication", "Sertraline", FieldType.Text),
                        new("Medication_Strength", "Strength", "50 mg", FieldType.Text),
                        new("Medication_Frequency", "Frequency", "Once daily", FieldType.Text),
                        new("Medication_Prescriber", "Prescriber", "Dr. Anita Shah", FieldType.Person),
                        new("Medication_StartDate", "Start date", "20-Oct-2026", FieldType.Date),
                    ],
                    Badges = [new("Active", BadgeTone.Success)],
                    PrimaryLink = L("MedicationDetail", "Open medication", "/Client/1042/Medications/5501"),
                    SecondaryLinks = [L("ClientChart", "Client chart", "/Client/1042/Chart")]
                }
            ]
        };

        var bill = new SmartSearchResponse
        {
            QueryId = QueryId, Query = req.Q ?? "", IntentCode = "Dynamic", SectionTitle = "Last billing",
            InterpretedAs = "Latest billing charge per client, where client name matches \"vatsal\", newest first",
            Layout = ResultLayout.Panel, TotalCount = 1,
            Items =
            [
                new ResultItem
                {
                    EntityType = "Charge", EntityId = 9901, ClientId = 1042, Rank = 1,
                    Title = "Vatsal Patel", Subtitle = "02-Nov-2026 · 90834", Initials = "VP",
                    Fields =
                    [
                        new("Charge_ServiceDate", "Service date", "02-Nov-2026", FieldType.Date),
                        new("Charge_ProcedureCode", "Procedure code", "90834 (Psychotherapy, 45 min)", FieldType.Text),
                        new("Charge_BilledAmount", "Billed amount", "$145.00", FieldType.Number),
                        new("Charge_Payer", "Payer", "Medicaid", FieldType.Text),
                        new("Charge_Balance", "Balance due", "$145.00", FieldType.Number),
                    ],
                    Badges = [new("Submitted", BadgeTone.Info)],
                    PrimaryLink = L("ChargeDetail", "Open charge", "/Billing/Charges/9901"),
                    SecondaryLinks = [L("ClientChart", "Client chart", "/Client/1042/Chart")]
                }
            ]
        };

        return new SmartSearchResponse
        {
            QueryId = QueryId, Query = req.Q ?? "", IntentCode = "Dynamic",
            InterpretedAs = "last medication and last billing",
            Layout = ResultLayout.Empty, TotalCount = 2,
            Sections = [med, bill]
        };
    }

    private static SmartSearchResponse Suggest(SearchRequest req) => new()
    {
        QueryId = QueryId,
        Query = req.Q ?? "",
        InterpretedAs = "Latest visit for clients matching \"vatsl\"",
        IntentCode = "GetLatestVisit",
        Layout = ResultLayout.Empty,
        NameResolution = new NameResolution
        {
            Status = NameResolutionStatus.Suggest,
            Input = "vatsl",
            Suggestions =
            [
                new(1042, "Vatsal Patel",  "DOB 12-Mar-1988 · MRN 100245", 0.91, $"?queryId={QueryId}&clientId=1042"),
                new(1187, "Vatsal Shah",   "DOB 04-Jul-1992 · MRN 100871", 0.90, $"?queryId={QueryId}&clientId=1187"),
                new(2210, "Vatsala Mehta", "DOB 19-Jan-1979 · MRN 101552", 0.84, $"?queryId={QueryId}&clientId=2210"),
            ]
        }
    };

    private static SmartSearchResponse Empty(SearchRequest req) => new()
    {
        QueryId = QueryId,
        Query = req.Q ?? "",
        InterpretedAs = "Latest visit for clients matching \"zzqx\"",
        IntentCode = "GetLatestVisit",
        Layout = ResultLayout.Empty,
        NameResolution = new NameResolution { Status = NameResolutionStatus.NotFound, Input = "zzqx" },
        FollowUps = [new("Open the regular client search", "/Clients/Search")]
    };
}
