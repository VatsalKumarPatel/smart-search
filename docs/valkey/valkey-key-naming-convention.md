# Valkey Key Naming Convention & Key Builder

Nov 5, 2026 · @Vatsalkumar Patel

## Purpose and status

Every key we write to Valkey follows one format and is built through one class, `ValkeyKey`, so no developer hand-types a key string. Status: **Proposal — for team review.**

Why now: keys are currently created ad hoc across modules, which makes them hard to find, hard to invalidate, and risky across tenants (a key missing the partner ID can leak data between partners). When two modules cache the same data under different keys, one copy goes stale after the other is updated, and the bug is hard to trace.

**Goals**

- Predictable keys: anyone can tell owner, scope and purpose from the key alone.
- One copy of each piece of data: a table row is cached under exactly one key, shared by every module.
- Tenant safety: partner-, user-, role- and session-scoped data always carries its owning IDs.
- Easy invalidation: CDC clears a row's key when the row changes; one `SCAN` pattern clears a partner, user, role or session.
- One place to change: the builder class (in Common.API) and the key registry on this page.

**In scope:** cache entries, session data, Pub/Sub channels, Streams, locks and counters in Valkey. **Out of scope:** Valkey cluster topology, eviction policy tuning, and data retention rules for PHI.

## Key format

Each Valkey instance belongs to one environment, so keys carry no app or environment prefix. Every tenant key starts with the partner. A version suffix is **not** part of the key by default; it is added only when needed (see When to add a version).

```
Global:  g:{name}[:{id}][:{qualifier}...][:v{n}]
Row:     p:{<partnerId>}:t:{table}:{rowId}[:v{n}]
Partner: p:{<partnerId>}:{name}[:{id}][:{qualifier}...][:v{n}]
Role:    p:{<partnerId>}:r:{roleId}:{name}[...][:v{n}]
User:    p:{<partnerId>}:u:{userId}:{name}[...][:v{n}]
Session: p:{<partnerId>}:u:{userId}:s:{sessionId}:{name}[...][:v{n}]
```

| Part | Required | Rule | Example |
| --- | --- | --- | --- |
| Root | Yes | `g` for global data, or `p:{partnerId}` for anything owned by a partner. The braces are literal (cluster hash tag) | `p:{1042}` |
| Owner path | Per scope | `t` for a table row; `r:{roleId}`, `u:{userId}` or `u:{userId}:s:{sessionId}`; omitted for partner-level data | `u:55120` |
| `table` / `name` | Yes | Row keys: the table name, taken from the row DTO. Other keys: a name from the shared `CacheName` enum — unique across all teams, never typed as text | `appointment`, `claim-summary` |
| `id` | Optional | Typed `long` or `Guid` | `88731` |
| `qualifier` | Optional | Fixed variant (`list:today`) or `FilterHash` of user input | `a3f9c21b7e4d0a55` |
| `v{n}` | Optional | Only when the value's shape changes; starts at `v2` | `v2` |

**Grammar rules**

1. Colon `:` is the only separator. Words inside a part use kebab-case: `home-metrics`, never `HomeMetrics` or `home_metrics`.
2. Lowercase ASCII letters, digits and hyphens only. No spaces, dots, slashes or emails.
3. Single letters `g`, `p`, `t`, `r`, `u`, `s` are reserved scope markers, and `v` followed by digits is reserved for versions. `CacheName` names, table names and qualifiers can never take these forms.
4. IDs are internal numeric or GUID IDs only — never names, MRNs, SSNs, emails or any PHI. Keys show up in logs, `MONITOR` and `SCAN` output.
5. GUIDs are written lowercase without braces or hyphens (`n` format) to keep keys short.
6. Total length stays under 150 characters; user input is hashed with `FilterHash`.
7. Because users, roles and sessions all belong to a partner, every non-global key starts with `p:{partnerId}`. One partner's data then sits under one prefix and one cluster slot.

**When to add a version**

Most keys never need one. Add `.Version(2)` only when the stored value changes shape in a way old code cannot read — a renamed or removed field, or a changed type. Adding a field does not need a version.

- No suffix means version 1, so the first versioned key is `:v2`, then `:v3`.
- While old and new code run side by side during a release, they read different keys and never see each other's shape. Old keys expire by TTL.
- Safety net: `IValkeyCache` treats a value that fails to deserialize as a cache miss, deletes it and reloads, so a forgotten version causes one extra database read, not an error.

## Scopes

Pick the narrowest scope that is still shared by everyone who reads the value. If the data is a single table row, always use the row scope, so there is only one copy.

