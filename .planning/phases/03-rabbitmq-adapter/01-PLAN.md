---
phase: 03-rabbitmq-adapter
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Directory.Packages.props
  - Oragon.AdaptivePool.sln
  - src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj
  - src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt
  - src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt
  - src/Oragon.AdaptivePool.RabbitMQ/Builder/AdaptiveConnectionPoolBuilder.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Options/AdaptiveConnectionPoolOptions.cs
  - src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs
  - src/Oragon.AdaptivePool.RabbitMQ/Internals/AdapterDiagnosticsLog.cs
autonomous: true
requirements: [RMQ-01, RMQ-04]
must_haves:
  truths:
    - "A new `Oragon.AdaptivePool.RabbitMQ` project exists, multi-targets net10/net9/net8, and is included in the solution."
    - "RabbitMQ.Client 7.2.1 is centrally pinned in Directory.Packages.props."
    - "Calling `services.AddAdaptiveConnectionPool(name, configureFactory, configurePool)` registers a working `IAdaptivePool<IConnection>` resolvable via `GetRequiredKeyedService<IAdaptivePool<IConnection>>(name)` (and, for `name == string.Empty`, also via non-keyed `IAdaptivePool<IConnection>`)."
    - "The connection pool factory hook resolves an `IConnectionFactory` in priority order: keyed singleton by name → closure callback → IOptions binding; throws InvalidOperationException with a clear message when none provided."
    - "The adapter forces `ConnectionFactory.AutomaticRecoveryEnabled = false` after the consumer's `configureFactory` runs and emits a `LogLevel.Warning` if the consumer had set it true."
    - "BeforeUse/Check hooks return Unhealthy when `IConnection.IsOpen` is false; Release calls `CloseAsync()` then `DisposeAsync()` swallowing exceptions during close."
    - "All public types have entries in PublicAPI.Unshipped.txt (PublicApiAnalyzers active per Core convention)."
  artifacts:
    - path: "src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj"
      provides: "RabbitMQ adapter package, multi-target net10/9/8, NuGet metadata, PublicApiAnalyzers"
      contains: "TargetFrameworks>net10.0;net9.0;net8.0"
    - path: "src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs"
      provides: "AddAdaptiveConnectionPool extension method"
      exports: ["AddAdaptiveConnectionPool"]
    - path: "src/Oragon.AdaptivePool.RabbitMQ/Builder/AdaptiveConnectionPoolBuilder.cs"
      provides: "Connection-pool builder with WithBounds/IdleTimeout/etc."
      exports: ["AdaptiveConnectionPoolBuilder"]
    - path: "src/Oragon.AdaptivePool.RabbitMQ/Options/AdaptiveConnectionPoolOptions.cs"
      provides: "IOptions-bindable connection settings (HostName, Port, UserName, Password, VirtualHost, RequestedHeartbeat)"
      exports: ["AdaptiveConnectionPoolOptions"]
    - path: "src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs"
      provides: "3-mode probe (keyed → closure → IOptions) for IConnectionFactory"
    - path: "src/Oragon.AdaptivePool.RabbitMQ/Internals/AdapterDiagnosticsLog.cs"
      provides: "[LoggerMessage]-based source-gen log for the AutomaticRecoveryEnabled override warning"
    - path: "Directory.Packages.props"
      provides: "Pinned RabbitMQ.Client 7.2.1"
      contains: "RabbitMQ.Client"
  key_links:
    - from: "AdaptiveConnectionPoolServiceCollectionExtensions.AddAdaptiveConnectionPool"
      to: "Oragon.AdaptivePool.Core ServiceCollectionExtensions.AddAdaptivePool<IConnection>"
      via: "delegated registration with builder.Factory/.BeforeUse/.Check/.Release"
      pattern: "AddAdaptivePool<IConnection>"
    - from: "Factory hook"
      to: "ConnectionFactoryResolver.Resolve"
      via: "IServiceProvider scoped resolution at first acquire"
      pattern: "ResolveConnectionFactory|ConnectionFactoryResolver"
    - from: "Factory hook"
      to: "ConnectionFactory.CreateConnectionAsync(ct)"
      via: "RabbitMQ.Client v7 async API"
      pattern: "CreateConnectionAsync"
---

<objective>
Establish the `Oragon.AdaptivePool.RabbitMQ` adapter project and ship the `AddAdaptiveConnectionPool(name, configureFactory, configurePool)` DI extension that produces a working `IAdaptivePool<IConnection>` over Core's hooks. The connection pool is the foundation Plan 02 (channel pool) layers on top of.

