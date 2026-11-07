// Dependency-free test runner (no NuGet needed): dotnet run --project tests/SmartSearch.Tests
// Exercises the dynamic query path end to end without AWS or SQL Server:
// validator → compiler → describer → result parser, plus the Bedrock response parser.
// Writes every generated SQL statement to tests/SmartSearch.Tests/out/*.sql for review / syntax checks.

using System.Data;
using System.Text.Json;
using SmartSearch.Application.Dynamic;
using SmartSearch.Application.Views;

var today = new DateTime(2026, 11, 7);
var catalog = TestCatalog.Build();
var validator = new QueryPlanValidator();
var compiler = new SqlCompiler(new SqlCompilerOptions());
var allowAll = (string _) => true;
var outDir = Path.Combine(AppContext.BaseDirectory, "../../../out");
Directory.CreateDirectory(outDir);
var failures = 0;
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

void Check(bool condition, string what)
{
    Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {what}");
    if (!condition) failures++;
}

QueryPlan Plan(string s) => JsonSerializer.Deserialize<QueryPlan>(s, json)!;

(ValidationResult v, CompiledQuery? q) Run(string name, string planJson, Action<FilterNode>? resolveNames = null)
{
    Console.WriteLine($"\n== {name}");
    var v = validator.Validate(Plan(planJson), catalog, allowAll, today);
    if (!v.IsValid) { Console.WriteLine("  errors: " + string.Join("; ", v.Errors)); return (v, null); }
    foreach (var n in v.Plan!.NameFilters) resolveNames?.Invoke(n);
    var q = compiler.Compile(v.Plan!, catalog, tenantId: 7, userId: 42, today);
    File.WriteAllText(Path.Combine(outDir, name + ".sql"), q.Sql);
    Console.WriteLine("  interpreted: " + PlanDescriber.Describe(v.Plan!, catalog, today));
    if (v.Plan!.Notes.Count > 0) Console.WriteLine("  notes: " + string.Join(" | ", v.Plan.Notes));
    return (v, q);
}