| Scope | Code | Key starts with | Use for | Example key |
| --- | --- | --- | --- | --- |
| Global | `g` | `g:` | Reference data identical for all partners: lookup lists, feature catalogue, app config | `g:feature-flag` |
| Row | `t` | `p:{partnerId}:t:` | One table row, shared by every module | `p:{1042}:t:appointment:88731` |
| Partner | `p` | `p:{partnerId}:` | Views across many rows: partner settings, dashboard aggregates | `p:{1042}:payer-list` |
| Role | `r` | `p:{partnerId}:r:{roleId}:` | Role permissions, menu/sidebar per role | `p:{1042}:r:7:role-menu` |
| User | `u` | `p:{partnerId}:u:{userId}:` | User preferences, recent clients, user search results | `p:{1042}:u:55120:recent-clients` |
| Session | `s` | `p:{partnerId}:u:{userId}:s:{sessionId}:` | Login session data, wizard state, SSE connection tracking | `p:{1042}:u:55120:s:9f2c41ab7d0e4c1a:token-state` |

**Choosing a scope**

- Value is one table row → `t`, no matter which module reads it.
- Value is the same for every partner → `g`.
- Value combines rows into a view that is the same for everyone in the partner → `p`.
- Value depends on what a role can see (menus, permission sets) → `r`. Prefer this over `u` for permissions: one entry serves every user with that role.
- Value is personal to one user and should survive logout → `u`.
- Value must disappear at logout or session expiry → `s`, always with a TTL no longer than the session timeout.

**Invalidation patterns** (use `SCAN` with `MATCH`, never `KEYS` in production)

| Clear | Pattern |
| --- | --- |
| One table row | Delete `p:{1042}:t:appointment:88731` directly (no scan needed) |
| All of one partner | `p:{1042}:*` |
| All cached rows of one table for a partner | `p:{1042}:t:appointment:*` |
| One view for a partner | `p:{1042}:claim-summary*` |
| One role | `p:{1042}:r:7:*` |
| One user (incl. their sessions) | `p:{1042}:u:55120:*` |
| One session | `p:{1042}:u:55120:s:9f2c41ab7d0e4c1a:*` |

## Shared row keys and CDC invalidation

A table row is cached once, under one key, in one shape, and every module reads that same entry. When the row changes, CDC deletes the key, so no module can read an old copy.

**Why one key per row.** If Billing and Scheduling each cached their own copy of appointment 88731, an update through Scheduling would refresh only Scheduling's copy. Billing would keep showing old data until its TTL ran out, and tracing it would mean finding every key that held the appointment. With one row key there is one copy to read, one copy to clear, and one key to check when debugging.

**How it works**

1. **One shape.** Each cacheable table has one row DTO in Common.API, marked with the table name. Every module gets the same object and uses the fields it needs.
2. **One loader.** On a cache miss, every module calls the same `IRowCache.GetAsync`. It loads the row by primary key, caches it and returns it. No team writes its own loader.
3. **CDC deletes, the next read reloads.** The existing CDC pipeline (AWS DMS → EventBridge → Lambda) already sees every change with its table name. The CDC Lambda builds the row key from the table name, partner ID and primary key, and deletes it. It deletes rather than writes, so two writers can never race.
4. **Views depend on tables.** Cached views that combine rows (claim summary, dashboard metrics, role menu) list the tables they depend on in the registry. When CDC sees a change in one of those tables for a partner, it also deletes that partner's view keys.

```
Appointment 88731 updated (partner 1042)
   → DMS / CDC event: table = Appointment, PartnerId = 1042, AppointmentId = 88731
   → CDC Lambda deletes  p:{1042}:t:appointment:88731
   → registry: claim-summary depends on Appointment
   → CDC Lambda deletes  p:{1042}:claim-summary*
   → next read by any module reloads fresh data
```

**Row DTO and loader**

```csharp
// Common.Api.Caching.Rows — one DTO per cacheable table, shared by every API
[CacheTable("Appointment", KeyColumn = "AppointmentId")]
public sealed record AppointmentRow(
    long AppointmentId,
    long PartnerId,
    long ClientId,
    long ProviderId,
    DateTime StartTime,
    DateTime EndTime,
    string Status);

// Any module, same call, same key, same shape
var appt = await _rows.GetAsync<AppointmentRow>(partnerId, appointmentId, ct);
// key: p:{1042}:t:appointment:88731
```

```csharp
public interface IRowCache
{
    /// <summary>Reads the shared row key; on a miss loads the row by primary key, caches it and returns it.</summary>
    Task<TRow?> GetAsync<TRow>(long partnerId, long rowId, CancellationToken ct = default) where TRow : class;

    /// <summary>Deletes the shared row key. Called by the CDC Lambda; app code rarely needs it.</summary>
    Task InvalidateAsync<TRow>(long partnerId, long rowId, CancellationToken ct = default) where TRow : class;
}
```

