using SmartSearch.Contracts.ViewModels;

namespace SmartSearch.Contracts;

/// <summary>Who is searching, for which tenant, under which query id.</summary>
public sealed record SearchContext(Guid QueryId, int TenantId, int UserId, System.Security.Claims.ClaimsPrincipal User);

/// <summary>
/// Raw row returned by a module capability. Values stay typed (DateTime, int...);
/// the ViewModelComposer formats them using the ViewTemplate.
/// Field keys must match the "key" values in FieldMapJson.
/// </summary>
public sealed record ResultItemData(
    string EntityType,
    long EntityId,
    long? ClientId,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>Implemented by each domain module (Clients, Visits...). Registered as keyed service by intent code.</summary>
public interface ISearchCapability
{
    string IntentCode { get; }
    Task<CapabilityResult> ExecuteAsync(SearchContext ctx, IntentSlots slots, CancellationToken ct);
}

public sealed record CapabilityResult(IReadOnlyList<ResultItemData> Items, int TotalCount);

/// <summary>Validated slots from the Bedrock tool call.</summary>
public sealed record IntentSlots(
    IReadOnlyList<long> ClientIds,       // already resolved by NameMatcher
    DateOnly? From = null,
    DateOnly? To = null,
    string? Provider = null,
    string? Status = null,
    int Limit = 25,
    int Page = 1);

/// <summary>
/// Implemented by each module for its entity type. Builds links to EXISTING pages
/// with LinkGenerator and omits any link the user is not allowed to open.
/// </summary>
public interface ILinkProvider
{
    string EntityType { get; }
    IEnumerable<ResultLink> GetLinks(ResultItemData item, SearchContext ctx);
}
