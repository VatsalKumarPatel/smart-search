using System.Text.RegularExpressions;

namespace SmartSearch.Application.Dynamic;

/// <summary>In-memory copy of ss.SemEntity / SemField / SemRelation / SemSynonym. Load once, cache, refresh on change.</summary>
public sealed class SemanticCatalog
{
    public required IReadOnlyDictionary<string, EntityDef> Entities { get; init; }
    public required IReadOnlyList<RelationDef> Relations { get; init; }
    public IReadOnlyList<SynonymDef> Synonyms { get; init; } = [];

    public EntityDef? Entity(string code) =>
        Entities.TryGetValue(code, out var e) && e.IsEnabled ? e : null;

    public RelationDef? Relation(string from, string to) =>
        Relations.FirstOrDefault(r => r.From.Equals(from, StringComparison.OrdinalIgnoreCase)
                                   && r.To.Equals(to, StringComparison.OrdinalIgnoreCase));
}

public sealed class EntityDef
{
    public required string Code { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string SqlSource { get; init; }          // ss.v_Visit
    public required string KeyColumn { get; init; }
    public string? ClientIdColumn { get; init; }
    public string? DefaultDateField { get; init; }           // FieldCode
    public bool RequiresTimeRange { get; init; }
    public int DefaultLookbackMonths { get; init; } = 12;
    public int MaxLookbackMonths { get; init; } = 240;
    public int DefaultLimit { get; init; } = 50;
    public int MaxLimit { get; init; } = 500;
    public string? LinkEntityType { get; init; }
    public string? RequiredPermission { get; init; }
    public bool IsConsentRestricted { get; init; }
    public required string TitleTemplate { get; init; }
    public string? SubtitleTemplate { get; init; }
    public string? StatementTemplate { get; init; }
    public string? PrimaryLink { get; init; }
    public IReadOnlyList<string> SecondaryLinks { get; init; } = [];
    public bool IsEnabled { get; init; } = true;
    public required IReadOnlyDictionary<string, FieldDefinition> Fields { get; init; }

    public FieldDefinition? Field(string code) => Fields.TryGetValue(code, out var f) ? f : null;
}

public sealed class FieldDefinition
{
    public required string EntityCode { get; init; }
    public required string Code { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }           // text|code|int|decimal|date|datetime|bool|personName
    public bool IsFilterable { get; init; } = true;
    public bool IsSelectable { get; init; } = true;
    public bool IsSortable { get; init; } = true;
    public bool IsDefault { get; init; }
    public int Sensitivity { get; init; }                    // 2 = never exposed
    public IReadOnlyList<string> AllowedValues { get; init; } = [];
    public int SortOrder { get; init; } = 100;

    /// <summary>Output column alias and template placeholder key: Visit_VisitDate.</summary>
    public string Alias => $"{EntityCode}_{Code}";
    public string Ref => $"{EntityCode}.{Code}";
    public bool IsRestricted => Sensitivity >= 2;
}

public sealed record RelationDef(string From, string To, string Cardinality, string FromColumn, string ToColumn)
{
    public bool IsManyToOne => Cardinality == "ManyToOne";
}

public sealed record SynonymDef(string Term, string TargetType, string Target);

/// <summary>"Visit.VisitDate" → (Visit, VisitDate)</summary>
public readonly partial record struct FieldRef(string Entity, string Field)
{
    public static bool TryParse(string? text, out FieldRef result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;
        result = new FieldRef(m.Groups[1].Value, m.Groups[2].Value);
        return true;
    }

    public override string ToString() => $"{Entity}.{Field}";

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]*)\.([A-Za-z][A-Za-z0-9]*)$")]
    private static partial Regex Pattern();
}

public interface ISemanticCatalogProvider
{
    /// <summary>Loads ss.Sem* tables; cached (IMemoryCache) and refreshed when the catalog changes.</summary>
    Task<SemanticCatalog> GetAsync(CancellationToken ct);
}
