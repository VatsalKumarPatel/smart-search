# Valkey Key Naming Convention & Key Builder

Nov 5, 2026 · @Vatsalkumar Patel

## Purpose and status

Every key we write to Valkey follows one format and is built through one class, `ValkeyKey`, so no developer hand-types a key string. Status: **Proposal — for team review.**

Why now: keys are currently created ad hoc across modules, which makes them hard to find, hard to invalidate, and risky across tenants (a key missing the partner ID can leak data between partners).

**Goals**

- Predictable keys: anyone can tell owner, scope and purpose from the key alone.
- Tenant safety: partner-, user-, role- and session-scoped data always carries its owning IDs.
- Easy invalidation: clear everything for one partner, user, role or session with a single `SCAN` pattern.
- One place to change: the builder class and the key registry on this page.

**In scope:** cache entries, session data, Pub/Sub channels, Streams, locks and counters in Valkey. **Out of scope:** Valkey cluster topology, eviction policy tuning, and data retention rules for PHI.

## Key format

Each Valkey instance belongs to one environment, so keys carry no app or environment prefix. Every tenant key starts with the partner, and the owner path narrows from partner to role or user to session:

```
Global:  g:{module}:{entity}[:{id}][:{qualifier}...]:v{n}
Partner: p:{<partnerId>}:{module}:{entity}[:{id}][:{qualifier}...]:v{n}
Role:    p:{<partnerId>}:r:{roleId}:{module}:{entity}[...]:v{n}
User:    p:{<partnerId>}:u:{userId}:{module}:{entity}[...]:v{n}
Session: p:{<partnerId>}:u:{userId}:s:{sessionId}:{module}:{entity}[...]:v{n}
```

| Part | Required | Rule | Example |
| --- | --- | --- | --- |
| Root | Yes | `g` for global data, or `p:{partnerId}` for anything owned by a partner. The braces are literal (cluster hash tag) | `p:{1042}` |
| Owner path | Per scope | `r:{roleId}`, `u:{userId}`, or `u:{userId}:s:{sessionId}`; omitted for partner-level data | `u:55120` |
| `module` | Yes | Always from the `KeyModule` enum — never typed as text | `scheduling` |
| `entity` | Yes | Noun for what is stored, singular; defined once by the owning module | `appointment` |
| `id` | Optional | Typed `long` or `Guid` of the entity instance | `88731` |
| `qualifier` | Optional | Fixed variant (`list:today`) or `FilterHash` of user input | `a3f9c21b7e4d0a55` |
| `v{n}` | Yes | Schema version of the value; bump when the payload shape changes | `v1` |

**Grammar rules**

1. Colon `:` is the only separator. Words inside a part use kebab-case: `home-metrics`, never `HomeMetrics` or `home_metrics`.
2. Lowercase ASCII letters, digits and hyphens only. No spaces, dots, slashes or emails.
3. Single letters `g`, `p`, `r`, `u`, `s` are reserved scope markers; module names must be 2+ characters so they never collide.
4. IDs are internal numeric or GUID IDs only — never names, MRNs, SSNs, emails or any PHI. Keys show up in logs, `MONITOR` and `SCAN` output.
5. GUIDs are written lowercase without braces or hyphens (`n` format) to keep keys short.
6. Target total length is under 150 characters; hash long variable input (for example a search filter) with SHA-256 and use the first 16 hex characters as the qualifier.
7. Because users, roles and sessions all belong to a partner, every non-global key starts with `p:{partnerId}`. One partner's data then sits under one prefix and one cluster slot.

## Scopes

Pick the narrowest scope that is still shared by everyone who reads the value. Every scope below global includes the partner, so tenant isolation is built into the key.

| Scope | Code | Key starts with | Use for | Example key |
| --- | --- | --- | --- | --- |
| Global | `g` | `g:` | Reference data identical for all partners: lookup lists, feature catalogue, app config | `g:config:feature-flag:v1` |
| Partner | `p` | `p:{partnerId}:` | Partner settings, partner-level lookups, dashboard aggregates | `p:{1042}:billing:payer-list:v2` |
| Role | `r` | `p:{partnerId}:r:{roleId}:` | Role permissions, menu/sidebar per role | `p:{1042}:r:7:security:menu:v1` |
| User | `u` | `p:{partnerId}:u:{userId}:` | User preferences, recent clients, user-level permission overrides | `p:{1042}:u:55120:home:metrics:v1` |
| Session | `s` | `p:{partnerId}:u:{userId}:s:{sessionId}:` | Login session data, wizard state, SSE connection tracking | `p:{1042}:u:55120:s:9f2c41ab7d0e4c1a:auth:token-state:v1` |