Purpose: Cover RMQ-01 (connection pool extension with IsOpen hooks + AutomaticRecoveryEnabled=false) and bootstrap RMQ-04 (DI/builder conventions matching the sister `Oragon.RabbitMQ` library). Surface any Core API gap NOW (before Plan 02 layers on it and Phase 4 publishes) per success criterion #5.

Output:
- New project `src/Oragon.AdaptivePool.RabbitMQ/` (sln entry, csproj, PublicAPI files).
- `RabbitMQ.Client` 7.2.1 pinned in Directory.Packages.props.
- Public surface: `AdaptiveConnectionPoolBuilder`, `AdaptiveConnectionPoolOptions`, `AddAdaptiveConnectionPool`.
- Internal: 3-mode connection-factory resolver, source-gen diagnostics logger.
</objective>

<execution_context>
@/mnt/p/dynamic-pool/.claude/get-shit-done/workflows/execute-plan.md
@/mnt/p/dynamic-pool/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/research/STACK.md
@.planning/research/PITFALLS.md
@.planning/phases/03-rabbitmq-adapter/03-CONTEXT.md
@.planning/phases/03-rabbitmq-adapter/03-RESEARCH.md
@.planning/phases/02-elasticity-health/03-SUMMARY.md
@src/Oragon.AdaptivePool.Core/Abstractions/IAdaptivePool.cs
@src/Oragon.AdaptivePool.Core/Abstractions/IPoolItem.cs
@src/Oragon.AdaptivePool.Core/Abstractions/PoolState.cs
@src/Oragon.AdaptivePool.Core/Hooks/HookDelegates.cs
@src/Oragon.AdaptivePool.Core/Builder/AdaptivePoolBuilder.cs
@src/Oragon.AdaptivePool.Core/Builder/AdaptiveObjectPoolFactory.cs
@src/Oragon.AdaptivePool.Core/DependencyInjection/ServiceCollectionExtensions.cs
@src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj
@Directory.Build.props
@Directory.Packages.props
@Oragon.AdaptivePool.sln

<interfaces>
<!-- Carry-forward facts. The .Value vs .Object trap is real — ARCHITECTURE.md is stale; Core uses .Value. -->