The default loader selects exactly the DTO's columns from the table named in `[CacheTable]`, filtered by partner and primary key, so every API loads the same shape.

**Rules for row keys**

- Only tables with a row DTO in Common.API can be row-cached. Tables with PHI columns that must not be cached either get a DTO without those columns or are marked not cacheable in the registry.
- Row keys are written only by `IRowCache` and deleted only by `IRowCache` or the CDC Lambda. Modules never write a row key directly.
- A module that needs different fields asks for them to be added to the shared DTO. It does not create its own copy of the row.
- Row TTL is a safety net (1 h by default), because CDC normally deletes the key within seconds of a change.

## TTL, data types and operations

Every key gets a TTL unless the registry marks it as permanent and names an owner who cleans it up.

| Scope | Default TTL | Max TTL | Notes |
| --- | --- | --- | --- |
| Global | 24 h | 7 days | Refresh on deploy or on reference-data change |
| Row | 1 h | 24 h | CDC deletes on change; TTL is only a safety net |
| Partner | 1 h | 24 h | Deleted by CDC when a dependent table changes |
| Role | 30 min | 8 h | Deleted on role/permission change |
| User | 30 min | 24 h | Sliding expiry allowed |
| Session | = session timeout | = session timeout | Delete explicitly on logout |
| Any key built from user input | 5 min | 30 min | Search results and filters |

Add ±10% random jitter to TTLs on bulk-loaded keys so they do not all expire in the same second.

**Non-cache key types** use the same format; their `CacheName` starts with a kind prefix so they are never confused with cached data:

| Kind | Name prefix | Example |
| --- | --- | --- |
| Pub/Sub channel | `ch` | `p:{1042}:ch-client-update` |
| Stream | `st` | `p:{1042}:st-cdc-event` |
| Distributed lock | `lock` | `p:{1042}:lock-claim-batch:3301` |
| Counter / rate limit | `ctr` | `p:{1042}:u:55120:ctr-login-fail` |

**Cluster and hash tags**

- Valkey Cluster routes a key by the text inside the first `{...}`. Writing the partner as `p:{1042}` keeps one partner's keys on one slot, so multi-key commands (`MGET`, transactions, Lua) work within a partner.
- Global keys have no braces, so they spread across slots.
- If one partner grows large enough to make its slot hot, move that partner's high-volume entities to a wider tag such as `p:{1042-scheduling}` — a change in the builder, not at call sites.
- On a single primary (no cluster) the braces are harmless and keep the format ready for cluster later.

**Values**

- Serialize with `System.Text.Json`.
- Prefer a Hash over many small String keys when fields are read together.
- Do not store PHI in Valkey unless the entry is approved in the registry, encrypted at rest/in transit, and has a short TTL.

## Proposed key builder: `ValkeyKey`

Developers never concatenate key strings. They ask a factory for a key in a scope, pass the IDs and parts as parameters, and get back a validated string. The factory always starts tenant keys with the partner, so a row, user, role or session key cannot be built without its partner ID.

**Design**

- `IValkeyKeyFactory` — one method per scope; the method signature forces the right owning IDs.
- `Row<TRow>()` — the table name comes from the row DTO's `[CacheTable]` attribute, so nobody types it.
- `CacheName` enum — the one list of every other key name, shared by all teams, so a name cannot be misspelled or used twice.
- `ValkeyKey` — fluent builder for the optional parts (`Id`, `Qualifier`, `FilterHash`, `Version`), plus `Build()`, `Template()`, scan patterns and the default TTL for its scope.
- The team that owns a name writes its key method once; other teams call that method.

**Registration**

```csharp
// Program.cs — part of AddCommonApi(); each environment has its own Valkey
builder.Services.AddSingleton<IValkeyKeyFactory, ValkeyKeyFactory>();
```

**Factory**