**Choosing a scope**

- Value the same for every partner → `g`.
- Value differs per partner but not per person → `p`.
- Value depends on what a role can see (menus, permission sets) → `r`. Prefer this over `u` for permissions: one entry serves every user with that role.
- Value is personal to one user and should survive logout → `u`.
- Value must disappear at logout or session expiry → `s`, always with a TTL no longer than the session timeout.

**Invalidation patterns** (use `SCAN` with `MATCH`, never `KEYS` in production)

| Clear | Pattern |
| --- | --- |
| All of one partner | `p:{1042}:*` |
| One role | `p:{1042}:r:7:*` |
| One user (incl. their sessions) | `p:{1042}:u:55120:*` |
| One session | `p:{1042}:u:55120:s:9f2c41ab7d0e4c1a:*` |
| All global data of one module | `g:scheduling:*` |

For high-traffic invalidation, prefer a generation counter over `SCAN`: store `p:{1042}:sys:gen:v1` and include its value in the qualifier, so bumping the counter makes old keys unreachable and they expire by TTL.

## TTL, data types and operations

Every key gets a TTL unless the registry marks it as permanent and names an owner who cleans it up.

| Scope | Default TTL | Max TTL | Notes |
| --- | --- | --- | --- |
| Global | 24 h | 7 days | Refresh on deploy or on reference-data change |
| Partner | 1 h | 24 h | Invalidate on partner settings change |
| Role | 30 min | 8 h | Invalidate on role/permission change |
| User | 30 min | 24 h | Sliding expiry allowed |
| Session | = session timeout | = session timeout | Delete explicitly on logout |

Add ±10% random jitter to TTLs on bulk-loaded keys so they do not all expire in the same second.

**Non-cache key types** use the same format, with a reserved `entity` prefix so they are never confused with cached data:

| Kind | Entity prefix | Example |
| --- | --- | --- |
| Pub/Sub channel | `ch` | `p:{1042}:notify:ch-client-update:v1` |
| Stream | `st` | `p:{1042}:notify:st-cdc-event:v1` |
| Distributed lock | `lock` | `p:{1042}:billing:lock-claim-batch:3301:v1` |
| Counter / rate limit | `ctr` | `p:{1042}:u:55120:auth:ctr-login-fail:v1` |
| Generation counter | `gen` | `p:{1042}:sys:gen:v1` |

**Cluster and hash tags**

- Valkey Cluster routes a key by the text inside the first `{...}`. Writing the partner as `p:{1042}` keeps one partner's keys on one slot, so multi-key commands (`MGET`, transactions, Lua) work within a partner.
- Global keys have no braces, so they spread across slots.
- If one partner grows large enough to make its slot hot, move that partner's high-volume entities to a wider tag such as `p:{1042-scheduling}` — a change in the builder, not at call sites.
- On a single primary (no cluster) the braces are harmless and keep the format ready for cluster later.

**Values**

- Serialize with `System.Text.Json`; keep the payload shape tied to `v{n}`.
- Prefer a Hash over many small String keys when fields are read together.
- Do not store PHI in Valkey unless the entry is approved in the registry, encrypted at rest/in transit, and has a short TTL.

## Proposed key builder: `ValkeyKey`

Developers never concatenate key strings. They ask a factory for a key in a scope, pass the IDs and parts as parameters, and get back a validated string. The factory always starts tenant keys with the partner, so a user, role or session key cannot be built without its partner ID.

**Design**

- `IValkeyKeyFactory` — one method per scope; the method signature forces the right owning IDs (you cannot build a user key without a partner ID).
- `KeyModule` enum — the only way to name a module, so `"sheduleing"` vs `"Schedule"` cannot happen.
- `ValkeyKey` — fluent builder for the optional parts (`Id`, `Qualifier`, `FilterHash`, `Version`), plus `Build()`, `Template()`, scan patterns and the default TTL for its scope.
- Key definitions owned by the module that owns the data — written once as extension methods, used by every team.