// ---------------------------------------------------------------- the five seed examples
{
    var (v, q) = Run("01_depression_not_seen_90d", """
      {"kind":"rows","entity":"Client","select":["Client.FirstName","Client.LastName","Client.DateOfBirth","Client.PrimaryProgram"],
       "where":{"and":[
         {"exists":{"entity":"Diagnosis","where":{"and":[{"field":"Diagnosis.Code","op":"startsWith","value":"F32"},{"field":"Diagnosis.EndDate","op":"isNull"}]}}},
         {"notExists":{"entity":"Visit","where":{"field":"Visit.VisitDate","op":"gte","value":{"relative":"-90d"}}}}]},
       "orderBy":[{"field":"Client.LastName","dir":"asc"}],"limit":100}
      """);
    Check(v.IsValid, "valid");
    Check(q!.Sql.Contains("EXISTS (SELECT 1 FROM [ss].[v_Diagnosis] AS e1"), "diagnosis EXISTS subquery");
    Check(q.Sql.Contains("NOT EXISTS (SELECT 1 FROM [ss].[v_Visit] AS e2"), "visit NOT EXISTS subquery");
    Check(q.Sql.Split("[v_UserClientAccess]").Length - 1 == 3, "access filter on root AND inside both subqueries");
    Check(q.Parameters.Any(p => p.Value is string s && s == "F32%"), "startsWith bound as parameter 'F32%'");
    Check(q.Parameters.Any(p => p.Value is DateTime d && d == today.AddDays(-90)), "relative -90d resolved to a date parameter");
    Check(!q.Sql.Contains("F32"), "no literal values inside SQL text");
    Check(q.Parameters.First(p => p.Name == "@__limit").Value is 101, "TOP = limit + 1");
}
{
    var (v, q) = Run("02_no_shows_last_month_dr_shah", """
      {"kind":"rows","entity":"Visit","select":["Visit.VisitDate","Visit.VisitType","Client.FirstName","Client.LastName"],
       "where":{"and":[{"field":"Visit.Status","op":"eq","value":"No show"},{"field":"Visit.ProviderName","op":"contains","value":"Shah"}]},
       "timeRange":{"from":{"startOf":"month","offset":-1},"to":{"startOf":"month","offset":0}},
       "orderBy":[{"field":"Visit.VisitDate","dir":"desc"}]}
      """);
    Check(v.IsValid, "valid");
    Check(q!.Sql.Contains("LEFT JOIN [ss].[v_Client] AS j1"), "joins Client (many-to-one) for the name columns");
    Check(v.Plan!.From == new DateTime(2026, 10, 1) && v.Plan.To == new DateTime(2026, 11, 1), "last month = 01-Oct to 01-Nov (exclusive)");
    Check(v.Plan.Notes.Count == 0, "no default-window note when a range was given");
    Check(q.Parameters.Any(p => p.Value is string s && s == "%Shah%"), "contains → %Shah%");
}
{
    var (v, q) = Run("03_visits_per_program_this_year", """
      {"kind":"aggregate","entity":"Visit","groupBy":["Visit.Program"],"aggregates":[{"fn":"count","alias":"visits"}],
       "timeRange":{"from":{"startOf":"year","offset":0}},"orderBy":[{"field":"visits","dir":"desc"}],"limit":50}
      """);
    Check(v.IsValid, "valid");
    Check(q!.IsAggregate && q.Sql.Contains("COUNT_BIG(*) AS [visits]") && q.Sql.Contains("GROUP BY r.[Program]"), "GROUP BY with COUNT_BIG");
    Check(q.Sql.Contains("ORDER BY [visits] DESC"), "orders by aggregate alias");
}
{
    var (v, q) = Run("04_more_than_3_no_shows", """
      {"kind":"rows","entity":"Client","select":["Client.FirstName","Client.LastName","Client.PrimaryClinician"],
       "where":{"exists":{"entity":"Visit","minCount":4,"where":{"and":[{"field":"Visit.Status","op":"eq","value":"No show"},{"field":"Visit.VisitDate","op":"gte","value":{"relative":"-6m"}}]}}}}
      """);
    Check(v.IsValid, "valid");
    Check(q!.Sql.Contains("(SELECT COUNT_BIG(*) FROM [ss].[v_Visit] AS e1") && q.Sql.Contains(">= @p"), "minCount → COUNT subquery >= 4");
}
{
    var (v, q) = Run("05_latest_visit_for_vatsal", """
      {"kind":"rows","entity":"Visit","select":["Visit.VisitDate","Visit.VisitType","Visit.ProviderName","Visit.Status"],
       "where":{"field":"Client.FullName","op":"nameMatches","value":"vatsal"},
       "orderBy":[{"field":"Visit.VisitDate","dir":"desc"}],"latestPerClient":true,"limit":25}
      """, n => n.ResolvedClientIds = [1042, 1187]);
    Check(v.IsValid, "valid");
    Check(q!.Sql.Contains("ROW_NUMBER() OVER (PARTITION BY r.[ClientId]"), "latest per client via ROW_NUMBER");
    Check(q.Sql.Contains("r.[ClientId] IN (@p"), "name resolved to client ids, filtered on the root table (index seek)");
    Check(v.Plan!.From is null && v.Plan.Notes.Count == 0, "named person → full history, no 12-month default window");
}

