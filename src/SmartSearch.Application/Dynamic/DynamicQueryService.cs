using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartSearch.Application.Views;
using SmartSearch.Contracts;
using SmartSearch.Contracts.ViewModels;

namespace SmartSearch.Application.Dynamic;

public sealed class DynamicQueryOptions
{
    /// <summary>Hard cap per part. Generated queries run on the read replica with a limited login.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;
    public int MaxEntitiesInPrompt { get; set; } = 8;
    public int MaxExamplesInPrompt { get; set; } = 5;
}

/// <summary>
/// Connection to a READ-ONLY copy of the EHR database (Always On readable secondary / reporting replica),
/// using a login that can only SELECT from the ss.v_* curated views, and that sits in a
/// Resource Governor workload group with CPU / memory / MAXDOP caps.
/// </summary>
public interface IReadOnlyConnectionFactory
{
    DbConnection Create();
    bool IsTimeout(DbException ex);   // SqlException.Number == -2 for SqlClient
}

/// <summary>Wraps the NameMatcher (exact → phonetic → fuzzy) used by the predefined intents too.</summary>
public interface INameResolver
{
    Task<NameResolutionOutcome> ResolveAsync(string name, SearchContext ctx, CancellationToken ct);
}

public sealed record NameResolutionOutcome(NameResolutionStatus Status, IReadOnlyList<long> MatchedClientIds, IReadOnlyList<NameSuggestion> Suggestions);

public enum DynamicOutcome { Results, Clarify, NameSuggestions, NameNotFound, Rejected }

public enum SectionOutcome { Results, NoAccess, Rejected, TooBroad }

/// <summary>One answered part of the question, e.g. "Last medication".</summary>
public sealed record DynamicSection
{
    public required string Title { get; init; }
    public SectionOutcome Outcome { get; init; }
    public string? InterpretedAs { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<ResultItemData> Items { get; init; } = [];
    public bool HasMore { get; init; }
    public FieldMap? Template { get; init; }
    public string? Message { get; init; }
    public byte[]? SqlHash { get; init; }
    public long DbMs { get; init; }
}

/// <summary>Everything the pipeline needs to build the response and write ss.QueryLog.</summary>
public sealed record DynamicSearchResult
{
    public DynamicOutcome Outcome { get; init; }
    public IReadOnlyList<DynamicSection> Sections { get; init; } = [];
    public NameResolution? NameResolution { get; init; }
    public ClarifySpec? Clarify { get; init; }
    public string? Message { get; init; }

    // ---- for ss.QueryLog (IntentCode = 'Dynamic'); PlanJson is what re-runs execute without calling Bedrock
    public string? PlanJson { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public long PlannerMs { get; init; }
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
}

/// <summary>
/// The dynamic path, used when no predefined intent fits:
///   1. pick relevant catalog entities + examples
///   2. Bedrock → SearchPlan with one part per thing asked (forced tool call)
///   3. validate every part; one repair round with the errors
///   4. resolve person names ONCE across parts (did-you-mean if unsure)
///   5. compile each part to parameterized SQL and run the parts in parallel, read-only, with a timeout
///   6. parse rows → sections (a part the user has no access to becomes a "no access" section)
/// The model sees the question and the catalog only. It never sees SQL, table names or data.
/// </summary>
public sealed class DynamicQueryService(
    ISemanticCatalogProvider catalogs,
    IPlanExampleStore examples,
    IQueryPlanner planner,
    QueryPlanValidator validator,
    SqlCompiler compiler,
    INameResolver names,
    IReadOnlyConnectionFactory connections,
    DynamicQueryOptions options)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>New question: plan with Bedrock, then execute.</summary>
    public async Task<DynamicSearchResult> RunAsync(string question, SearchContext ctx, Func<string, bool> hasPermission, DateTime today, CancellationToken ct)
    {
        var catalog = await catalogs.GetAsync(ct);

        var entities = SchemaRetriever.Select(question, catalog, options.MaxEntitiesInPrompt);
        var shots = await examples.FindAsync(question, entities.Select(e => e.Code).ToList(), options.MaxExamplesInPrompt, ct);
        var system = PlanPrompt.System(entities, catalog, shots, today);
        var schema = PlanPrompt.ToolSchema(entities);

        var sw = Stopwatch.StartNew();
        var planned = await planner.PlanAsync(question, system, schema, ct);
        var check = Check(planned, catalog, hasPermission, today);
        int tokensIn = planned.InputTokens, tokensOut = planned.OutputTokens;

        if (check.NeedsRepair && planned.RawPlanJson is not null)
        {
            var repaired = await planner.RepairAsync(question, system, schema, planned, check.RepairErrors, ct);
            tokensIn += repaired.InputTokens;
            tokensOut += repaired.OutputTokens;
            if (repaired.Plan is not null) { planned = repaired; check = Check(repaired, catalog, hasPermission, today); }
        }

        var basis = new DynamicSearchResult
        {
            PlanJson = planned.RawPlanJson, InputTokens = tokensIn, OutputTokens = tokensOut,
            PlannerMs = sw.ElapsedMilliseconds, ValidationErrors = check.RepairErrors
        };
        return await ExecuteAsync(planned.Plan, check, catalog, ctx, pinnedNames: null, today, basis, ct);
    }