**Registration**

```csharp
// Program.cs — stateless, no configuration needed (each environment has its own Valkey)
builder.Services.AddSingleton<IValkeyKeyFactory, ValkeyKeyFactory>();
```

**Factory**

```csharp
/// <summary>Closed list of modules. Adding one is a reviewed package change (rare).</summary>
public enum KeyModule
{
    Core,
    Billing,
    Clinical,
    Medical,
    Scheduling,
    Security,
    Realtime,
    Platform
}

public interface IValkeyKeyFactory
{
    ValkeyKey Global(KeyModule module, string entity);
    ValkeyKey Partner(long partnerId, KeyModule module, string entity);
    ValkeyKey Role(long partnerId, long roleId, KeyModule module, string entity);
    ValkeyKey User(long partnerId, long userId, KeyModule module, string entity);
    ValkeyKey Session(long partnerId, long userId, string sessionId, KeyModule module, string entity);
}

public sealed class ValkeyKeyFactory : IValkeyKeyFactory
{
    public ValkeyKey Global(KeyModule module, string entity)
        => new(KeyScope.Global, null, [], module, entity);

    public ValkeyKey Partner(long partnerId, KeyModule module, string entity)
        => new(KeyScope.Partner, partnerId, [], module, entity);

    public ValkeyKey Role(long partnerId, long roleId, KeyModule module, string entity)
        => new(KeyScope.Role, partnerId, ["r", Str(roleId)], module, entity);

    public ValkeyKey User(long partnerId, long userId, KeyModule module, string entity)
        => new(KeyScope.User, partnerId, ["u", Str(userId)], module, entity);

    public ValkeyKey Session(long partnerId, long userId, string sessionId, KeyModule module, string entity)
        => new(KeyScope.Session, partnerId, ["u", Str(userId), "s", sessionId], module, entity);

    private static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);
}
```

**Builder**