```csharp
/// <summary>
/// The one list of every non-row key name, shared by all teams. Each name exists once and is
/// specific (ClaimSummary, not Summary). Adding one is a small reviewed Common.API change.
/// </summary>
public enum CacheName
{
    FeatureFlag,
    PayerList,
    ClaimSummary,
    ClaimSearch,
    HomeMetrics,
    RoleMenu,
    RecentClients,
    TokenState,
    SseConnection,
    ChClientUpdate,     // Pub/Sub channel
    StCdcEvent,         // stream
    LockClaimBatch,     // distributed lock
    CtrLoginFail        // counter
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class CacheTableAttribute(string table) : Attribute
{
    public string Table { get; } = table;
    public string KeyColumn { get; init; } = "Id";
}

public interface IValkeyKeyFactory
{
    ValkeyKey Global(CacheName name);
    ValkeyKey Row<TRow>(long partnerId, long rowId) where TRow : class;
    ValkeyKey Partner(long partnerId, CacheName name);
    ValkeyKey Role(long partnerId, long roleId, CacheName name);
    ValkeyKey User(long partnerId, long userId, CacheName name);
    ValkeyKey Session(long partnerId, long userId, string sessionId, CacheName name);
}

public sealed class ValkeyKeyFactory : IValkeyKeyFactory
{
    public ValkeyKey Global(CacheName name)
        => new(KeyScope.Global, null, [], Name(name));

    public ValkeyKey Row<TRow>(long partnerId, long rowId) where TRow : class
        => new ValkeyKey(KeyScope.Row, partnerId, ["t"], TableName<TRow>()).Id(rowId);

    public ValkeyKey Partner(long partnerId, CacheName name)
        => new(KeyScope.Partner, partnerId, [], Name(name));

    public ValkeyKey Role(long partnerId, long roleId, CacheName name)
        => new(KeyScope.Role, partnerId, ["r", Str(roleId)], Name(name));

    public ValkeyKey User(long partnerId, long userId, CacheName name)
        => new(KeyScope.User, partnerId, ["u", Str(userId)], Name(name));

    public ValkeyKey Session(long partnerId, long userId, string sessionId, CacheName name)
        => new(KeyScope.Session, partnerId, ["u", Str(userId), "s", sessionId], Name(name));

    /// <summary>Table name from [CacheTable]: "Appointment" -> appointment, "Client_Address" -> client-address.</summary>
    public static string TableName<TRow>()
    {
        var attr = typeof(TRow).GetCustomAttribute<CacheTableAttribute>()
            ?? throw new InvalidOperationException($"{typeof(TRow).Name} has no [CacheTable] attribute.");
        return ValkeyKey.ToKebab(attr.Table);
    }

    private static string Name(CacheName name) => ValkeyKey.ToKebab(name.ToString());   // ClaimSummary -> claim-summary
    private static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);
}
```

**Builder**