// ---------------------------------------------------------------- safety: plans that must be rejected
Console.WriteLine("\n== rejected plans");
void Rejects(string what, string planJson, string expect, Func<string, bool>? perms = null)
{
    var v = validator.Validate(Plan(planJson), catalog, perms ?? allowAll, today);
    Check(!v.IsValid && v.Errors.Any(e => e.Contains(expect, StringComparison.OrdinalIgnoreCase)), $"{what}  →  {string.Join("; ", v.Errors)}");
}
Rejects("unknown entity", """{"kind":"rows","entity":"Users"}""", "unknown entity");
Rejects("restricted field (SSN)", """{"kind":"rows","entity":"Client","select":["Client.Ssn"]}""", "restricted");
Rejects("restricted field in filter", """{"kind":"rows","entity":"Visit","where":{"field":"Visit.NoteText","op":"contains","value":"suicid"}}""", "restricted");
Rejects("unknown field", """{"kind":"rows","entity":"Client","select":["Client.Password"]}""", "unknown field");
Rejects("SQL in a field name", """{"kind":"rows","entity":"Client","select":["Client.FirstName; DROP TABLE x"]}""", "Entity.Field");
Rejects("one-to-many without exists", """{"kind":"rows","entity":"Client","select":["Visit.VisitDate"]}""", "not reachable");
Rejects("wrong operator for type", """{"kind":"rows","entity":"Visit","where":{"field":"Visit.VisitDate","op":"contains","value":"2024"}}""", "not allowed");
Rejects("bad date", """{"kind":"rows","entity":"Visit","where":{"field":"Visit.VisitDate","op":"gte","value":"yesterday-ish"}}""", "not a date");
Rejects("node with two shapes", """{"kind":"rows","entity":"Visit","where":{"field":"Visit.Status","op":"eq","value":"Draft","and":[]}}""", "exactly one");
Rejects("bad aggregate alias", """{"kind":"aggregate","entity":"Visit","aggregates":[{"fn":"count","alias":"x]; DROP"}]}""", "alias");
Rejects("sum on text", """{"kind":"aggregate","entity":"Visit","aggregates":[{"fn":"sum","field":"Visit.Status","alias":"s"}]}""", "not valid");
Rejects("field from another entity inside exists", """{"kind":"rows","entity":"Client","where":{"exists":{"entity":"Visit","where":{"field":"Client.Status","op":"eq","value":"Active"}}}}""", "inside exists");
Rejects("no permission for Diagnosis", """{"kind":"rows","entity":"Client","where":{"exists":{"entity":"Diagnosis"}}}""", "access", p => p != "Diagnosis.View");
Rejects("latestPerClient on Staff", """{"kind":"rows","entity":"Staff","latestPerClient":true}""", "belongs to a client");

// ---------------------------------------------------------------- limits and history guard
Console.WriteLine("\n== limits");
{
    var v = validator.Validate(Plan("""{"kind":"rows","entity":"Visit","limit":100000,"timeRange":{"from":"1990-01-01"}}"""), catalog, allowAll, today);
    Check(v.IsValid && v.Plan!.Limit == 500, "limit capped at entity MaxLimit (500)");
    Check(v.Plan!.From == today.AddMonths(-240), "1990 start clamped to 20 years of history");
}
{
    var v = validator.Validate(Plan("""{"kind":"rows","entity":"Visit","where":{"field":"Visit.VisitDate","op":"between","value":["2015-01-01","2015-12-31"]}}"""), catalog, allowAll, today);
    Check(v.IsValid && v.Plan!.From is null, "explicit date filter → no default 12-month window");
    var q = compiler.Compile(v.Plan!, catalog, 7, 42, today);
    Check(q.Parameters.Any(p => p.Value is DateTime d && d == new DateTime(2016, 1, 1)), "between end date is inclusive (< next day)");
}
{
    var bigIn = "[" + string.Join(",", Enumerable.Range(0, 150).Select(i => $"\"v{i}\"")) + "]";
    var v = validator.Validate(Plan("""{"kind":"rows","entity":"Visit","where":{"field":"Visit.Status","op":"in","value":""" + bigIn + "}}"), catalog, allowAll, today);
    Check(!v.IsValid, "IN list over 100 values rejected");
}
{
    var (_, q) = Run("06_like_escaping", """{"kind":"rows","entity":"Client","where":{"field":"Client.LastName","op":"contains","value":"50%_off[x]"}}""");
    Check(q!.Parameters.Any(p => p.Value is string s && s == @"%50\%\_off\[x]%"), "LIKE wildcards in user text are escaped");
}

// ---------------------------------------------------------------- result parser + template
Console.WriteLine("\n== result parser");
{
    var (v, q) = Run("07_parser_rows", """{"kind":"rows","entity":"Visit","select":["Visit.VisitDate","Visit.VisitType","Visit.Status"],"limit":2}""");
    var table = new DataTable();
    foreach (var c in q!.Columns) table.Columns.Add(c.Alias, typeof(object));
    for (var i = 0; i < 3; i++)   // 3 rows for limit 2 → HasMore
    {
        var row = table.NewRow();
        foreach (var c in q.Columns)
            row[c.Alias] = c.Alias switch
            {
                SqlCompiler.KeyAlias => 88213L + i,
                SqlCompiler.ClientAlias => 1042L,
                "Visit_VisitDate" => new DateTime(2026, 11, 2 + i, 9, 30, 0),
                "Visit_Status" => "Signed",
                "Client_FirstName" => "Vatsal",
                "Client_LastName" => "Patel",
                _ => (object)DBNull.Value
            };
        table.Rows.Add(row);
    }
    var rows = await ResultParser.ReadAsync(table.CreateDataReader(), q, v.Plan!, CancellationToken.None);
    Check(rows.Items.Count == 2 && rows.HasMore, "returns limit rows and flags HasMore");
    Check(rows.Items[0].EntityType == "Visit" && rows.Items[0].EntityId == 88213 && rows.Items[0].ClientId == 1042, "key / client id mapped for links");
    Check(rows.Items[0].Fields["Visit_VisitType"] is null, "DBNull → null");
    var map = DynamicTemplateBuilder.Build(v.Plan!, catalog);
    Check(map.Title == "{Client_FirstName} {Client_LastName}" && map.Statement!.Contains("](VisitDetail)"), "template from catalog (title + statement with links)");
    Check(map.Badges.Any(b => b.Key == "Visit_Status"), "status field shown as a badge");
    Check(q.Columns.Any(c => c.Alias == "Client_FirstName" && c.Hidden), "title fields auto-selected but hidden as columns");
}