```csharp
public enum KeyScope { Global, Partner, Role, User, Session }

public sealed partial class ValkeyKey
{
    private const char Sep = ':';
    private const int MaxLength = 150;

    private readonly KeyScope _scope;
    private readonly string _root;            // g | p:{1042}
    private readonly string[] _ownerPath;     // [] | r:7 | u:55120 | u:55120:s:abc
    private readonly string _module;
    private readonly string _entity;
    private readonly List<string> _qualifiers = [];
    private bool _hasHashedPart;
    private string? _id;
    private int _version = 1;

    internal ValkeyKey(KeyScope scope, long? partnerId,
                       string[] ownerPath, KeyModule module, string entity)
    {
        if (scope == KeyScope.Global && partnerId is not null)
            throw new ArgumentException("Global keys cannot carry a partner ID.");
        if (scope != KeyScope.Global && partnerId is null or <= 0)
            throw new ArgumentException("Partner ID is required for partner, role, user and session keys.");

        _scope = scope;
        _root = scope == KeyScope.Global ? "g" : $"p:{{{partnerId}}}";   // p:{1042}
        _ownerPath = ownerPath.Select(Segment).ToArray();
        _module = ToKebab(module.ToString());                              // Scheduling -> scheduling
        _entity = Segment(entity);
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
        foreach (var (name, value) in node)
            clean[name.ToLowerInvariant()] = value?.ToString().Trim().ToLowerInvariant();

        var canonical = JsonSerializer.Serialize(clean);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        _qualifiers.Add(Convert.ToHexString(hash, 0, 8).ToLowerInvariant());
        _hasHashedPart = true;
        return this;
    }

    public ValkeyKey Version(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        _version = version;
        return this;
    }

    public string Build()
    {
        var sb = new StringBuilder(OwnerPrefix())
            .Append(Sep).Append(_module)
            .Append(Sep).Append(_entity);

        if (_id is not null) sb.Append(Sep).Append(_id);
        foreach (var q in _qualifiers) sb.Append(Sep).Append(q);
        sb.Append(Sep).Append('v').Append(_version);

        var key = sb.ToString();
        if (key.Length > MaxLength)
            throw new InvalidOperationException($"Key exceeds {MaxLength} chars: {key}.");
        return key;
    }

    /// <summary>Key shape with IDs and hashes masked, for logs and metrics: p:{*}:u:*:billing:claim-search:*:v1</summary>
    public string Template()
    {
        var sb = new StringBuilder(_scope == KeyScope.Global ? "g" : "p:{*}");
        for (var i = 0; i < _ownerPath.Length; i += 2)
            sb.Append(Sep).Append(_ownerPath[i]).Append(Sep).Append('*');
        sb.Append(Sep).Append(_module).Append(Sep).Append(_entity);
        if (_id is not null) sb.Append(Sep).Append('*');
        foreach (var q in _qualifiers) sb.Append(Sep).Append(_hasHashedPart && q.Length == 16 ? "*" : q);
        return sb.Append(Sep).Append('v').Append(_version).ToString();
    }

    /// <summary>SCAN pattern for everything owned by this partner/role/user/session.</summary>
    public string OwnerPattern() => $"{OwnerPrefix()}:*";

    /// <summary>SCAN pattern for this entity under this owner, all ids/qualifiers/versions.</summary>
    public string EntityPattern() => $"{OwnerPrefix()}:{_module}:{_entity}:*";

    /// <summary>Default TTL per scope with ±10% jitter. Keys with user-input hashes get 5 minutes.</summary>
    public TimeSpan DefaultTtl(TimeSpan? sessionTimeout = null)
    {
        if (_hasHashedPart) return TimeSpan.FromMinutes(5);
        var baseTtl = _scope switch
        {
            KeyScope.Global  => TimeSpan.FromHours(24),
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

    private static string ToKebab(string pascal)
        => KebabRegex().Replace(pascal, "$1-$2").ToLowerInvariant();     // ClaimSearch -> claim-search

    internal static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Key segment cannot be empty.");
        if (!SegmentRegex().IsMatch(value))      // no silent trimming/lowercasing: wrong input fails loudly
            throw new ArgumentException($"Invalid key segment '{value}'. Allowed: a-z, 0-9, '-'.");
        return value;
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex SegmentRegex();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex KebabRegex();
}
```

**Module key definitions** — each module owns its keys in one file, so names and versions are never repeated at call sites:

```csharp
// Owned by Scheduling. Shared keys live in Common.API so every team calls the same method.
public static class SchedulingKeys
{
    public static ValkeyKey Appointment(this IValkeyKeyFactory keys, long partnerId, long appointmentId)
        => keys.Partner(partnerId, KeyModule.Scheduling, "appointment").Id(appointmentId).Version(1);

    public static ValkeyKey TodayAppointments(this IValkeyKeyFactory keys, long partnerId, long userId)
        => keys.User(partnerId, userId, KeyModule.Scheduling, "appointment").Qualifier("list", "today").Version(1);
}

// Billing-only key: lives in the Billing repo, still uses the KeyModule enum.
internal static class BillingKeys
{
    public static ValkeyKey ClaimSearch(this IValkeyKeyFactory keys, long partnerId, long userId, ClaimSearchFilter filter)
        => keys.User(partnerId, userId, KeyModule.Billing, "claim-search").FilterHash(filter).Version(1);
}

public sealed record ClaimSearchFilter(string? PatientName, string? Status, long? PayerId);
```

**Usage** (StackExchange.Redis works unchanged against Valkey)

```csharp
var key = _keys.TodayAppointments(tenant.PartnerId, user.Id);

await _db.StringSetAsync(key.Build(), JsonSerializer.Serialize(list), key.DefaultTtl());
// p:{1042}:u:55120:scheduling:appointment:list:today:v1

// Clear everything for one user on role change
await foreach (var k in _server.KeysAsync(pattern: key.OwnerPattern(), pageSize: 500))
    await _db.KeyDeleteAsync(k);
```

**Guardrails**

- Add a NetArchTest rule (or Roslyn analyzer) that fails the build if `IDatabase` string-key methods are called with a string literal or interpolated string outside `ValkeyKey`.
- Unit-test each module's key definitions against the expected string, so a renamed part or version bump shows up in code review.
- Optional: a `ForCurrentPartner(...)` overload that reads the partner ID from `Kernel.Tenancy`, so callers cannot pass the wrong tenant.

