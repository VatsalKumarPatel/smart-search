using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartSearch.Application.Dynamic;

public sealed class SqlCompilerOptions
{
    /// <summary>
    /// Row-level access filter for client-owned rows. {0} is replaced by the client id column expression.
    /// Point it at your caseload / program-access view. @__tenant and @__user are always bound.
    /// </summary>
    public string ClientAccessPredicate { get; set; } =
        "EXISTS (SELECT 1 FROM [ss].[v_UserClientAccess] AS acc WHERE acc.[TenantId] = @__tenant AND acc.[UserId] = @__user AND acc.[ClientId] = {0})";

    /// <summary>Generated shapes vary a lot, so a fresh plan per query avoids bad parameter sniffing.</summary>
    public bool UseRecompile { get; set; } = true;
    public int MaxDop { get; set; } = 2;
}

public sealed record SqlParam(string Name, object? Value);

public sealed record OutputColumn(string Alias, FieldDefinition? Field, string? AggregateFn, bool Hidden);

public sealed record CompiledQuery(string Sql, IReadOnlyList<SqlParam> Parameters, IReadOnlyList<OutputColumn> Columns, bool IsAggregate, int Limit);

/// <summary>
/// Compiles a ValidatedPlan to ONE parameterized T-SQL statement.
/// Safety rules enforced here, not by the model:
///   - identifiers come only from the catalog and are checked + bracket-quoted
///   - every value is a parameter (LIKE patterns are escaped)
///   - TenantId and the client-access predicate are always added, including inside EXISTS
///   - TOP is always present (limit + 1, so the UI can say "more results")
/// </summary>
public sealed partial class SqlCompiler(SqlCompilerOptions options)
{
    public const string KeyAlias = "__Key", ClientAlias = "__ClientId";

    public CompiledQuery Compile(ValidatedPlan vp, SemanticCatalog catalog, int tenantId, int userId, DateTime today)
    {
        var b = new Builder(catalog, options, today.Date);
        b.Bind("__tenant", tenantId);
        b.Bind("__user", userId);
        b.Bind("__limit", vp.Limit + 1);

        var root = vp.Root;
        const string r = "r";

        // ---- joins (ManyToOne only, one hop)
        var joins = new StringBuilder();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [root.Code] = r };
        var j = 0;
        foreach (var code in vp.Joins)
        {
            var target = catalog.Entity(code)!;
            var rel = catalog.Relation(root.Code, target.Code)!;
            var a = $"j{++j}";
            aliases[target.Code] = a;
            joins.Append(CultureInfo.InvariantCulture,
                $"\nLEFT JOIN {Source(target.SqlSource)} AS {a} ON {a}.[TenantId] = {r}.[TenantId] AND {a}.{Ident(rel.ToColumn)} = {r}.{Ident(rel.FromColumn)}");
        }
        string Col(FieldDefinition f) => $"{aliases[f.EntityCode]}.{Ident(f.ColumnName)}";

        // ---- WHERE
        var where = new List<string> { $"{r}.[TenantId] = @__tenant" };
        if (root.ClientIdColumn is not null)
            where.Add(string.Format(CultureInfo.InvariantCulture, options.ClientAccessPredicate, $"{r}.{Ident(root.ClientIdColumn)}"));
        if (vp.TimeField is not null && vp.From is not null) where.Add($"{Col(vp.TimeField)} >= {b.Bind(vp.From.Value)}");
        if (vp.TimeField is not null && vp.To is not null) where.Add($"{Col(vp.TimeField)} < {b.Bind(vp.To.Value)}");
        if (vp.Plan.Where is not null) where.Add(b.Filter(vp.Plan.Where, new Scope(root, r, aliases)));

        var whereSql = string.Join("\n  AND ", where);
        var hint = options.UseRecompile ? $"\nOPTION (RECOMPILE, MAXDOP {options.MaxDop})" : $"\nOPTION (MAXDOP {options.MaxDop})";
        var from = $"FROM {Source(root.SqlSource)} AS {r}{joins}";

        if (vp.Kind == PlanKind.Aggregate)
        {
            var cols = new List<OutputColumn>();
            var select = new List<string>();
            foreach (var g in vp.GroupBy)
            {
                select.Add($"{Col(g)} AS {Ident(g.Alias)}");
                cols.Add(new OutputColumn(g.Alias, g, null, false));
            }
            foreach (var agg in vp.Aggregates)
            {
                var expr = agg.Fn switch
                {
                    "count" => agg.Field is null ? "COUNT_BIG(*)" : $"COUNT_BIG({Col(agg.Field)})",
                    "countDistinct" => $"COUNT_BIG(DISTINCT {Col(agg.Field!)})",
                    "sum" => $"SUM({Col(agg.Field!)})",
                    "avg" => $"AVG(CAST({Col(agg.Field!)} AS decimal(18,2)))",
                    "min" => $"MIN({Col(agg.Field!)})",
                    _ => $"MAX({Col(agg.Field!)})"
                };
                select.Add($"{expr} AS {Ident(agg.Alias)}");
                cols.Add(new OutputColumn(agg.Alias, agg.Field, agg.Fn, false));
            }

            var groupBy = vp.GroupBy.Count > 0 ? "\nGROUP BY " + string.Join(", ", vp.GroupBy.Select(Col)) : "";
            var orderBy = vp.OrderBy.Count > 0
                ? "\nORDER BY " + string.Join(", ", vp.OrderBy.Select(s => $"{Ident(s.AggregateAlias ?? s.Field!.Alias)} {(s.Desc ? "DESC" : "ASC")}"))
                : "";

            var sql = $"SELECT TOP (@__limit) {string.Join(", ", select)}\n{from}\nWHERE {whereSql}{groupBy}{orderBy}{hint};";
            return new CompiledQuery(sql, b.Parameters, cols, IsAggregate: true, vp.Limit);
        }