// ---------------------------------------------------------------- Bedrock response parsing
Console.WriteLine("\n== planner response");
{
    var raw = """
      {"id":"msg_1","type":"message","role":"assistant","content":[
        {"type":"tool_use","id":"toolu_1","name":"build_query_plan","input":{"kind":"clarify","clarify":{"question":"Which Vatsal?","options":["Vatsal Patel","Vatsal Shah"]}}}],
       "usage":{"input_tokens":1800,"output_tokens":60}}
      """;
    var r = BedrockQueryPlanner.Parse(raw);
    Check(r.Plan?.Clarify?.Options.Count == 2 && r.Plan.Parts.Count == 0 && r.ToolUseId == "toolu_1" && r.InputTokens == 1800, "tool_use input → SearchPlan (clarify)");
    var single = BedrockQueryPlanner.Parse("""{"content":[{"type":"tool_use","id":"t","name":"build_query_plan","input":{"kind":"rows","entity":"Visit"}}]}""");
    Check(single.Plan?.Parts.Count == 1 && single.Plan.Parts[0].Entity == "Visit", "bare single plan is wrapped as one part");
    var bad = BedrockQueryPlanner.Parse("""{"content":[{"type":"text","text":"SELECT * FROM Clients"}]}""");
    Check(bad.Plan is null && bad.Error is not null, "plain text / SQL reply is not accepted");
}

// ---------------------------------------------------------------- prompt
Console.WriteLine("\n== prompt");
{
    var ents = SchemaRetriever.Select("how many no shows per provider last quarter", catalog);
    Check(ents.Any(e => e.Code == "Visit") && ents.Any(e => e.Code == "Client"), "retriever picks Visit (synonym 'no shows') + Client");
    var sys = PlanPrompt.System(ents, catalog, [], today);
    Check(!sys.Contains("Ssn") && !sys.Contains("NoteText") && !sys.Contains("v_Visit"), "prompt has no restricted fields and no view/table names");
    var schema = PlanPrompt.ToolSchema(ents).ToJsonString();
    Check(schema.Contains("\"Visit.VisitDate\"") && !schema.Contains("Client.Ssn"), "tool schema enumerates allowed field refs only");
    File.WriteAllText(Path.Combine(outDir, "prompt_example.txt"), sys + "\n\nTOOL SCHEMA:\n" + schema);
}