## Preventing naming mistakes and user-input drift

No free text reaches the key builder. Module names come from an enum, IDs are typed, and user input is cleaned and hashed, so the same data always gets the same key.

**Problem 1 — different teams spell modules differently.** With plain strings, Billing writes `"sheduleing"` and Medical writes `"Schedule"`. Both compile, but they create two keys for the same data:

```
p:{1042}:sheduleing:appointment:v1   ← Billing
p:{1042}:schedule:appointment:v1     ← Medical
```

Fixes:

- **`KeyModule` enum.** Everyone writes `KeyModule.Scheduling`. A typo does not compile, and IntelliSense shows the list.
- **The owner defines a module's keys once.** Scheduling writes `SchedulingKeys.Appointment(...)`; Billing and Medical call that method instead of typing `"appointment"`. Need a key that doesn't exist? Ask the owner to add it.

| Key used by | Where the definition lives |
| --- | --- |
| One repo only | That repo, using `KeyModule` |
| More than one domain | Common.API package, owned by the owning domain |

**Problem 2 — user input changes the key.** A search for `"Smith"`, `"smith "`, or the same filters in a different order would otherwise create different keys, cause cache misses, and put patient names into keys.

Fixes:

1. **Typed IDs only.** `Id(long)` / `Id(Guid)` and `long partnerId` — a user cannot type a value that changes the key's shape.
2. **User filters go through `FilterHash(filter)`.** It trims, lowercases and sorts every field, then hashes to 16 characters. `"Smith"`, `" SMITH"` and any field order give the same key, and no PHI appears in it:

```
p:{1042}:u:55120:billing:claim-search:a3f9c21b7e4d0a55:v1
```

3. **`Qualifier()` is for fixed, developer-written words only** (`"list"`, `"today"`). It rejects anything outside `a-z 0-9 -` instead of silently changing it, so bad input fails in testing, not in production.
4. **Keys built from user input get a 5-minute TTL** by default, so search keys clean themselves up.

**Finding which key is which.** Log `key.Template()` (`p:{*}:u:*:billing:claim-search:*:v1`) instead of the full key, and track hit/miss per template. In Dev/QA, also log the filter next to its hash so a hash can be traced back.

## Suggestion: Common.API shared package

Create one **Common.API** repo for everything every API repeats today: middleware pipeline, authentication, authorization, and the Redis/Valkey connection plus the key builder. Today the same code is copied into each API solution in slightly different versions, so one fix means editing every API.

**What moves into Common.API**

| Area | Contents |
| --- | --- |
| Middleware pipeline | Exception handling, correlation ID, request logging, security headers, CORS defaults |
| Authentication | JWT bearer setup, token validation, claims mapping (partner, user, role) |
| Authorization | Policies, permission attribute, partner-access check |
| Tenancy | `ITenantContext` resolved from claims (partner ID, user ID, role ID) |
| Caching | One Valkey/Redis connection (`IConnectionMultiplexer`), `IValkeyCache`, `ValkeyKey`, `KeyModule`, shared key definitions |
| Observability | Logging, tracing and health checks |

Valkey speaks the Redis protocol, so one `StackExchange.Redis` connection setup serves both. Each API points it at its environment's endpoint through configuration.

**Repo layout**