```csharp
public enum KeyScope { Global, Row, Partner, Role, User, Session }

public sealed partial class ValkeyKey
{
    private const char Sep = ':';
    private const int MaxLength = 150;

    private readonly KeyScope _scope;
    private readonly string _root;            // g | p:{1042}
    private readonly string[] _ownerPath;     // [] | t | r:7 | u:55120 | u:55120:s:abc
    private readonly string _name;            // claim-summary | appointment (row keys)
    private readonly List<string> _qualifiers = [];
    private bool _hasHashedPart;
    private string? _id;
    private int? _version;                    // no suffix unless Version() is called

    internal ValkeyKey(KeyScope scope, long? partnerId, string[] ownerPath, string name)
    {
        if (scope == KeyScope.Global && partnerId is not null)
            throw new ArgumentException("Global keys cannot carry a partner ID.");
        if (scope != KeyScope.Global && partnerId is null or <= 0)
            throw new ArgumentException("Partner ID is required for row, partner, role, user and session keys.");

        _scope = scope;
        _root = scope == KeyScope.Global ? "g" : $"p:{{{partnerId}}}";   // p:{1042}
        _ownerPath = ownerPath.Select(Segment).ToArray();
        _name = NameSegment(name);
    }

    public KeyScope Scope => _scope;

    public ValkeyKey Id(long id) { _id = id.ToString(CultureInfo.InvariantCulture); return this; }
    public ValkeyKey Id(Guid id) { _id = id.ToString("N"); return this; }

    /// <summary>Fixed, developer-written variants only (e.g. "list", "today"). Never user input.</summary>
    public ValkeyKey Qualifier(params string[] parts)
    {
        foreach (var part in parts) _qualifiers.Add(Segment(part));
        return this;
    }

    /// <summary>
    /// For user input (search filters). Trims, lowercases and sorts every field, then hashes,
    /// so "Smith", " smith " and field order all give the same key, and no PHI reaches the key.
    /// </summary>
    public ValkeyKey FilterHash<T>(T filter) where T : notnull
    {
        var node = JsonSerializer.SerializeToNode(filter)!.AsObject();
        var clean = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (field, value) in node)
            clean[field.ToLowerInvariant()] = value?.ToString().Trim().ToLowerInvariant();

        var canonical = JsonSerializer.Serialize(clean);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        _qualifiers.Add(Convert.ToHexString(hash, 0, 8).ToLowerInvariant());
        _hasHashedPart = true;
        return this;
    }

    /// <summary>Optional. Only when the value's shape changes. No suffix = v1, so start at 2.</summary>
    public ValkeyKey Version(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 2);
        _version = version;
        return this;
    }

    public string Build()
    {
        var sb = new StringBuilder(OwnerPrefix()).Append(Sep).Append(_name);

        if (_id is not null) sb.Append(Sep).Append(_id);
        foreach (var q in _qualifiers) sb.Append(Sep).Append(q);
        if (_version is not null) sb.Append(Sep).Append('v').Append(_version);

        var key = sb.ToString();
        if (key.Length > MaxLength)
            throw new InvalidOperationException($"Key exceeds {MaxLength} chars: {key}.");
        return key;
    }

    /// <summary>Key shape with IDs and hashes masked, for logs and metrics: p:{*}:u:*:claim-search:*</summary>
    public string Template()
    {
        var sb = new StringBuilder(_scope == KeyScope.Global ? "g" : "p:{*}");
        foreach (var part in _ownerPath)
            sb.Append(Sep).Append(part.Length == 1 ? part : "*");   // keep markers t/r/u/s, mask IDs
        sb.Append(Sep).Append(_name);
        if (_id is not null) sb.Append(Sep).Append('*');
        foreach (var q in _qualifiers) sb.Append(Sep).Append(_hasHashedPart && q.Length == 16 ? "*" : q);
        if (_version is not null) sb.Append(Sep).Append('v').Append(_version);
        return sb.ToString();
    }

    /// <summary>SCAN pattern for everything owned by this partner/role/user/session.</summary>
    public string OwnerPattern() => $"{OwnerPrefix()}:*";

    /// <summary>SCAN pattern for this name under this owner, all ids/qualifiers/versions.</summary>
    public string NamePattern() => $"{OwnerPrefix()}:{_name}*";

    /// <summary>Default TTL per scope with ±10% jitter. Keys with user-input hashes get 5 minutes.</summary>
    public TimeSpan DefaultTtl(TimeSpan? sessionTimeout = null)
    {
        if (_hasHashedPart) return TimeSpan.FromMinutes(5);
        var baseTtl = _scope switch
        {
            KeyScope.Global  => TimeSpan.FromHours(24),
            KeyScope.Row     => TimeSpan.FromHours(1),
            KeyScope.Partner => TimeSpan.FromHours(1),
            KeyScope.Role    => TimeSpan.FromMinutes(30),
            KeyScope.User    => TimeSpan.FromMinutes(30),
            KeyScope.Session => sessionTimeout ?? TimeSpan.FromMinutes(20),
            _ => TimeSpan.FromMinutes(30)
        };
        if (_scope == KeyScope.Session) return baseTtl; // never outlive the session
        var jitter = 1 + (Random.Shared.NextDouble() * 0.2 - 0.1);
        return TimeSpan.FromSeconds(baseTtl.TotalSeconds * jitter);
    }

    public override string ToString() => Build();
    public static implicit operator string(ValkeyKey key) => key.Build();

    private string OwnerPrefix()
        => _ownerPath.Length == 0 ? _root : $"{_root}{Sep}{string.Join(Sep, _ownerPath)}";

    /// <summary>ClaimSummary -> claim-summary, Client_Address -> client-address.</summary>
    internal static string ToKebab(string value)
        => KebabRegex().Replace(value, "$1-$2").Replace('_', '-').ToLowerInvariant();

    /// <summary>Names sit right after the owner path, so they must never look like a scope marker.</summary>
    private static string NameSegment(string value)
    {
        var v = Segment(value);
        if (v.Length < 2)
            throw new ArgumentException("Names need 2+ characters; single letters are scope markers.");
        return v;
    }

    internal static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Key segment cannot be empty.");
        if (!SegmentRegex().IsMatch(value))      // no silent trimming/lowercasing: wrong input fails loudly
            throw new ArgumentException($"Invalid key segment '{value}'. Allowed: a-z, 0-9, '-'.");
        if (VersionLikeRegex().IsMatch(value))
            throw new ArgumentException($"'{value}' looks like a version suffix; use Version() instead.");
        return value;
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex SegmentRegex();

    [GeneratedRegex("^v[0-9]+$")]
    private static partial Regex VersionLikeRegex();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex KebabRegex();
}
```

**Key definitions** — the team that owns a name writes its key method once; other teams call it:

```csharp
// Owned by Billing, using shared names from CacheName.
public static class BillingKeys
{
    public static ValkeyKey ClaimSummary(this IValkeyKeyFactory keys, long partnerId, long claimId)
        => keys.Partner(partnerId, CacheName.ClaimSummary).Id(claimId);

    public static ValkeyKey ClaimSearch(this IValkeyKeyFactory keys, long partnerId, long userId, ClaimSearchFilter filter)
        => keys.User(partnerId, userId, CacheName.ClaimSearch).FilterHash(filter);
}

public sealed record ClaimSearchFilter(string? PatientName, string? Status, long? PayerId);
```