        // ---- rows
        var rowCols = new List<OutputColumn> { new(KeyAlias, null, null, true) };
        var rowSelect = new List<string> { $"{r}.{Ident(root.KeyColumn)} AS {Ident(KeyAlias)}" };
        if (root.ClientIdColumn is not null)
        {
            rowSelect.Add($"{r}.{Ident(root.ClientIdColumn)} AS {Ident(ClientAlias)}");
            rowCols.Add(new OutputColumn(ClientAlias, null, null, true));
        }
        foreach (var f in vp.Select)
        {
            rowSelect.Add($"{Col(f)} AS {Ident(f.Alias)}");
            rowCols.Add(new OutputColumn(f.Alias, f, null, vp.TemplateOnly.Contains(f.Alias)));
        }

        var order = vp.OrderBy.Count > 0
            ? vp.OrderBy.Select(s => $"{Col(s.Field!)} {(s.Desc ? "DESC" : "ASC")}").ToList()
            : [$"{r}.{Ident(root.KeyColumn)} DESC"];

        string rowSql;
        if (vp.LatestPerClient)
        {
            // newest row per client, then sort + limit across clients
            var outerOrder = vp.OrderBy.Count > 0
                ? string.Join(", ", vp.OrderBy.Select(s => $"{Ident(s.Field!.Alias)} {(s.Desc ? "DESC" : "ASC")}"))
                : $"{Ident(KeyAlias)} DESC";
            rowSql =
                $"WITH q AS (\n  SELECT {string.Join(", ", rowSelect)},\n" +
                $"         ROW_NUMBER() OVER (PARTITION BY {r}.{Ident(root.ClientIdColumn!)} ORDER BY {string.Join(", ", order)}) AS [__rn]\n" +
                $"  {from}\n  WHERE {whereSql}\n)\n" +
                $"SELECT TOP (@__limit) * FROM q WHERE [__rn] = 1\nORDER BY {outerOrder}{hint};";
        }
        else
        {
            rowSql = $"SELECT TOP (@__limit) {string.Join(", ", rowSelect)}\n{from}\nWHERE {whereSql}\nORDER BY {string.Join(", ", order)}{hint};";
        }