// ---------------------------------------------------------------- multi-domain question, end to end
Console.WriteLine("\n== multi-part: \"provide me details for last medication for vatsal and his last billing details\"");
{
    const string medsAndBilling = """
      {"parts":[
        {"title":"Last medication","kind":"rows","entity":"Medication",
         "select":["Medication.DrugName","Medication.Strength","Medication.Frequency","Medication.Prescriber","Medication.StartDate","Medication.Status"],
         "where":{"field":"Client.FullName","op":"nameMatches","value":"vatsal"},
         "orderBy":[{"field":"Medication.StartDate","dir":"desc"}],"latestPerClient":true,"limit":25},
        {"title":"Last billing","kind":"rows","entity":"Charge",
         "select":["Charge.ServiceDate","Charge.ProcedureCode","Charge.BilledAmount","Charge.Payer","Charge.ClaimStatus","Charge.Balance"],
         "where":{"field":"Client.FullName","op":"nameMatches","value":"vatsal"},
         "orderBy":[{"field":"Charge.ServiceDate","dir":"desc"}],"latestPerClient":true,"limit":25}]}
      """;

    DataTable Respond(string sql)
    {
        var t = new DataTable();
        void Cols(params string[] names) { foreach (var n in names) t.Columns.Add(n, typeof(object)); }
        if (sql.Contains("[v_Medication]"))
        {
            Cols("__Key", "__ClientId", "Medication_DrugName", "Medication_Strength", "Medication_Frequency", "Medication_Prescriber", "Medication_StartDate", "Medication_Status", "Client_FirstName", "Client_LastName", "__rn");
            t.Rows.Add(5501L, 1042L, "Sertraline", "50 mg", "once daily", "Dr. Anita Shah", new DateTime(2026, 10, 20), "Active", "Vatsal", "Patel", 1L);
        }
        else
        {
            Cols("__Key", "__ClientId", "Charge_ServiceDate", "Charge_ProcedureCode", "Charge_BilledAmount", "Charge_Payer", "Charge_ClaimStatus", "Charge_Balance", "Client_FirstName", "Client_LastName", "__rn");
            t.Rows.Add(9901L, 1042L, new DateTime(2026, 11, 2), "90834", 145.00m, "Medicaid", "Submitted", 145.00m, "Vatsal", "Patel", 1L);
        }
        return t;
    }

    var ctx = new SmartSearch.Contracts.SearchContext(Guid.NewGuid(), 7, 42, new System.Security.Claims.ClaimsPrincipal());
    var matched = new NameResolutionOutcome(SmartSearch.Contracts.ViewModels.NameResolutionStatus.Matched, [1042], []);

    DynamicQueryService Service(FakePlanner planner, FakeNames names, FakeConnections db) => new(
        new FakeCatalogProvider(catalog), new NoExamples(), planner, validator, compiler, names, db, new DynamicQueryOptions());

    // 1. everything allowed
    var planner = new FakePlanner(medsAndBilling);
    var names = new FakeNames(_ => matched);
    var db = new FakeConnections(Respond);
    var result = await Service(planner, names, db).RunAsync("provide me details for last medication for vatsal and his last billing details", ctx, allowAll, today, default);
    Check(result.Outcome == DynamicOutcome.Results && result.Sections.Count == 2, "two sections returned");
    Check(result.Sections[0].Title == "Last medication" && result.Sections[1].Title == "Last billing", "sections keep the order and titles of the question");
    Check(names.Asked.Count == 1, "the name 'vatsal' is resolved once and shared by both parts");
    Check(db.Sql.Count == 2 && db.Sql.Any(s => s.Contains("[v_Medication]")) && db.Sql.Any(s => s.Contains("[v_Charge]")), "one SQL statement per domain");
    Check(db.Sql.All(s => s.Contains("ROW_NUMBER() OVER (PARTITION BY r.[ClientId]")), "'last' = latest row per client in each domain");
    Check(db.Sql.All(s => !s.Contains(">= @p")), "no default date window because a person is named (full history searched)");
    Check(result.Sections[1].Items[0].Fields["Charge_BilledAmount"] is 145.00m, "billing amount parsed");
    Check(result.Sections[0].InterpretedAs!.StartsWith("Latest medication per client"), "section description: " + result.Sections[0].InterpretedAs);

    var composer = new ViewModelComposer(new NoTemplates(), new CountLayout(),
        [new FakeLinks("Medication", "MedicationDetail", "ClientChart"), new FakeLinks("Charge", "ChargeDetail", "ClientChart")]);
    var vm = await DynamicResponseMapper.MapAsync(result, ctx, "provide me details...", null, composer, default);
    Check(vm.Sections.Count == 2 && vm.InterpretedAs == "last medication and last billing", "outer response wraps two sections");
    Check(vm.Sections[0].Layout == SmartSearch.Contracts.ViewModels.ResultLayout.Panel && vm.Sections[0].Items[0].Title == "Vatsal Patel", "each section composed with its own layout");
    Check(vm.Sections[0].Items[0].PrimaryLink?.Url == "/MedicationDetail/5501" && vm.Sections[1].Items[0].PrimaryLink?.Url == "/ChargeDetail/9901", "deep links to existing medication / billing pages");
    Check(vm.Sections[0].Items[0].Statement.Any(s => s.Link?.Key == "MedicationDetail"), "statement sentence with inline link built for the section");

    // 2. clinician without billing access: medication still answered, billing says no access
    db = new FakeConnections(Respond);
    result = await Service(new FakePlanner(medsAndBilling), new FakeNames(_ => matched), db)
        .RunAsync("same question", ctx, p => p != "Billing.View", today, default);
    Check(result.Sections[0].Outcome == SectionOutcome.Results && result.Sections[1].Outcome == SectionOutcome.NoAccess, "no billing permission → billing section says no access, medication still shown");
    Check(db.Sql.Count == 1 && !db.Sql[0].Contains("[v_Charge]"), "billing SQL never runs without permission");

    // 3. misspelled name → did-you-mean, then re-run the stored plan pinned to the chosen client (no new Bedrock call)
    planner = new FakePlanner(medsAndBilling.Replace("vatsal", "vatsl"));
    var suggest = new NameResolutionOutcome(SmartSearch.Contracts.ViewModels.NameResolutionStatus.Suggest, [],
        [new SmartSearch.Contracts.ViewModels.NameSuggestion(1042, "Vatsal Patel", "DOB 12-Mar-1988", 0.91, "?clientId=1042")]);
    db = new FakeConnections(Respond);
    var svc = Service(planner, new FakeNames(_ => suggest), db);
    result = await svc.RunAsync("last medication for vatsl and his last billing", ctx, allowAll, today, default);
    Check(result.Outcome == DynamicOutcome.NameSuggestions && result.NameResolution!.Suggestions.Count == 1 && db.Sql.Count == 0, "unsure name → did-you-mean, nothing queried");
    var rerun = await svc.RerunAsync(result.PlanJson!, new Dictionary<string, long> { ["vatsl"] = 1042 }, ctx, allowAll, today, default);
    Check(rerun.Outcome == DynamicOutcome.Results && rerun.Sections.Count == 2 && planner.Calls == 1, "picked suggestion re-runs the stored plan without calling Bedrock again");

    // 4. a bad first plan is repaired once using the validator's errors
    planner = new FakePlanner(
        """{"parts":[{"title":"Last medication","kind":"rows","entity":"Medication","select":["Medication.Dose"]}]}""",
        """{"parts":[{"title":"Last medication","kind":"rows","entity":"Medication","select":["Medication.DrugName"]}]}""");
    result = await Service(planner, new FakeNames(_ => matched), new FakeConnections(Respond)).RunAsync("meds", ctx, allowAll, today, default);
    Check(planner.Repairs == 1 && result.Outcome == DynamicOutcome.Results, "unknown field → one repair round → success");

    // 5. clarify
    result = await Service(new FakePlanner("""{"clarify":{"question":"Which billing details?","options":["Last charge","Outstanding balance"]}}"""),
        new FakeNames(_ => matched), new FakeConnections(Respond)).RunAsync("billing for vatsal", ctx, allowAll, today, default);
    vm = await DynamicResponseMapper.MapAsync(result, ctx, "billing for vatsal", null, composer, default);
    Check(result.Outcome == DynamicOutcome.Clarify && vm.FollowUps.Count == 2 && vm.FollowUps[0].Text == "billing for vatsal (Last charge)", "ambiguous → clarifying question with options");

}

{
    // write the two multi-part statements for the syntax check
    var plan = System.Text.Json.JsonSerializer.Deserialize<SearchPlan>("""
      {"parts":[
        {"title":"Last medication","kind":"rows","entity":"Medication","select":["Medication.DrugName"],"where":{"field":"Client.FullName","op":"nameMatches","value":"vatsal"},"orderBy":[{"field":"Medication.StartDate","dir":"desc"}],"latestPerClient":true},
        {"title":"Last billing","kind":"rows","entity":"Charge","select":["Charge.BilledAmount"],"where":{"field":"Client.FullName","op":"nameMatches","value":"vatsal"},"orderBy":[{"field":"Charge.ServiceDate","dir":"desc"}],"latestPerClient":true}]}
      """, json)!;
    var sv = validator.ValidateSearch(plan, catalog, allowAll, today);
    foreach (var part in sv.Parts)
    {
        foreach (var n in part.Result.Plan!.NameFilters) n.ResolvedClientIds = [1042];
        File.WriteAllText(Path.Combine(outDir, $"08_multi_part_{part.Index + 1}.sql"), compiler.Compile(part.Result.Plan, catalog, 7, 42, today).Sql);
    }
}

Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