```
Common.API/
  src/
    Common.Api.Hosting/        middleware pipeline, AddCommonApi()/UseCommonApi()
    Common.Api.Security/       authentication, authorization, tenancy
    Common.Api.Caching/        Valkey connection, IValkeyCache, key builder, KeyModule, shared keys
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
        services.AddValkey(config);            // one IConnectionMultiplexer, IValkeyCache, IValkeyKeyFactory
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
- The Valkey key format is frozen in version 1.x. Golden tests check exact key strings, so a format change cannot slip in. A format change would be 2.0.0 — a planned project, not a routine update.
- Repos on different 1.x versions still produce identical keys, so there is no rush to update all 13 at once.
- Each API adopts a new version **in its next regular release**, through the approvals that release already has. Urgent fixes go out under one change request covering all affected services.
- New repo-specific keys need no package change: they live in that repo and use `KeyModule`.
## Using the same builder in every repo

The builder ships inside Common.API as the `Common.Api.Caching` package, and that package is the only supported way to reach Valkey. Every repo then uses the same rules, and checks in the build, in Valkey and in an audit job catch anything that slips through.

**1. One shared package**

- Publish `Common.Api.Caching` (with the other Common.API packages) to the internal feed (Azure Artifacts, AWS CodeArtifact or GitHub Packages).
- It contains `ValkeyKey`, `KeyModule`, `IValkeyKeyFactory`, `IValkeyCache`, shared key definitions and the DI extension `services.AddValkey(configuration)`.
- It follows semantic versioning: a key-format change is a **major** version. Golden-string unit tests inside the package lock the format.
- Each repo pins the version with Central Package Management (`Directory.Packages.props`) and updates it in its next regular release.

**2. Make the raw client unreachable**

The package exposes a wrapper that accepts only `ValkeyKey`, never `string`. Developers have no reason to touch `IDatabase` directly:

```csharp
public interface IValkeyCache
{
    Task<T?> GetAsync<T>(ValkeyKey key, CancellationToken ct = default);
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

**Non-.NET writers** (for example Lambdas in Python or Node): use a small port of the builder. Validate it against a shared `key-test-vectors.json` that ships with the package, listing inputs and expected keys. Both implementations run the same vectors in CI, so they cannot drift.

## Key registry

Every new key is added to this table in the same PR that introduces it. The rows below are examples to replace with real keys.

| Key template | Scope | Type | TTL | Owner module | Invalidated by | Builder method |
| --- | --- | --- | --- | --- | --- | --- |
| `g:config:feature-flag:v1` | Global | Hash | 24 h | Platform | Deploy, flag change | `PlatformKeys.FeatureFlags` |
| `p:{<partnerId>}:dashboard:home-metrics:v1` | Partner | String (JSON) | 1 h | Dashboard | Nightly job, manual refresh | `DashboardKeys.HomeMetrics` |
| `p:{<partnerId>}:r:<roleId>:security:menu:v1` | Role | String (JSON) | 30 min | Security | Role permission change | `SecurityKeys.RoleMenu` |
| `p:{<partnerId>}:u:<userId>:scheduling:appointment:list:today:v1` | User | String (JSON) | 30 min | Scheduling | Appointment create/update | `SchedulingKeys.TodayAppointments` |
| `p:{<partnerId>}:u:<userId>:s:<sessionId>:realtime:sse-connection:v1` | Session | String | Session timeout | Realtime | Logout, disconnect | `RealtimeKeys.SseConnection` |
| `p:{<partnerId>}:realtime:ch-client-update:v1` | Partner | Pub/Sub channel | — | Realtime | — | `RealtimeKeys.ClientUpdateChannel` |

## Rules

**Do**

- Build every key through `IValkeyKeyFactory` and a module key definition.
- Use the narrowest scope that is shared by all readers; prefer role over user for permissions.
- Set a TTL on every key; delete session keys explicitly on logout.
- Bump `v{n}` when the stored payload shape changes, instead of migrating old values.
- Add the key to the registry in the same PR.

**Don't**

- Put PHI, names, emails, MRNs or tokens inside a key.
- Use `KEYS` in any environment except local dev; use `SCAN` (on every primary node in cluster mode) or generation counters.
- Share one key between two modules — expose the data through the owning module's contract instead.
- Store unbounded lists or values larger than ~100 KB under one key.

## Open questions

- [ ] Are role IDs unique across partners, or only within a partner? The format assumes within a partner.
- [ ] Session ID source: auth session ID, or a hash of it so the raw value never appears in keys?
- [ ] Is any PHI cached in Valkey today, and does it need an encryption-at-value layer?
- [ ] Are we on Valkey Cluster or a single primary with replicas? This decides how scan-based invalidation runs.
- [ ] If more than one product shares a Valkey instance later, do we add a product prefix then?
- [ ] Migration plan for existing keys: dual-write and read-through, or a cut-over per module?
- [ ] Owning team and internal feed for Common.API; final package names for `Common.Api.*`.
- [ ] Which repos write to Valkey from non-.NET code and need the port?