**Usage** (StackExchange.Redis works unchanged against Valkey)

```csharp
// A single row: shared key, shared loader
var appt = await _rows.GetAsync<AppointmentRow>(tenant.PartnerId, appointmentId, ct);
// p:{1042}:t:appointment:88731

// A view
var key = _keys.ClaimSummary(tenant.PartnerId, claimId);
await _cache.SetAsync(key, summary);              // TTL defaults to key.DefaultTtl()
// p:{1042}:claim-summary:3301

// Shape changed in a breaking way? Add a version to that one key only
_keys.Partner(partnerId, CacheName.ClaimSummary).Id(claimId).Version(2);
// p:{1042}:claim-summary:3301:v2
```

**Guardrails**

- Add a NetArchTest rule (or Roslyn analyzer) that fails the build if `IDatabase` string-key methods are called with a string literal or interpolated string outside `ValkeyKey`.
- Unit-test each key definition against the expected string, so a renamed part or version change shows up in code review.
- Optional: a `ForCurrentPartner(...)` overload that reads the partner ID from `Kernel.Tenancy`, so callers cannot pass the wrong tenant.

## Preventing naming mistakes and user-input drift

No free text reaches the key builder. Table names come from the row DTO, every other name comes from one shared `CacheName` enum, IDs are typed, and user input is cleaned and hashed, so the same data always gets the same key.

**Problem 1 — teams name the same thing differently, or use the same name for different things.** With plain strings, Billing writes `"sheduleing-list"` and Medical writes `"schedule-list"` for the same data, and two teams can both call their own data `"summary"`:

```
p:{1042}:sheduleing-list    ← Billing   (same data, two keys)
p:{1042}:schedule-list      ← Medical
p:{1042}:summary            ← Billing claims AND Clinical notes (different data, one key)
```

Fixes:

- **Row data uses the row key.** Both teams call `_rows.GetAsync<AppointmentRow>(...)` and get `p:{1042}:t:appointment:88731`. There is no name to type.
- **One `CacheName` enum for every other key.** Everyone picks from the same list in Common.API. A typo does not compile, IntelliSense shows the list, and each name exists only once, so two teams cannot both claim `summary`. Names must be specific: `claim-summary`, `note-summary`.
- **One owner per name.** The registry records which team owns each name; other teams call the owner's key method instead of building the key themselves.

**Problem 2 — user input changes the key.** A search for `"Smith"`, `"smith "`, or the same filters in a different order would otherwise create different keys, cause cache misses, and put patient names into keys.

Fixes:

1. **Typed IDs only.** `Id(long)` / `Id(Guid)` and `long partnerId` — a user cannot type a value that changes the key's shape.
2. **User filters go through `FilterHash(filter)`.** It trims, lowercases and sorts every field, then hashes to 16 characters. `"Smith"`, `" SMITH"` and any field order give the same key, and no PHI appears in it:

```
p:{1042}:u:55120:claim-search:a3f9c21b7e4d0a55
```

3. **`Qualifier()` is for fixed, developer-written words only** (`"list"`, `"today"`). It rejects anything outside `a-z 0-9 -` instead of silently changing it, so bad input fails in testing, not in production.
4. **Keys built from user input get a 5-minute TTL** by default, so search keys clean themselves up.

**Finding which key is which.** Log `key.Template()` (`p:{*}:u:*:claim-search:*`) instead of the full key, and track hit/miss per template. In Dev/QA, also log the filter next to its hash so a hash can be traced back. For a row, the key itself says everything: `p:{1042}:t:appointment:88731`.

## Suggestion: Common.API shared package

Create one **Common.API** repo for everything every API repeats today: middleware pipeline, authentication, authorization, and the Redis/Valkey connection plus the key builder and row cache. Today the same code is copied into each API solution in slightly different versions, so one fix means editing every API.

**What moves into Common.API**

| Area | Contents |
| --- | --- |
| Middleware pipeline | Exception handling, correlation ID, request logging, security headers, CORS defaults |
| Authentication | JWT bearer setup, token validation, claims mapping (partner, user, role) |
| Authorization | Policies, permission attribute, partner-access check |
| Tenancy | `ITenantContext` resolved from claims (partner ID, user ID, role ID) |
| Caching | One Valkey/Redis connection (`IConnectionMultiplexer`), `IValkeyCache`, `IRowCache`, row DTOs, `ValkeyKey`, `CacheName` |
| Observability | Logging, tracing and health checks |

Valkey speaks the Redis protocol, so one `StackExchange.Redis` connection setup serves both. Each API points it at its environment's endpoint through configuration.

**Repo layout**

