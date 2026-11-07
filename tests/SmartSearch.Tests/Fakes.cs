using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using SmartSearch.Application.Dynamic;
using SmartSearch.Application.Views;
using SmartSearch.Contracts;
using SmartSearch.Contracts.ViewModels;

// ---------------------------------------------------------------- planner / names / catalog fakes

internal sealed class FakePlanner(params string[] toolInputs) : IQueryPlanner
{
    private int _next;
    public int Calls { get; private set; }
    public int Repairs { get; private set; }

    public Task<PlannerResult> PlanAsync(string q, string s, JsonObject schema, CancellationToken ct) => Next();
    public Task<PlannerResult> RepairAsync(string q, string s, JsonObject schema, PlannerResult prev, IReadOnlyList<string> errors, CancellationToken ct)
    {
        Repairs++;
        return Next();
    }

    private Task<PlannerResult> Next()
    {
        Calls++;
        var input = toolInputs[Math.Min(_next++, toolInputs.Length - 1)];
        var raw = "{\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_" + _next + "\",\"name\":\"build_query_plan\",\"input\":" + input
                + "}],\"usage\":{\"input_tokens\":2100,\"output_tokens\":180}}";
        return Task.FromResult(BedrockQueryPlanner.Parse(raw));
    }
}

internal sealed class FakeNames(Func<string, NameResolutionOutcome> resolve) : INameResolver
{
    public List<string> Asked { get; } = [];
    public Task<NameResolutionOutcome> ResolveAsync(string name, SearchContext ctx, CancellationToken ct)
    {
        Asked.Add(name);
        return Task.FromResult(resolve(name));
    }
}

internal sealed class FakeCatalogProvider(SemanticCatalog catalog) : ISemanticCatalogProvider
{
    public Task<SemanticCatalog> GetAsync(CancellationToken ct) => Task.FromResult(catalog);
}

internal sealed class NoExamples : IPlanExampleStore
{
    public Task<IReadOnlyList<PlanExample>> FindAsync(string q, IReadOnlyCollection<string> e, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PlanExample>>([]);
}

// ---------------------------------------------------------------- composer fakes

internal sealed class NoTemplates : IViewTemplateStore
{
    public Task<FieldMap> GetAsync(int t, string i, string e, ResultLayout l, CancellationToken ct) => throw new InvalidOperationException("dynamic path must use the template override");
    public Task<bool> ExistsAsync(int t, string i, string e, ResultLayout l, CancellationToken ct) => Task.FromResult(false);
}

internal sealed class CountLayout : ILayoutSelector
{
    public Task<ResultLayout> SelectAsync(SearchContext c, string i, int n, ResultLayout? requested, CancellationToken ct) =>
        Task.FromResult(requested ?? (n == 1 ? ResultLayout.Panel : n <= 6 ? ResultLayout.Cards : ResultLayout.List));
}

internal sealed class FakeLinks(string entityType, params string[] keys) : ILinkProvider
{
    public string EntityType => entityType;
    public IEnumerable<ResultLink> GetLinks(ResultItemData item, SearchContext ctx) =>
        keys.Select(k => new ResultLink(k, k, $"/{k}/{(k == "ClientChart" ? item.ClientId : item.EntityId)}"));
}

// ---------------------------------------------------------------- minimal ADO.NET fakes (no NuGet)

/// <summary>Returns a canned table depending on which curated view the SQL reads from; records every command.</summary>
internal sealed class FakeConnections(Func<string, DataTable> respond) : IReadOnlyConnectionFactory
{
    public List<string> Sql { get; } = [];
    public DbConnection Create() => new FakeConnection(this, respond);
    public bool IsTimeout(DbException ex) => ex is FakeTimeout;
}

internal sealed class FakeTimeout() : DbException("timeout");

internal sealed class FakeConnection(FakeConnections owner, Func<string, DataTable> respond) : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    [AllowNull] public override string ConnectionString { get; set; } = "";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new FakeCommand(owner, respond) { Connection = this };
}

internal sealed class FakeCommand(FakeConnections owner, Func<string, DataTable> respond) : DbCommand
{
    private readonly FakeParameters _params = new();
    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _params;
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new FakeParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        lock (owner.Sql) owner.Sql.Add(CommandText);
        return respond(CommandText).CreateDataReader();
    }
}

internal sealed class FakeParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    [AllowNull] public override string ParameterName { get; set; } = "";
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() { }
}

internal sealed class FakeParameters : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];
    public override int Count => _items.Count;
    public override object SyncRoot => _items;
    public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v); }
    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains((DbParameter)value);
    public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;
}