    /// <summary>
    /// Re-run a stored plan (ss.QueryLog.IntentJson) without Bedrock: layout change, paging,
    /// or a did-you-mean pick (pinnedNames: "vatsl" → 1042).
    /// </summary>
    public async Task<DynamicSearchResult> RerunAsync(string planJson, IReadOnlyDictionary<string, long>? pinnedNames, SearchContext ctx,
        Func<string, bool> hasPermission, DateTime today, CancellationToken ct)
    {
        var catalog = await catalogs.GetAsync(ct);
        var plan = JsonSerializer.Deserialize<SearchPlan>(planJson, Json);
        var check = plan is null ? new SearchValidation([], ["stored plan is unreadable"]) : validator.ValidateSearch(plan, catalog, hasPermission, today);
        return await ExecuteAsync(plan, check, catalog, ctx, pinnedNames, today, new DynamicSearchResult { PlanJson = planJson }, ct);
    }

    private async Task<DynamicSearchResult> ExecuteAsync(SearchPlan? plan, SearchValidation check, SemanticCatalog catalog, SearchContext ctx,
        IReadOnlyDictionary<string, long>? pinnedNames, DateTime today, DynamicSearchResult basis, CancellationToken ct)
    {
        if (plan?.Clarify is { } clarify && plan.Parts.Count == 0)
            return basis with { Outcome = DynamicOutcome.Clarify, Clarify = clarify };

        var usable = check.Parts.Where(p => p.Result.IsValid).ToList();
        if (plan is null || check.GlobalErrors.Count > 0 || (usable.Count == 0 && check.Parts.All(p => p.Result.NeedsRepair)))
            return basis with
            {
                Outcome = DynamicOutcome.Rejected,
                Message = "I couldn't turn that into a safe search. Try naming what you want to see, for whom, and a time period."
            };

        // ---- names: resolve each distinct name once, shared by all parts
        var byName = usable.SelectMany(p => p.Result.Plan!.NameFilters)
                           .GroupBy(n => n.Value!.Value.GetString()!.Trim().ToLowerInvariant());
        foreach (var group in byName)
        {
            IReadOnlyList<long> ids;
            if (pinnedNames is not null && pinnedNames.TryGetValue(group.Key, out var pinned))
                ids = [pinned];
            else
            {
                var outcome = await names.ResolveAsync(group.Key, ctx, ct);
                if (outcome.Status == NameResolutionStatus.Suggest)
                    return basis with
                    {
                        Outcome = DynamicOutcome.NameSuggestions,
                        NameResolution = new NameResolution { Status = NameResolutionStatus.Suggest, Input = group.Key, Suggestions = outcome.Suggestions }
                    };
                if (outcome.Status == NameResolutionStatus.NotFound)
                    return basis with
                    {
                        Outcome = DynamicOutcome.NameNotFound,
                        NameResolution = new NameResolution { Status = NameResolutionStatus.NotFound, Input = group.Key }
                    };
                ids = outcome.MatchedClientIds;
            }
            foreach (var node in group) node.ResolvedClientIds = ids;
        }

        // ---- run every part in parallel; keep the original order for display
        var tasks = check.Parts.Select(p => p.Result switch
        {
            { IsValid: true } => RunPartAsync(p, catalog, ctx, today, ct),
            { PermissionOnly: true } => Task.FromResult(new DynamicSection
            {
                Title = p.Title, Outcome = SectionOutcome.NoAccess,
                Message = "You don't have access to this information."
            }),
            _ => Task.FromResult(new DynamicSection
            {
                Title = p.Title, Outcome = SectionOutcome.Rejected,
                Message = "This part of the question couldn't be answered. Try asking it on its own."
            })
        });
        var sections = await Task.WhenAll(tasks);

        return basis with { Outcome = DynamicOutcome.Results, Sections = sections };
    }

    private async Task<DynamicSection> RunPartAsync(PartValidation part, SemanticCatalog catalog, SearchContext ctx, DateTime today, CancellationToken ct)
    {
        var vp = part.Result.Plan!;
        var compiled = compiler.Compile(vp, catalog, ctx.TenantId, ctx.UserId, today);
        var section = new DynamicSection
        {
            Title = part.Title,
            InterpretedAs = PlanDescriber.Describe(vp, catalog, today),
            Notes = vp.Notes,
            SqlHash = SHA256.HashData(Encoding.UTF8.GetBytes(compiled.Sql))
        };

        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = connections.Create();     // one connection per part so parts run in parallel
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = compiled.Sql;
            cmd.CommandType = CommandType.Text;
            cmd.CommandTimeout = options.CommandTimeoutSeconds;
            foreach (var p in compiled.Parameters)
            {
                var dp = cmd.CreateParameter();
                dp.ParameterName = p.Name;
                dp.Value = p.Value ?? DBNull.Value;
                cmd.Parameters.Add(dp);
            }

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, ct);
            var rows = await ResultParser.ReadAsync(reader, compiled, vp, ct);

            return section with
            {
                Outcome = SectionOutcome.Results,
                Items = rows.Items,
                HasMore = rows.HasMore,
                Template = DynamicTemplateBuilder.Build(vp, catalog),
                DbMs = sw.ElapsedMilliseconds
            };
        }
        catch (DbException ex) when (connections.IsTimeout(ex))
        {
            return section with
            {
                Outcome = SectionOutcome.TooBroad,
                DbMs = sw.ElapsedMilliseconds,
                Message = "This covers too much data. Add a shorter date range, a program or a client name."
            };
        }
    }

    private SearchValidation Check(PlannerResult planned, SemanticCatalog catalog, Func<string, bool> hasPermission, DateTime today) =>
        planned.Plan is null
            ? new SearchValidation([], [planned.Error ?? "no plan"])
            : validator.ValidateSearch(planned.Plan, catalog, hasPermission, today);
}