```
Common.API/
  src/
    Common.Api.Hosting/        middleware pipeline, AddCommonApi()/UseCommonApi()
    Common.Api.Security/       authentication, authorization, tenancy
    Common.Api.Caching/        Valkey connection, IValkeyCache, IRowCache, row DTOs, key builder, CacheName
    Common.Api.Observability/  logging, tracing, health checks
  tests/                       golden key tests, middleware and auth tests
  Common.API.sln
```

Each folder ships as its own NuGet package, so a change to auth does not force a caching upgrade. A single package is simpler, but every small fix would then reach every API.

**What each API's `Program.cs` becomes**

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCommonApi(builder.Configuration, options =>
{
    options.ServiceName = "billing-api";
    options.UseValkey = true;
});

builder.Services.AddControllers();

var app = builder.Build();
app.UseCommonApi();          // exception handling, correlation ID, auth, authz in the right order
app.MapControllers();
app.Run();
```

```csharp
// Inside Common.Api.Hosting
public static IServiceCollection AddCommonApi(this IServiceCollection services,
    IConfiguration config, Action<CommonApiOptions>? configure = null)
{
    var options = new CommonApiOptions();
    configure?.Invoke(options);

    services.AddCommonSecurity(config);        // JWT, policies, ITenantContext
    services.AddCommonObservability(options);  // logging, tracing, health checks
    if (options.UseValkey)
        services.AddValkey(config);            // one IConnectionMultiplexer, IValkeyCache, IRowCache, IValkeyKeyFactory
    return services;
}