From Core (Phase 1+2, verified live in this plan's source reads):
```csharp
public interface IPoolItem<out T> : IDisposable, IAsyncDisposable
{
    T Value { get; }   // NOT .Object — ARCHITECTURE.md sample is stale
}

public interface IAdaptivePool<T> : IDisposable, IAsyncDisposable where T : notnull
{
    int MaxSize { get; }
    int MinSize { get; }
    int Available { get; }
    int InUse { get; }
    IPoolItem<T> Acquire();
    ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default);
    Task ReadyAsync();
}

public enum PoolState { Healthy, Unhealthy }

public delegate ValueTask<T>          FactoryDelegate<T>(IServiceProvider services, CancellationToken cancellationToken);
public delegate ValueTask<PoolState>  BeforeUseDelegate<T>(T item, CancellationToken cancellationToken);
public delegate ValueTask<PoolState>  CheckDelegate<T>(T item, CancellationToken cancellationToken);
public delegate ValueTask<PoolState>  AfterUseDelegate<T>(T item, CancellationToken cancellationToken);
public delegate ValueTask             ReleaseDelegate<T>(T item, CancellationToken cancellationToken);

// Builder fluent surface that THIS plan composes:
public sealed class AdaptivePoolBuilder<T> where T : notnull
{
    public AdaptivePoolBuilder<T> Factory(FactoryDelegate<T> factory);
    public AdaptivePoolBuilder<T> BeforeUse(BeforeUseDelegate<T> hook);
    public AdaptivePoolBuilder<T> Check(CheckDelegate<T> hook);
    public AdaptivePoolBuilder<T> Release(ReleaseDelegate<T> hook);
    public AdaptivePoolBuilder<T> WithBounds(int minSize, int maxSize, int initialSize);
    public AdaptivePoolBuilder<T> IdleTimeout(TimeSpan t);
    // (also: WhenExhausted, GrowOnWaiterCount, GrowOnUtilizationPercent, GrowOnWaitTimeP95,
    //  ShrinkCooldownWindows, WithFailurePolicy, WithTimeProvider — exposed but not required here)
}

// Core DI registration the adapter delegates to:
public static IServiceCollection AddAdaptivePool<T>(
    this IServiceCollection services,
    string name,
    Action<AdaptivePoolBuilder<T>> configure)
    where T : notnull;
```

From RabbitMQ.Client 7.2.1 (verified per RESEARCH.md and rabbitmq.github.io API docs):
```csharp
public interface IConnectionFactory
{
    Task<IConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
    // (other overloads exist; we only need the no-endpoint overload)
}

public class ConnectionFactory : IConnectionFactory
{
    public string HostName { get; set; }
    public int Port { get; set; }
    public string UserName { get; set; }
    public string Password { get; set; }
    public string VirtualHost { get; set; }
    public Uri Uri { get; set; }
    public TimeSpan RequestedHeartbeat { get; set; }       // default 60s
    public bool AutomaticRecoveryEnabled { get; set; }     // default TRUE — adapter MUST flip to FALSE
    public static string DefaultUser { get; }              // "guest"
    public static string DefaultPass { get; }              // "guest"
    public static string DefaultVHost { get; }             // "/"
    Task<IConnection> CreateConnectionAsync(CancellationToken ct = default);
}

public interface IConnection : IAsyncDisposable, IDisposable
{
    bool IsOpen { get; }
    Task CloseAsync(CancellationToken cancellationToken = default);
    // ... (other members not needed in this plan)
}
```
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Pin RabbitMQ.Client, scaffold project, register in solution</name>
  <files>
    Directory.Packages.props,
    Oragon.AdaptivePool.sln,
    src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj,
    src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt,
    src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt
  </files>
  <action>
    1. Edit `Directory.Packages.props`: add `<PackageVersion Include="RabbitMQ.Client" Version="7.2.1" />` to the existing `<ItemGroup>`. Add a comment marking this as a Phase 3 addition. Do not touch existing pins.

    2. Create `src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj` modeled on `src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj`:
       - `<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>`
       - `<IsPackable>true</IsPackable>`
       - `<PackageId>Oragon.AdaptivePool.RabbitMQ</PackageId>`
       - `<Description>RabbitMQ.Client v7+ IConnection/IChannel adapter for Oragon.AdaptivePool — layered, lifecycle-managed pools with built-in observability.</Description>`
       - `<PackageTags>pool;objectpool;adaptive;elastic;rabbitmq;async;observability;opentelemetry</PackageTags>`
       - `<PackageLicenseExpression>MIT</PackageLicenseExpression>`
       - PackageReferences: `RabbitMQ.Client`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.CodeAnalysis.PublicApiAnalyzers` (PrivateAssets="all"), `MinVer` (PrivateAssets="all"), `Microsoft.SourceLink.GitHub` (PrivateAssets="all").
       - `ProjectReference` to `..\Oragon.AdaptivePool.Core\Oragon.AdaptivePool.Core.csproj`.
       - `<AdditionalFiles Include="PublicAPI.Shipped.txt" />` and `<AdditionalFiles Include="PublicAPI.Unshipped.txt" />`.
       - `<InternalsVisibleTo Include="Oragon.AdaptivePool.RabbitMQ.Tests" />` and `<InternalsVisibleTo Include="Oragon.AdaptivePool.RabbitMQ.IntegrationTests" />` (Plan 03 needs them; declare ahead).

    3. Create empty `src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt` and `src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt` (literally empty — analyzer fills as types are added).

    4. Add the new project to `Oragon.AdaptivePool.sln` under the existing `src` solution folder (`{827E0CD3-B72D-47B6-A68D-7590B98EB39B}`). Generate a new GUID for the project. Replicate the Debug/Release × Any CPU/x64/x86 configuration block already present for `Oragon.AdaptivePool.Core`.

    5. Verify: `dotnet restore Oragon.AdaptivePool.sln` succeeds and `dotnet build Oragon.AdaptivePool.sln -c Release` exits 0 (RabbitMQ project will be empty of code at this point — that's fine; build should still succeed).
  </action>
  <verify>
    <automated>
      cd /mnt/p/dynamic-pool && \
      dotnet restore Oragon.AdaptivePool.sln && \
      dotnet build Oragon.AdaptivePool.sln -c Release && \
      grep -c '"Oragon.AdaptivePool.RabbitMQ"' Oragon.AdaptivePool.sln && \
      grep -c '<PackageVersion Include="RabbitMQ.Client" Version="7.2.1"' Directory.Packages.props
    </automated>
  </verify>
  <done>
    - Both grep counts ≥ 1 (sln entry, packages pin present).
    - `dotnet build` produces 5 projects: Core, Core.Tests, Core.Stress, Core.Benchmarks, RabbitMQ. Zero errors. Carry-forward SourceLink "no remote" warnings allowed (same as Phase 1+2).
    - `bin/Release/net10.0/Oragon.AdaptivePool.RabbitMQ.dll` exists (even though it has no code yet, the project compiles).
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 2: Implement Options + Builder + 3-mode ConnectionFactoryResolver + diagnostics log (no DI extension yet)</name>
  <files>
    src/Oragon.AdaptivePool.RabbitMQ/Options/AdaptiveConnectionPoolOptions.cs,
    src/Oragon.AdaptivePool.RabbitMQ/Builder/AdaptiveConnectionPoolBuilder.cs,
    src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs,
    src/Oragon.AdaptivePool.RabbitMQ/Internals/AdapterDiagnosticsLog.cs,
    src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt
  </files>
  <behavior>
    - Test 1 (Plan 03 will write): `AdaptiveConnectionPoolBuilder` with no overrides exposes default `MinSize=0, MaxSize=8, InitialSize=0, IdleTimeout=TimeSpan.FromSeconds(60)` (matches Core defaults but adapter restates them so the public DX is self-contained).
    - Test 2: `AdaptiveConnectionPoolOptions` is a POCO bindable from `IConfiguration` — properties: `HostName`, `Port` (default 5672), `UserName`, `Password`, `VirtualHost`, `RequestedHeartbeat` (TimeSpan?, nullable so omission means "use ConnectionFactory default 60s").
    - Test 3: `ConnectionFactoryResolver.Resolve(sp, name, configureFactory)` returns the keyed `IConnectionFactory` if registered (verified via NSubstitute service provider returning a substituted factory).
    - Test 4: When no keyed factory exists but a `configureFactory` closure is provided, Resolve returns a `new ConnectionFactory()` mutated by the closure.
    - Test 5: When neither keyed nor closure available but `IOptionsMonitor<AdaptiveConnectionPoolOptions>` returns options with HostName set, Resolve returns a populated `ConnectionFactory`.
    - Test 6: When all three modes fail, Resolve throws `InvalidOperationException` with the pool name in the message.
    - Test 7: `AdapterDiagnosticsLog.AutomaticRecoveryOverridden` is a [LoggerMessage] partial with EventId=2001, LogLevel.Warning, message containing "AutomaticRecoveryEnabled" and the pool name as a structured property.
  </behavior>
  <action>
    1. Create `Options/AdaptiveConnectionPoolOptions.cs` — public sealed class:
       ```csharp
       public sealed class AdaptiveConnectionPoolOptions
       {
           public string? HostName { get; set; }
           public int Port { get; set; } = 5672;
           public string? UserName { get; set; }
           public string? Password { get; set; }
           public string? VirtualHost { get; set; }
           public TimeSpan? RequestedHeartbeat { get; set; }
       }
       ```

    2. Create `Builder/AdaptiveConnectionPoolBuilder.cs` — public sealed class with fluent setters mirroring the Core builder shape (per D-DI / RMQ-04 — sister-library convention). Properties exposed via fluent methods (returning `this`):
       ```csharp
       public sealed class AdaptiveConnectionPoolBuilder
       {
           public int MinSize { get; private set; } = 0;
           public int MaxSize { get; private set; } = 8;
           public int InitialSize { get; private set; } = 0;
           public TimeSpan IdleTimeout { get; private set; } = TimeSpan.FromSeconds(60);
           public AdaptiveConnectionPoolBuilder WithBounds(int min, int max, int initial) { /* validate 0 <= min <= initial <= max */ MinSize=min; MaxSize=max; InitialSize=initial; return this; }
           public AdaptiveConnectionPoolBuilder WithIdleTimeout(TimeSpan t) { /* validate > 0 */ IdleTimeout=t; return this; }
       }
       ```
       Validation messages must match Core's wording style. Do NOT re-export every Core knob (GrowOn*, ShrinkCooldownWindows) yet — defer to a future plan if needed; document with XML comments that those Core knobs can be reached via the consumer's own Core builder configuration if needed (this is NOT in scope for Phase 3 v1; keep the surface small).

    3. Create `Internals/ConnectionFactoryResolver.cs` — internal static class with the canonical 3-mode probe per RESEARCH Pattern 1. Signature:
       ```csharp
       internal static IConnectionFactory Resolve(
           IServiceProvider sp,
           string name,
           Action<ConnectionFactory>? configureFactory);
       ```
       Probe order:
       1. `sp.GetKeyedService<IConnectionFactory>(name)` — return if non-null.
       2. If `configureFactory` is not null, instantiate `new ConnectionFactory()`, invoke the closure, return it.
       3. `sp.GetService<IOptionsMonitor<AdaptiveConnectionPoolOptions>>()?.Get(name)` — if non-null and `HostName` is non-empty, build a `ConnectionFactory` with the bound values (use `ConnectionFactory.DefaultUser`/`DefaultPass`/`DefaultVHost` when null; map `Port` directly; map `RequestedHeartbeat` only when non-null).
       4. Throw `InvalidOperationException($"AddAdaptiveConnectionPool: no IConnectionFactory found for pool '{name}'. Provide one via keyed singleton, configureFactory closure, or IOptions binding.")`.

       Add a sibling helper `internal static void ForceAutomaticRecoveryDisabled(IConnectionFactory factory, ILogger logger, string poolName)`:
       - If `factory is ConnectionFactory cf && cf.AutomaticRecoveryEnabled`: call the [LoggerMessage] warning helper, then `cf.AutomaticRecoveryEnabled = false`.
       - Else: no-op.

    4. Create `Internals/AdapterDiagnosticsLog.cs` — `internal static partial class` using `[LoggerMessage]` source-gen for allocation-free logging (consistent with Phase 2 SUMMARY pattern):
       ```csharp
       internal static partial class AdapterDiagnosticsLog
       {
           [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
               Message = "AutomaticRecoveryEnabled was true on the configured ConnectionFactory for pool '{PoolName}'; Oragon.AdaptivePool overrides this to false (the pool owns lifecycle).")]
           public static partial void AutomaticRecoveryOverridden(this ILogger logger, string poolName);
       }
       ```
       Use EventId 2001+ to avoid collision with Core's 1xxx range.

    5. Update `PublicAPI.Unshipped.txt` to declare the new public types per PublicApiAnalyzers convention (each public member on its own line, format: `Oragon.AdaptivePool.RabbitMQ.Builder.AdaptiveConnectionPoolBuilder`, etc.). The analyzer will fail the build if any public symbol is missing — copy any analyzer-suggested content verbatim if it diff-fails.

    6. Verify: `dotnet build` exits 0; PublicApiAnalyzers does not flag undeclared public members.
  </action>
  <verify>
    <automated>
      cd /mnt/p/dynamic-pool && \
      dotnet build src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj -c Release && \
      grep -c "AdaptiveConnectionPoolBuilder" src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt && \
      grep -c "AdaptiveConnectionPoolOptions" src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt && \
      grep -v '^#' src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs | grep -c "GetKeyedService<IConnectionFactory>"
    </automated>
  </verify>
  <done>
    - Build clean on net8/9/10. Zero `RS0016`/`RS0017` (PublicApiAnalyzers diagnostics).
    - All 7 [LoggerMessage] partial methods (just one in this plan, but the pattern is in place) generate to allocation-free dispatchers.
    - The 3-mode probe is verified by direct grep on the source: `GetKeyedService<IConnectionFactory>` appears.
    - `ForceAutomaticRecoveryDisabled` mutates `AutomaticRecoveryEnabled` only if true — a code grep `grep -n "AutomaticRecoveryEnabled = false" src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs` returns one match.
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 3: Wire AddAdaptiveConnectionPool extension method delegating to Core's AddAdaptivePool&lt;IConnection&gt;</name>
  <files>
    src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs,
    src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt
  </files>
  <behavior>
    - Test 1 (covered in Plan 03 Task 1 unit tests): `services.AddAdaptiveConnectionPool("default", f => f.HostName="localhost", b => b.WithBounds(0, 4, 0))` followed by `BuildServiceProvider()` and `GetRequiredKeyedService<IAdaptivePool<IConnection>>("default")` returns a non-null pool.
    - Test 2: With `name == string.Empty`, the pool is also resolvable as the non-keyed `IAdaptivePool<IConnection>` (Core's fallback registration).
    - Test 3: Calling the extension twice with the same name registers exactly one keyed singleton (delegates to Core's `TryAddKeyedSingleton`).
    - Test 4: When the consumer set `cf.AutomaticRecoveryEnabled = true` inside the closure, the FIRST `AcquireAsync` (with a NSubstitute `IConnectionFactory` returning a substituted IConnection) emits a Warning log entry with EventId=2001 and the configured pool name. (Behavior must occur AT FACTORY TIME — first acquire — not at registration; the closure runs once per Factory invocation.)
    - Test 5: BeforeUse hook returns Healthy when `IConnection.IsOpen=true` and Unhealthy when false (NSubstitute toggles the property between calls).
    - Test 6: Release hook calls `IConnection.CloseAsync(...)` followed by `IConnection.DisposeAsync()` even when CloseAsync throws (try/catch swallows).
  </behavior>
  <action>
    1. Create `DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs` — public static class. Single public method:
       ```csharp
       public static IServiceCollection AddAdaptiveConnectionPool(
           this IServiceCollection services,
           string name,
           Action<ConnectionFactory>? configureFactory,
           Action<AdaptiveConnectionPoolBuilder> configurePool)
       ```
       Implementation:
       a. `ArgumentNullException.ThrowIfNull` on `services`, `name`, `configurePool`. (`configureFactory` IS allowed to be null — keyed/IOptions paths cover that case.)
       b. Build the adapter-side `AdaptiveConnectionPoolBuilder` once at registration: `var poolBuilder = new AdaptiveConnectionPoolBuilder(); configurePool(poolBuilder);` — capture its bounds/idle for use inside the Core `AddAdaptivePool<IConnection>` configure delegate.
       c. Delegate to Core: `services.AddAdaptivePool<IConnection>(name, builder => { ... })` configuring:
          - `.Factory(async (sp, ct) => { var factory = ConnectionFactoryResolver.Resolve(sp, name, configureFactory); var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Oragon.AdaptivePool.RabbitMQ") ?? NullLogger.Instance; ConnectionFactoryResolver.ForceAutomaticRecoveryDisabled(factory, logger, name); return await factory.CreateConnectionAsync(ct).ConfigureAwait(false); })`
          - `.BeforeUse((conn, _) => ValueTask.FromResult(conn.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))`
          - `.Check((conn, _) => ValueTask.FromResult(conn.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))`
          - `.Release(async (conn, ct) => { try { await conn.CloseAsync(ct).ConfigureAwait(false); } catch { /* Pitfall A: swallow close errors */ } await conn.DisposeAsync().ConfigureAwait(false); })`
          - `.WithBounds(poolBuilder.MinSize, poolBuilder.MaxSize, poolBuilder.InitialSize)`
          - `.IdleTimeout(poolBuilder.IdleTimeout)`
       d. Return `services`.

    2. Add XML doc on the method:
       - Mention RMQ-01 conventions (sister-library alignment).
       - Document that `AutomaticRecoveryEnabled` is forced to `false` per RESEARCH Pitfall 9 (link the constant: "see RESEARCH.md Pitfall A").
       - Document the 3-mode probe order (keyed → closure → IOptions).

    3. Update `PublicAPI.Unshipped.txt` for the new public extension method per PublicApiAnalyzers convention.

    4. Verify: `dotnet build` exits 0; do a smoke `dotnet build` of the whole solution to catch any cross-project break.
  </action>
  <verify>
    <automated>
      cd /mnt/p/dynamic-pool && \
      dotnet build Oragon.AdaptivePool.sln -c Release && \
      grep -v '^#' src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs | grep -c "AddAdaptivePool<IConnection>" && \
      grep -v '^#' src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs | grep -c "ForceAutomaticRecoveryDisabled" && \
      grep -c "AddAdaptiveConnectionPool" src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt
    </automated>
  </verify>
  <done>
    - Build clean across all 3 TFMs.
    - PublicAPI.Unshipped.txt lists the extension method.
    - `AddAdaptivePool<IConnection>` delegation present (the adapter does NOT re-implement pool engine).
    - `ForceAutomaticRecoveryDisabled` is called from inside the Factory delegate (so override happens before EVERY connection creation, not just at registration — covers the case where IConnectionFactory is keyed-singleton and the consumer mutates it later).
    - No regression in Core/Core.Tests/Core.Stress/Core.Benchmarks builds.
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Consumer DI registration → adapter | Untrusted `configureFactory` closure runs in adapter context; could throw, mutate factory in unexpected ways |
| Adapter → RabbitMQ broker | TCP boundary; broker can be unreachable, reject auth, or close mid-connection |
| Adapter → Core pool | Internal — Core is trusted (same author, this codebase) |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-03-01 | Tampering | `configureFactory` closure mutating `AutomaticRecoveryEnabled` to true post-registration | mitigate | `ForceAutomaticRecoveryDisabled` runs INSIDE the Factory delegate (per acquire), not just at registration — guarantees override even if a keyed singleton is mutated by another component |
| T-03-02 | Information Disclosure | `AdapterDiagnosticsLog.AutomaticRecoveryOverridden` log includes the pool name (potentially user-defined string) | accept | Pool name is a developer-defined string, not user data; Core already logs it under `pool.name` tag |
| T-03-03 | Information Disclosure | RabbitMQ credentials (`Password`) on `AdaptiveConnectionPoolOptions` | mitigate | Document in XML comment that `Password` should be supplied via secret stores (User Secrets / Key Vault / env), never hardcoded; do NOT include `Password` value in any log statement |
| T-03-04 | Denial of Service | `ConnectionFactory.CreateConnectionAsync` blocking indefinitely if broker unreachable | mitigate | Hook signature already accepts `CancellationToken`; pool's own `AcquireAsync` propagates the consumer's CT all the way down |
| T-03-05 | Spoofing | Untrusted keyed `IConnectionFactory` registered by another DI extension overriding the consumer's intent | accept | Standard DI semantics — last-registered keyed singleton wins; documented in XML doc as expected probe order |
| T-03-06 | Repudiation | Connection lifecycle decisions (factory failure, override warning) not traceable | mitigate | Source-gen `[LoggerMessage]` entries with EventId so consumers can filter; Core's existing `pool.factory.failures` counter increments on `Factory` throw |
</threat_model>

<verification>
- `dotnet restore` / `dotnet build Oragon.AdaptivePool.sln -c Release` exit 0 across net8/9/10.
- Solution contains 5 projects (Core, Core.Tests, Core.Stress, Core.Benchmarks, RabbitMQ).
- `Directory.Packages.props` pins `RabbitMQ.Client 7.2.1`.
- `src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt` lists every public type/member; PublicApiAnalyzers RS0016/RS0017 do not fire.
- All Phase 1+2 tests still pass (no Core regression): run `dotnet test tests/Oragon.AdaptivePool.Core.Tests/Oragon.AdaptivePool.Core.Tests.csproj -c Release --no-build` after the build.
- IF a Core API gap surfaces (e.g., the `AddAdaptivePool<T>` signature is insufficient to wire the Factory delegate this way, or a hook can't accept the closure capture pattern): STOP and return PLANNING BLOCKED to the orchestrator with the specific gap. Per phase success criterion #5, Core gets refactored before continuing.
</verification>

<success_criteria>
1. Solution builds clean on net8.0/net9.0/net10.0; new RabbitMQ project produces a packable assembly.
2. `RabbitMQ.Client 7.2.1` is centrally pinned and consumed by the new project.
3. `services.AddAdaptiveConnectionPool(name, configureFactory, configurePool)` exists, public API surface declared in PublicAPI.Unshipped.txt.
4. The Factory delegate forces `AutomaticRecoveryEnabled = false` and emits EventId=2001 Warning on override.
5. BeforeUse/Check return Unhealthy when `IConnection.IsOpen = false`; Release calls CloseAsync then DisposeAsync, swallowing close exceptions.
6. Core test suite remains green (no regression).
</success_criteria>

<output>
After completion, create `.planning/phases/03-rabbitmq-adapter/03-01-SUMMARY.md` documenting:
- Files created with line counts.
- The 3-mode probe behavior verified in code (with grep evidence).
- Whether any Core API gap surfaced (if yes — STOP and report; per success criterion #5 the gap must be filed BEFORE Plan 02 runs).
- Confirmation that `bin/Release/net{8,9,10}.0/Oragon.AdaptivePool.RabbitMQ.dll` exist.
- Heads-up to Plan 02: the connection pool is wired but UNTESTED (mocks come in Plan 03 Task 1, integration in Plan 03 Task 2). Plan 02 may discover bugs; Plan 02 author should not be surprised if Task 1 of Plan 03 finds wiring issues.
</output>