        return new CompiledQuery(rowSql, b.Parameters, rowCols, IsAggregate: false, vp.Limit);
    }

    // ------------------------------------------------------------------ identifiers

    internal static string Ident(string name)
    {
        if (!IdentPattern().IsMatch(name)) throw new InvalidOperationException($"Unsafe identifier '{name}' in catalog");
        return $"[{name}]";
    }

    internal static string Source(string source) => string.Join(".", source.Split('.').Select(Ident));

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex IdentPattern();

    // ------------------------------------------------------------------ filter builder

    private sealed record Scope(EntityDef Entity, string Alias, IReadOnlyDictionary<string, string> Aliases);

    private sealed class Builder(SemanticCatalog catalog, SqlCompilerOptions options, DateTime today)
    {
        private readonly List<SqlParam> _params = [];
        private int _sub;
        public IReadOnlyList<SqlParam> Parameters => _params;

        public void Bind(string name, object value) => _params.Add(new SqlParam("@" + name, value));

        public string Bind(object? value)
        {
            var name = $"@p{_params.Count}";
            _params.Add(new SqlParam(name, value));
            return name;
        }

        public string Filter(FilterNode n, Scope scope)
        {
            if (n.And is not null) return n.And.Count == 0 ? "1 = 1" : "(" + string.Join(" AND ", n.And.Select(c => Filter(c, scope))) + ")";
            if (n.Or is not null)  return n.Or.Count == 0 ? "1 = 0" : "(" + string.Join(" OR ", n.Or.Select(c => Filter(c, scope))) + ")";
            if (n.Not is not null) return $"NOT ({Filter(n.Not, scope)})";
            if (n.Exists is not null) return Exists(n.Exists, scope, negate: false);
            if (n.NotExists is not null) return Exists(n.NotExists, scope, negate: true);
            return Leaf(n, scope);
        }

        private string Exists(SubQuerySpec sub, Scope scope, bool negate)
        {
            var target = catalog.Entity(sub.Entity)!;
            var rel = catalog.Relation(scope.Entity.Code, target.Code)!;
            var a = $"e{++_sub}";
            var conds = new List<string>
            {
                $"{a}.[TenantId] = {scope.Alias}.[TenantId]",
                $"{a}.{Ident(rel.ToColumn)} = {scope.Alias}.{Ident(rel.FromColumn)}"
            };
            if (target.ClientIdColumn is not null)   // never let EXISTS look at clients outside the user's access
                conds.Add(string.Format(CultureInfo.InvariantCulture, options.ClientAccessPredicate, $"{a}.{Ident(target.ClientIdColumn)}"));
            if (sub.Where is not null)
                conds.Add(Filter(sub.Where, new Scope(target, a, new Dictionary<string, string> { [target.Code] = a })));

            var body = $"FROM {Source(target.SqlSource)} AS {a} WHERE {string.Join(" AND ", conds)}";
            if (sub.MinCount is > 1)
                return $"((SELECT COUNT_BIG(*) {body}) {(negate ? "<" : ">=")} {Bind((long)sub.MinCount.Value)})";
            return $"{(negate ? "NOT " : "")}EXISTS (SELECT 1 {body})";
        }

        private string Leaf(FilterNode n, Scope scope)
        {
            FieldRef.TryParse(n.Field, out var fr);
            var entity = catalog.Entity(fr.Entity)!;
            var f = entity.Field(fr.Field)!;
            var col = $"{scope.Aliases[entity.Code]}.{Ident(f.ColumnName)}";
            var isDate = f.DataType is "date" or "datetime";

            object? V(System.Text.Json.JsonElement e)
            {
                if (!PlanValues.TryConvert(f, e, today, out var v, out var err)) throw new InvalidOperationException(err);
                return v;
            }

            switch (n.Op)
            {
                case FilterOps.NameMatches:
                {
                    var ids = n.ResolvedClientIds ?? throw new InvalidOperationException("nameMatches was not resolved by the NameMatcher");
                    if (ids.Count == 0) return "1 = 0";
                    // filter on the root table's own ClientId when it has one, so SQL Server can seek the
                    // (TenantId, ClientId, date) index instead of filtering after the join
                    var clientCol = scope.Entity.ClientIdColumn is { } own
                        ? $"{scope.Alias}.{Ident(own)}"
                        : $"{scope.Aliases[entity.Code]}.{Ident(entity.ClientIdColumn!)}";
                    return $"{clientCol} IN ({string.Join(", ", ids.Select(id => Bind(id)))})";
                }
                case FilterOps.IsNull:    return $"{col} IS NULL";
                case FilterOps.IsNotNull: return $"{col} IS NOT NULL";
                case FilterOps.Eq when isDate:
                {
                    var d = ((DateTime)V(n.Value!.Value)!).Date;   // "on 3 Nov" = the whole day
                    return $"({col} >= {Bind(d)} AND {col} < {Bind(d.AddDays(1))})";
                }
                case FilterOps.Eq:  return $"{col} = {Bind(V(n.Value!.Value))}";
                case FilterOps.Neq: return $"({col} <> {Bind(V(n.Value!.Value))} OR {col} IS NULL)";
                case FilterOps.Gt:  return isDate ? $"{col} >= {Bind(((DateTime)V(n.Value!.Value)!).Date.AddDays(1))}" : $"{col} > {Bind(V(n.Value!.Value))}";
                case FilterOps.Gte: return $"{col} >= {Bind(V(n.Value!.Value))}";
                case FilterOps.Lt:  return $"{col} < {Bind(V(n.Value!.Value))}";
                case FilterOps.Lte: return isDate ? $"{col} < {Bind(((DateTime)V(n.Value!.Value)!).Date.AddDays(1))}" : $"{col} <= {Bind(V(n.Value!.Value))}";
                case FilterOps.Between:
                {
                    var arr = n.Value!.Value;
                    var lo = V(arr[0]);
                    var hi = V(arr[1]);
                    return isDate
                        ? $"({col} >= {Bind(((DateTime)lo!).Date)} AND {col} < {Bind(((DateTime)hi!).Date.AddDays(1))})"
                        : $"({col} >= {Bind(lo)} AND {col} <= {Bind(hi)})";
                }
                case FilterOps.In or FilterOps.NotIn:
                {
                    var list = string.Join(", ", n.Value!.Value.EnumerateArray().Select(e => Bind(V(e))));
                    return n.Op == FilterOps.In ? $"{col} IN ({list})" : $"({col} NOT IN ({list}) OR {col} IS NULL)";
                }
                case FilterOps.Contains:   return $"{col} LIKE {Bind("%" + EscapeLike((string)V(n.Value!.Value)!) + "%")} ESCAPE '\\'";
                case FilterOps.StartsWith: return $"{col} LIKE {Bind(EscapeLike((string)V(n.Value!.Value)!) + "%")} ESCAPE '\\'";
                default: throw new InvalidOperationException($"operator {n.Op} not supported");
            }
        }

        private static string EscapeLike(string s) =>
            s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
    }
}