public static WebApplication UseCommonApi(this WebApplication app)
{
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseAuthentication();
    app.UseMiddleware<TenantResolutionMiddleware>();
    app.UseAuthorization();
    app.MapHealthChecks("/health");
    return app;
}
```

Every API then gets the same pipeline in the same order, and fixing a middleware bug is one change in Common.API.

**Keeping it approval-friendly.** Our approval process applies at every stage, so Common.API is designed to change rarely:

- Only stable, non-domain code goes in. Billing rules stay in Billing.
- The key format is frozen in version 1.x. Golden tests check exact key strings, so a format change cannot slip in. A format change would be 2.0.0 — a planned project, not a routine update.
- Repos on different 1.x versions still produce identical keys, so there is no rush to update all 13 at once.
- Adding a column to a row DTO is backward compatible. Removing or renaming one adds `.Version(2)` to that table's row key.
- Each API adopts a new version **in its next regular release**, through the approvals that release already has. Urgent fixes go out under one change request covering all affected services.
- Adding a name to `CacheName` is a small, backward-compatible change that rides each API's regular release.

## Using the same builder in every repo

The builder ships inside Common.API as the `Common.Api.Caching` package, and that package is the only supported way to reach Valkey. Every repo then uses the same rules, and checks in the build, in Valkey and in an audit job catch anything that slips through.

**1. One shared package**

- Publish `Common.Api.Caching` (with the other Common.API packages) to the internal feed (Azure Artifacts, AWS CodeArtifact or GitHub Packages).
- It contains `ValkeyKey`, `CacheName`, `IValkeyKeyFactory`, `IValkeyCache`, `IRowCache`, the row DTOs and the DI extension `services.AddValkey(configuration)`.
- It follows semantic versioning: a key-format change is a **major** version. Golden-string unit tests inside the package lock the format.
- Each repo pins the version with Central Package Management (`Directory.Packages.props`) and updates it in its next regular release.

**2. Make the raw client unreachable**

The package exposes wrappers that accept only `ValkeyKey`, never `string`. Developers have no reason to touch `IDatabase` directly:

```csharp
public interface IValkeyCache
{
    Task<T?> GetAsync<T>(ValkeyKey key, CancellationToken ct = default);   // bad payload = miss + delete
    Task SetAsync<T>(ValkeyKey key, T value, TimeSpan? ttl = null, CancellationToken ct = default); // defaults to key.DefaultTtl()
    Task RemoveAsync(ValkeyKey key, CancellationToken ct = default);
    Task<long> RemoveByOwnerAsync(ValkeyKey ownerKey, CancellationToken ct = default);           // SCAN on every primary
    Task PublishAsync<T>(ValkeyKey channel, T message, CancellationToken ct = default);
}
```

`AddValkey()` registers `IConnectionMultiplexer` as internal to the package, so no other code needs it.

**3. Fail the build on direct use**

- The package carries `Microsoft.CodeAnalysis.BannedApiAnalyzers` and a `BannedSymbols.txt` through `buildTransitive`. Every consuming repo then gets it automatically.
- The banned list covers `StackExchange.Redis.IDatabase`, `IServer`, `ConnectionMultiplexer.Connect*` and `IDistributedCache`. Any direct call becomes a compile error, set to error in `.editorconfig`.
- Add a NetArchTest rule in each repo's architecture tests: no assembly except the package references `StackExchange.Redis`.

**4. Enforce in Valkey itself**

Give each service its own Valkey ACL user, allowed only on the convention's prefixes:

```
ACL SETUSER svc-scheduling on >*** ~p:* ~g:* &p:* +@all -@dangerous -keys -flushall -flushdb
```

A key outside `p:*` / `g:*` is rejected by the server, whatever language or repo wrote it. On ElastiCache this is an RBAC user with the same access string.

**5. Audit what is already there**

- A scheduled job SCANs each environment and counts keys that fail the convention regex or have no TTL. It reports the count to the dashboard and alerts above zero.
- New repos get the package and analyzer from the service template, so they start compliant.

**Non-.NET writers** (for example the CDC Lambda in Python or Node): use a small port of the builder. Validate it against a shared `key-test-vectors.json` that ships with the package, listing inputs and expected keys. Both implementations run the same vectors in CI, so they cannot drift.

## Key registry

Every new key is added to this table in the same PR that introduces it. The rows below are examples to replace with real keys. "Depends on" lists the tables whose changes make CDC delete the key.

| Key template | Scope | Type | TTL | Owner | Depends on (CDC deletes when changed) | Builder method |
| --- | --- | --- | --- | --- | --- | --- |
| `g:feature-flag` | Global | Hash | 24 h | Platform | FeatureFlag | `PlatformKeys.FeatureFlags` |
| `p:{<partnerId>}:t:appointment:<appointmentId>` | Row | String (JSON) | 1 h | Common.API | Appointment | `IRowCache.GetAsync<AppointmentRow>` |
| `p:{<partnerId>}:t:client:<clientId>` | Row | String (JSON) | 1 h | Common.API | Client | `IRowCache.GetAsync<ClientRow>` |
| `p:{<partnerId>}:claim-summary:<claimId>` | Partner | String (JSON) | 1 h | Billing | Claim, Appointment, Payer | `BillingKeys.ClaimSummary` |
| `p:{<partnerId>}:home-metrics` | Partner | String (JSON) | 1 h | Dashboard | Appointment, Claim, Note | `DashboardKeys.HomeMetrics` |
| `p:{<partnerId>}:r:<roleId>:role-menu` | Role | String (JSON) | 30 min | Security | Role, RolePermission, Menu | `SecurityKeys.RoleMenu` |
| `p:{<partnerId>}:u:<userId>:claim-search:<hash>` | User | String (JSON) | 5 min | Billing | — (short TTL) | `BillingKeys.ClaimSearch` |
| `p:{<partnerId>}:u:<userId>:s:<sessionId>:sse-connection` | Session | String | Session timeout | Realtime | — | `RealtimeKeys.SseConnection` |
| `p:{<partnerId>}:ch-client-update` | Partner | Pub/Sub channel | — | Realtime | — | `RealtimeKeys.ClientUpdateChannel` |

## Rules

**Do**

- Cache a single table row only through `IRowCache` and its shared row key.
- Build every other key through `IValkeyKeyFactory` and a name from `CacheName`.
- List the tables a cached view depends on in the registry, so CDC can delete it.
- Set a TTL on every key; delete session keys explicitly on logout.
- Add `.Version(n)` only when a value's shape changes in a breaking way.
- Add the key to the registry in the same PR.

**Don't**

- Cache your own copy of a table row under a view name.
- Put PHI, names, emails, MRNs or tokens inside a key.
- Use `KEYS` in any environment except local dev; use `SCAN` (on every primary node in cluster mode) or direct deletes.
- Reuse a `CacheName` for different data — add a new, specific name instead.
- Store unbounded lists or values larger than ~100 KB under one key.

## Open questions

- [ ] Which tables are cacheable as rows, and which columns must be left out of their DTOs (PHI)?
- [ ] Does every cacheable table carry `PartnerId`, so the CDC Lambda can build the row key from the change event alone?
- [ ] Which language does the CDC Lambda use, and does it need the builder port?
- [ ] Are role IDs unique across partners, or only within a partner? The format assumes within a partner.
- [ ] Session ID source: auth session ID, or a hash of it so the raw value never appears in keys?
- [ ] Is any PHI cached in Valkey today, and does it need an encryption-at-value layer?
- [ ] Are we on Valkey Cluster or a single primary with replicas? This decides how scan-based invalidation runs.
- [ ] If more than one product shares a Valkey instance later, do we add a product prefix then?
- [ ] Migration plan for existing keys: dual-write and read-through, or a cut-over per module?
- [ ] Owning team and internal feed for Common.API; final package names for `Common.Api.*`.
- [ ] Which repos write to Valkey from non-.NET code and need the port?
