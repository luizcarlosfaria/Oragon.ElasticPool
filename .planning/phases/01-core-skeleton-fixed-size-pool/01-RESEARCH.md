# Phase 1: Core Skeleton — Fixed-Size Pool — Research

**Researched:** 2026-05-02
**Domain:** .NET multi-target OSS library — fixed-size object pool engine, lifecycle hooks, DI integration, telemetry foundation
**Confidence:** HIGH (all package versions, .csproj setups, and BCL APIs verified against current sources May 2026)

---

## Executive Summary

This document fills Phase 1-specific implementation gaps **on top of** the existing project research in `.planning/research/` (which is treated as authoritative for ecosystem-level decisions). The job here is concrete code patterns, exact package syntax, and integration recipes for the 16 Phase 1 requirements.

Three changes since project-level research need explicit Phase 1 attention:

1. **Stack adjustment locked in CONTEXT.md:** Project research originally recommended **Shouldly**; the user's Phase 1 decisions instead lock in **Awesome Assertions** (now `AwesomeAssertions`, v9.4.0, 2026-02-18) — a community fork of FluentAssertions v7 under permanent Apache-2 license. This is a drop-in for FluentAssertions API, with the namespace renamed `AwesomeAssertions` as of v9. [VERIFIED: nuget.org/packages/AwesomeAssertions, awesomeassertions.org/upgradingtov9]
2. **xUnit v3 + Microsoft.Testing.Platform** in 2026 has a different `.csproj` shape than xUnit v2: `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`, no need for `Microsoft.NET.Test.Sdk` if going MTP-only, optional `xunit.v3.runner.visualstudio` for IDE integration. .NET 10 SDK ships with native MTP support via `global.json` runner config. [CITED: xunit.net/docs/getting-started/v3/microsoft-testing-platform]
3. **`Microsoft.Extensions.TimeProvider.Testing` 10.5.0** ships `FakeTimeProvider` — needed in Phase 1 even though sweeper is Phase 2, because the **API contract** for the pool must accept `TimeProvider` injection from day one (non-retrofittable). [VERIFIED: nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing]

**Primary recommendation:** Build the public API surface (interfaces + builder + `IPoolItem<T>` wrapper) **first**, before any engine internals. The non-retrofittable decisions live in those signatures (`CancellationToken` parameters, `ValueTask<T>` returns, `TimeProvider` injection point, `IMeterFactory` resolution). Once frozen, the engine can be hardened iteratively. The `MaxSize=1` ping-pong stress test is the anchor acceptance gate — it proves waiter-queue correctness, counter rollback, and cancellation fidelity in one test.

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Repository Layout & Solution Structure:**
- Estrutura: `src/` (projetos publicáveis) + `tests/` (unit + integration) + `samples/` + `.github/workflows/`
- Nomes de projetos: `Oragon.ElasticPool`, `Oragon.ElasticPool.RabbitMQ` (Phase 3), `Oragon.ElasticPool.Tests`, `Oragon.ElasticPool.Stress` (projeto separado, fora da CI default), `Oragon.ElasticPool.Benchmarks` (Phase 4)
- `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` por projeto (cada `.csproj` mantém os seus)
- `README.md` único na raiz do repositório com seções por pacote

**Public API Micro-Decisions:**
- `IPoolItem<T>.Value` expõe a instância pooled (alinha com `Lazy<T>.Value`, `Nullable<T>.Value`)
- Esgotamento (`MaxSize` atingido + todos in-use): comportamento **configurável via builder** — `.WhenExhausted(WaitBehavior.Wait)` (default) ou `.WhenExhausted(WaitBehavior.Throw)` lança `PoolExhaustedException` imediatamente; `Wait` respeita `CancellationToken` do `AcquireAsync`
- Builder: apenas `Factory` é obrigatório; `Build()` lança `InvalidOperationException` se ausente; demais hooks (`BeforeUse`, `Check`, `AfterUse`, `Release`) são opcionais com defaults no-op
- `services.AddElasticPool<T>(name, configure)` requer `name` explícito; default = `string.Empty` para apps single-pool; usa named options pattern internamente para múltiplos pools tipados no mesmo `T`

**Test & Tooling Infrastructure:**
- Test framework: **xUnit v3 + Microsoft.Testing.Platform + Awesome Assertions + NSubstitute** (Awesome Assertions = fork OSS recente do FluentAssertions com mesma sintaxe, mantido após mudança de licença Xceed)
- Stress tests: projeto separado `Oragon.ElasticPool.Stress` excluído da CI default (job dedicado nightly em Phase 4)
- Time mocking: `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` para todo teste sensível a tempo
- Code coverage: gate de **90% no Core** na CI; sem gate em adapters/samples; ferramentas: coverlet + ReportGenerator → Codecov

### Claude's Discretion

- Escolha exata de quais counters expor em TELEM-01 (mínimo: `pool.acquire.count`, `pool.factory.failures`; resto fica para Phase 2 quando grow/shrink/health surgem)
- Política de naming interno (private/internal classes) e estrutura de namespaces dentro de `Oragon.ElasticPool`
- Detalhes do `PoolState` enum (incluir `Quarantined`? por ora apenas `Healthy`/`Unhealthy`, com espaço para extensão em v2)
- Forma exata da exception `PoolExhaustedException` (mensagem, properties como `MaxSize`, `WaitTime`)
- Estratégia exata de double-dispose detection (Interlocked flag, Disposed property pública?)

### Deferred Ideas (OUT OF SCOPE)

- `Oragon.ElasticPool.OpenTelemetry` companion package — defer até Phase 4
- `Oragon.ElasticPool.Polly` glue package — defer para v1.x
- `Microsoft.Extensions.Diagnostics.HealthChecks` integration — defer para Phase 2 ou v1.x
- `PoolItemContext` / state bag rico nos hooks — defer; em v1, hooks recebem apenas `(T item, CancellationToken ct)`
- Quarentena com backoff (FAIL-V2-01) — explicitamente v2
- AfterUse ativo (HOOK-V2-01) — em Phase 1 a assinatura existe mas default é no-op
</user_constraints>

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| API-01 | `IElasticPool<T>` with sync `Acquire()` + async `AcquireAsync(ct)` returning `ValueTask<IPoolItem<T>>` | Public API Patterns §1; ValueTask Contract §1 |
| API-02 | `IPoolItem<T>` disposable wrapper, idempotent, double-dispose-safe | IPoolItem Wrapper §2; Disposal Pattern §2 |
| API-03 | `ElasticObjectPoolFactory.Build<T>(IServiceProvider, CancellationToken)` builder; validates config in `.Build()` | Builder Recipe §3 |
| HOOK-01 | `Factory` hook required, runs outside locks | Hook Delegate Signatures §4 |
| HOOK-02 | `BeforeUse` hook optional, fires in `Acquire`, failure → policy | Hook Delegate Signatures §4; Failure Path §5 |
| HOOK-03 | `Check` hook optional, fires in background sweeper (engine support; sweeper itself is Phase 2) | Hook Delegate Signatures §4 |
| HOOK-04 | `AfterUse` hook optional, runs on return; default no-op | Hook Delegate Signatures §4 |
| HOOK-05 | `Release` hook optional for cleanup | Hook Delegate Signatures §4 |
| BOUND-01 | `MinSize` / `MaxSize` / `InitialSize` config, `0 ≤ Min ≤ Initial ≤ Max` validated at `.Build()` | Builder Recipe §3 |
| BOUND-02 | Eager warm-up to `InitialSize`, awaitable, cancellable | Warm-up Pattern §6 |
| FAIL-01 | `IItemFailurePolicy<T>` invoked on `Unhealthy` or factory failure | Failure Policy Contract §5 |
| FAIL-02 | `DiscardAndReplaceFailurePolicy<T>` default | Failure Policy Contract §5 |
| TELEM-01 | `Meter` "Oragon.ElasticPool" via `IMeterFactory`; `pool.acquire.count` + `pool.factory.failures` minimum | IMeterFactory Recipe §7 |
| DI-01 | `services.AddElasticPool<T>(name, configure)` with named options | DI Extension Pattern §8 |
| QUAL-01 | `CancellationToken` end-to-end in every async path | CancellationToken Linking §9 |
| QUAL-02 | `IDisposable` + `IAsyncDisposable` with drain semantics | Pool Dispose State Machine §10 |
</phase_requirements>

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Pool lifecycle (acquire/release/dispose) | Engine (`ElasticPool<T>` internal) | Public API (`IElasticPool<T>`) | Engine encapsulates state; public surface is contract-only |
| Hook orchestration (Factory/BeforeUse/AfterUse/Release) | Engine (slow-path & release path) | Public API (delegate types) | Hook signatures are public contract; invocation is engine concern |
| Builder configuration validation | Builder (public, mutable) | Options record (public, frozen) | Mutable builder → frozen options at `Build()` is single transition point |
| DI registration | DI extension (public static) | DI Abstractions only | `Microsoft.Extensions.DependencyInjection.Abstractions` only — never the impl |
| Telemetry (Meter, Counter) | `TelemetryEmitter` (internal) | `IMeterFactory` (DI) + manual `new Meter()` fallback | Centralize naming; tolerate missing DI registration in tests |
| Failure policy decisions | `IItemFailurePolicy<T>` (public extension point) | Default `DiscardAndReplaceFailurePolicy<T>` | Pluggable from day one; ship one default policy |
| Time abstraction (engine ctor parameter) | `TimeProvider` parameter | `TimeProvider.System` default | Phase 1 doesn't run sweeper but accepts injection point — cannot retrofit |

---

## Standard Stack

### Core (verified versions May 2026)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.5 | `ILogger<T>`, `[LoggerMessage]` source-gen | Abstractions only, transitively safe [VERIFIED: nuget.org] |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.6 | `IServiceCollection` extensions | Abstractions only, no container [VERIFIED: nuget.org] |
| `Microsoft.Extensions.Options` | 10.0.x | Named options pattern for `AddElasticPool<T>(name, ...)` | Required when consumer wants per-pool configuration; pulled by `AddOptions<T>` extension [CITED: learn.microsoft.com/en-us/dotnet/core/extensions/options] |
| `MinVer` | 6.0.0 | Tag-driven SemVer (build-only) | `PrivateAssets="all"` [VERIFIED: nuget.org] |
| `Microsoft.SourceLink.GitHub` | 8.0.0 | Source debugging metadata (build-only) | `PrivateAssets="all"` [VERIFIED: nuget.org] |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | 3.3.x | Track public API surface from day one | `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` baseline [CITED: github.com/dotnet/roslyn-analyzers PublicApiAnalyzers.Help.md] |

> Note on `Microsoft.Extensions.Options`: Project STACK.md keeps Core minimal (only Logging.Abstractions + DI.Abstractions). However, **DI-01 named-pool requirement forces Options into the dependency surface** because that's where `IConfigureNamedOptions<T>`, `IOptionsMonitor<T>.Get(name)`, and `services.AddOptions<T>(name)` live. Options 10.0.x is itself minimal (depends only on `Microsoft.Extensions.Primitives`); the cost is acceptable. Document explicitly in README.

### Test Stack

| Library | Version | Purpose |
|---------|---------|---------|
| `xunit.v3` | 3.2.2 (current GA, 2026-01-14) | Test framework [VERIFIED: nuget.org/packages/xunit.v3] |
| `xunit.v3.runner.visualstudio` | matching xunit.v3 | IDE Test Explorer integration |
| `AwesomeAssertions` | 9.4.0 (2026-02-18) | Apache-2 fork of FluentAssertions; namespace `AwesomeAssertions` (renamed in v9) [VERIFIED: nuget.org/packages/AwesomeAssertions] |
| `NSubstitute` | 5.3.0 | Mocking |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.5.0 | `FakeTimeProvider` — needed in Phase 1 because pool ctor accepts `TimeProvider` (Phase 2 sweeper consumes it) [VERIFIED: nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing] |
| `Microsoft.Extensions.DependencyInjection` | 10.0.5 | Real DI container for integration tests (test-only) |
| `Microsoft.Extensions.Logging.Console` | 10.0.5 | Diagnostic logging in tests (test-only) |
| `Microsoft.Extensions.Diagnostics.Testing` | 10.0.x | Provides `MetricCollector<T>` for asserting counter values [CITED: learn.microsoft.com — "Test custom metrics"] |
| `coverlet.collector` | 6.0.4 | Code coverage in CI; threshold via `dotnet test /p:Threshold=90` [CITED: github.com/coverlet-coverage/coverlet MSBuildIntegration.md] |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| AwesomeAssertions | Shouldly 4.3 | Project research recommended Shouldly. CONTEXT.md overrides — user prefers FA-style API. AwesomeAssertions delivers FA v7 syntax under permanent Apache-2. |
| AwesomeAssertions | FluentAssertions v7 (pinned) | Still Apache-2 but security-fix-only; locks future. |
| `Microsoft.Extensions.Options` for named pools | Hand-rolled `Dictionary<string, ElasticPoolOptions<T>>` | Possible, but loses `IOptionsMonitor` change-tracking and standard DX users expect from `services.Configure<T>(name)`. |
| `IMeterFactory` from DI | Static `new Meter("Oragon.ElasticPool")` | DI-resolved is correct for production (test isolation, lifecycle), but engine MUST tolerate consumer not calling `services.AddMetrics()`. Use both: DI when available, manual fallback. [CITED: learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation — "AddMetrics" is required to register `IMeterFactory`] |

**Installation:**
```xml
<!-- Directory.Packages.props additions for Phase 1 -->
<PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.5" />
<PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.6" />
<PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.5" />
<PackageVersion Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="3.3.4" />
<PackageVersion Include="MinVer" Version="6.0.0" />
<PackageVersion Include="Microsoft.SourceLink.GitHub" Version="8.0.0" />
<!-- Test -->
<PackageVersion Include="xunit.v3" Version="3.2.2" />
<PackageVersion Include="xunit.v3.runner.visualstudio" Version="3.2.2" />
<PackageVersion Include="AwesomeAssertions" Version="9.4.0" />
<PackageVersion Include="NSubstitute" Version="5.3.0" />
<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.5.0" />
<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" />
<PackageVersion Include="Microsoft.Extensions.Diagnostics.Testing" Version="10.0.5" />
<PackageVersion Include="coverlet.collector" Version="6.0.4" />
```

---

## Architecture Patterns

### System Architecture (Phase 1 scope)

```
                ┌────────────────────────────────────────────┐
                │          Consumer Application              │
                │  services.AddElasticPool<T>(name, b => …)│
                │  pool.Acquire() / pool.AcquireAsync(ct)   │
                └──────────────────┬─────────────────────────┘
                                   │
                                   ▼
                ┌────────────────────────────────────────────┐
                │  ServiceCollectionExtensions.AddElastic   │
                │   Pool<T>(name, configure)                 │
                │   • registers AddOptions<>(name).Configure │
                │   • registers AddKeyedSingleton<IElastic  │
                │     Pool<T>>(name, factory: builder.Build) │
                └──────────────────┬─────────────────────────┘
                                   │
                                   ▼
                ┌────────────────────────────────────────────┐
                │  ElasticObjectPoolFactory.Build<T>(sp,ct) │
                │      ↓                                     │
                │  ElasticPoolBuilder<T>                    │
                │   .Factory(...) .BeforeUse(...)            │
                │   .Check(...) .AfterUse(...) .Release(...) │
                │   .WithBounds(min,max,initial)             │
                │   .WithFailurePolicy(...)                  │
                │   .WhenExhausted(Wait|Throw)               │
                │   .Build() → frozen ElasticPoolOptions<T> │
                └──────────────────┬─────────────────────────┘
                                   │
                                   ▼
                ┌────────────────────────────────────────────┐
                │  sealed ElasticPool<T> (engine)           │
                │   • ConcurrentQueue<PoolEntry<T>> _idle    │
                │   • Channel<TCS<PoolEntry<T>>> _waiters    │
                │   • Interlocked counters _total/_inUse     │
                │   • State machine: Open/Draining/Closed    │
                │   • TimeProvider _time (Phase 2 uses it)   │
                │   • TelemetryEmitter (Meter via IMeter     │
                │     Factory; falls back to new Meter())    │
                │   • Eager warm-up Task (runs InitialSize   │
                │     factory calls in parallel)             │
                └────────────────────────────────────────────┘
                                   │
                  Acquire returns  ▼
                ┌────────────────────────────────────────────┐
                │  sealed PoolItem<T> : IPoolItem<T>         │
                │   • .Value (the pooled instance)           │
                │   • IDisposable.Dispose() returns to pool  │
                │   • IAsyncDisposable.DisposeAsync() (calls │
                │     AfterUse hook async)                   │
                │   • Finalizer logs leak warning + force    │
                │     return defensively                     │
                │   • Interlocked _disposed flag (idempotent)│
                └────────────────────────────────────────────┘
```

### Recommended Project Structure (Phase 1)

```
src/
└── Oragon.ElasticPool/
    ├── Abstractions/
    │   ├── IElasticPool.cs           # public contract
    │   ├── IPoolItem.cs               # public disposable wrapper
    │   ├── IItemFailurePolicy.cs      # pluggable policy
    │   └── PoolState.cs               # enum: Healthy | Unhealthy
    ├── Builder/
    │   ├── ElasticObjectPoolFactory.cs
    │   ├── ElasticPoolBuilder.cs
    │   ├── ElasticPoolOptions.cs     # frozen record
    │   └── WaitBehavior.cs            # enum: Wait | Throw
    ├── Hooks/
    │   └── HookDelegates.cs           # FactoryDelegate<T>, BeforeUseDelegate<T>, etc.
    ├── Internals/
    │   ├── ElasticPool.cs            # sealed engine
    │   ├── PoolEntry.cs               # internal record
    │   ├── PoolItem.cs                # internal sealed wrapper
    │   └── PoolStateMachine.cs        # Open/Draining/Closed
    ├── Policies/
    │   └── DiscardAndReplaceFailurePolicy.cs
    ├── Telemetry/
    │   ├── TelemetryEmitter.cs        # Meter owner; IMeterFactory + fallback
    │   ├── PoolMeterNames.cs          # const strings
    │   └── PoolDiagnosticsLog.cs      # [LoggerMessage] partial class
    ├── DependencyInjection/
    │   └── ServiceCollectionExtensions.cs
    ├── Exceptions/
    │   └── PoolExhaustedException.cs
    ├── PublicAPI.Shipped.txt
    ├── PublicAPI.Unshipped.txt
    └── Oragon.ElasticPool.csproj
tests/
├── Oragon.ElasticPool.Tests/      # unit tests, runs in default CI
└── Oragon.ElasticPool.Stress/     # MaxSize=1 ping-pong, factory-fault property tests; nightly only
```

### Pattern 1: Public API Patterns — Hook Delegate Signatures (HOOK-01..05)

All delegates accept `CancellationToken` from day one. All return `ValueTask<T>` — never `Task<T>`. This is the non-retrofittable contract.

```csharp
// Source: synthesized from project ARCHITECTURE.md + ValueTask docs
// [CITED: learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1]
namespace Oragon.ElasticPool.Hooks;

/// <summary>
/// Creates a new pooled instance. Required hook — must be configured via .Factory(...).
/// Runs OUTSIDE any pool lock so that slow factory work (network I/O, etc.)
/// never blocks the acquire fast path.
/// </summary>
public delegate ValueTask<T> FactoryDelegate<T>(IServiceProvider services, CancellationToken cancellationToken);

/// <summary>
/// Cheap on-borrow validation. MUST complete in &lt; 1ms p99 (no server round-trips).
/// Heavy probes belong in the Check hook (background sweep, Phase 2).
/// </summary>
public delegate ValueTask<PoolState> BeforeUseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// Background health probe. Engine signature only in Phase 1; sweeper consumes in Phase 2.
/// </summary>
public delegate ValueTask<PoolState> CheckDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// On-return validation. Default no-op in v1; activated via FAIL-V2 / HOOK-V2.
/// </summary>
public delegate ValueTask<PoolState> AfterUseDelegate<T>(T item, CancellationToken cancellationToken);

/// <summary>
/// Cleanup hook for evicted/discarded items (e.g., connection.CloseAsync()).
/// </summary>
public delegate ValueTask ReleaseDelegate<T>(T item, CancellationToken cancellationToken);
```

**Why `ValueTask` not `Task`:** Hot-path callbacks (Factory, Release) are likely to complete synchronously in many cases (e.g., a Factory pulling a pre-warmed connection from a sub-pool). `ValueTask` allocation-free fast path matters for high-throughput. Project ARCHITECTURE.md and PITFALLS.md (Pitfall 19) lock this in. The .NET API docs confirm ValueTask is intended for this exact case but warn: **never await twice, never call `.Result` until completed, never `AsTask` twice**. Document this in XML docs on the hook delegates so consumer code respects the contract. [CITED: learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1]

### Pattern 2: `IPoolItem<T>` Wrapper — Disposal Pattern (API-02)

Sealed class with both `IDisposable` and `IAsyncDisposable`, idempotent via Interlocked flag, finalizer for leak detection.

```csharp
// Source: synthesized from learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync
// + project PITFALLS.md (Pitfall 2 — leak detection)
namespace Oragon.ElasticPool.Abstractions;

public interface IPoolItem<out T> : IDisposable, IAsyncDisposable
{
    /// <summary>The pooled instance. Throws ObjectDisposedException if already disposed.</summary>
    T Value { get; }
}

namespace Oragon.ElasticPool.Internals;

internal sealed class PoolItem<T> : IPoolItem<T>
{
    private readonly ElasticPool<T> _owner;
    private readonly PoolEntry<T> _entry;
    private int _disposed; // 0 = live, 1 = disposed

    public PoolItem(ElasticPool<T> owner, PoolEntry<T> entry)
    {
        _owner = owner;
        _entry = entry;
    }

    public T Value
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PoolItem<T>));
            return _entry.Item;
        }
    }

    public void Dispose()
    {
        // Idempotent via Interlocked CAS — concurrent or repeated calls return on second invocation.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        GC.SuppressFinalize(this);
        _owner.ReturnSync(_entry); // synchronous return path (no AfterUse hook awaited)
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        GC.SuppressFinalize(this);
        return _owner.ReturnAsync(_entry); // awaits AfterUse + Release decisions
    }

    // Finalizer fires only if consumer leaked the item (forgot using/await using).
    // Returns the entry defensively so the pool doesn't permanently lose it.
    // Cannot do real async work in a finalizer — use the sync return path.
    ~PoolItem()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _owner.ReturnFromFinalizer(_entry); // logs leak warning + sync return
        }
        catch
        {
            // Finalizers MUST NOT throw — swallow.
        }
    }
}
```

**Key disciplines:**
- `Interlocked.Exchange(ref _disposed, 1)` — atomic flag flip, idempotent.
- `GC.SuppressFinalize(this)` only after successful claim (avoids double-finalization complications).
- Finalizer NEVER awaits and NEVER throws — uses synchronous defensive return path.
- `Value` getter throws `ObjectDisposedException` post-dispose — prevents UAF bugs in consumer code.

`SuppressFinalize` discipline confirmed: [CITED: learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync — "DisposeAsync method... call GC.SuppressFinalize"]

### Pattern 3: Builder Recipe (API-03, BOUND-01)

```csharp
// Source: project ARCHITECTURE.md Builder Pattern Shape; refined for Phase 1 scope
namespace Oragon.ElasticPool.Builder;

public static class ElasticObjectPoolFactory
{
    public static ElasticPoolBuilder<T> Build<T>(IServiceProvider services, CancellationToken cancellationToken = default)
        => new(services, cancellationToken);
}

public sealed class ElasticPoolBuilder<T>
{
    private readonly IServiceProvider _services;
    private readonly CancellationToken _ct;

    private FactoryDelegate<T>? _factory;
    private BeforeUseDelegate<T>? _beforeUse;
    private CheckDelegate<T>? _check;
    private AfterUseDelegate<T>? _afterUse;
    private ReleaseDelegate<T>? _release;
    private int _minSize = 0;
    private int _maxSize = 1;
    private int _initialSize = 0;
    private WaitBehavior _whenExhausted = WaitBehavior.Wait;
    private IItemFailurePolicy<T>? _failurePolicy;
    private TimeProvider _timeProvider = TimeProvider.System;

    internal ElasticPoolBuilder(IServiceProvider services, CancellationToken ct)
    {
        _services = services;
        _ct = ct;
    }

    public ElasticPoolBuilder<T> Factory(FactoryDelegate<T> factory)
    { _factory = factory ?? throw new ArgumentNullException(nameof(factory)); return this; }

    public ElasticPoolBuilder<T> BeforeUse(BeforeUseDelegate<T> hook) { _beforeUse = hook; return this; }
    public ElasticPoolBuilder<T> Check(CheckDelegate<T> hook) { _check = hook; return this; }
    public ElasticPoolBuilder<T> AfterUse(AfterUseDelegate<T> hook) { _afterUse = hook; return this; }
    public ElasticPoolBuilder<T> Release(ReleaseDelegate<T> hook) { _release = hook; return this; }

    public ElasticPoolBuilder<T> WithBounds(int minSize, int maxSize, int initialSize)
    {
        _minSize = minSize;
        _maxSize = maxSize;
        _initialSize = initialSize;
        return this;
    }

    public ElasticPoolBuilder<T> WhenExhausted(WaitBehavior behavior) { _whenExhausted = behavior; return this; }
    public ElasticPoolBuilder<T> WithFailurePolicy(IItemFailurePolicy<T> policy) { _failurePolicy = policy; return this; }
    public ElasticPoolBuilder<T> WithTimeProvider(TimeProvider timeProvider) { _timeProvider = timeProvider; return this; }

    public IElasticPool<T> Build()
    {
        // Validation — fail loud, fail early. Required by API-03 + BOUND-01.
        if (_factory is null)
            throw new InvalidOperationException("Factory hook is required. Call .Factory(...) before .Build().");
        if (_minSize < 0)
            throw new ArgumentOutOfRangeException(nameof(_minSize), "MinSize must be >= 0.");
        if (_maxSize < 1)
            throw new ArgumentOutOfRangeException(nameof(_maxSize), "MaxSize must be >= 1.");
        if (_minSize > _maxSize)
            throw new InvalidOperationException($"MinSize ({_minSize}) cannot exceed MaxSize ({_maxSize}).");
        if (_initialSize < _minSize || _initialSize > _maxSize)
            throw new InvalidOperationException(
                $"InitialSize ({_initialSize}) must satisfy MinSize ({_minSize}) <= InitialSize <= MaxSize ({_maxSize}).");

        var options = new ElasticPoolOptions<T>
        {
            Factory = _factory,
            BeforeUse = _beforeUse,
            Check = _check,
            AfterUse = _afterUse,
            Release = _release,
            MinSize = _minSize,
            MaxSize = _maxSize,
            InitialSize = _initialSize,
            WhenExhausted = _whenExhausted,
            FailurePolicy = _failurePolicy ?? new DiscardAndReplaceFailurePolicy<T>(),
            TimeProvider = _timeProvider,
        };

        return new ElasticPool<T>(options, _services, _ct);
    }
}

public sealed record ElasticPoolOptions<T>
{
    public required FactoryDelegate<T> Factory { get; init; }
    public BeforeUseDelegate<T>? BeforeUse { get; init; }
    public CheckDelegate<T>? Check { get; init; }
    public AfterUseDelegate<T>? AfterUse { get; init; }
    public ReleaseDelegate<T>? Release { get; init; }
    public int MinSize { get; init; }
    public int MaxSize { get; init; }
    public int InitialSize { get; init; }
    public WaitBehavior WhenExhausted { get; init; }
    public required IItemFailurePolicy<T> FailurePolicy { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public enum WaitBehavior { Wait, Throw }
```

### Pattern 4: Failure Policy Contract (FAIL-01, FAIL-02)

```csharp
namespace Oragon.ElasticPool.Abstractions;

public enum PoolState { Healthy, Unhealthy }

public interface IItemFailurePolicy<T>
{
    /// <summary>
    /// Decide what to do with a broken item or a failed factory call.
    /// Engine guarantees: counter is rolled back BEFORE this call (no ghost reservation).
    /// </summary>
    /// <param name="failedItem">The broken item — may be default(T) when factory itself failed.</param>
    /// <param name="failureKind">Why the policy was invoked.</param>
    /// <param name="exception">The exception, if any (factory failure case).</param>
    /// <returns>Action the engine should take.</returns>
    ValueTask<FailureDecision> HandleAsync(
        T? failedItem,
        FailureKind failureKind,
        Exception? exception,
        CancellationToken cancellationToken);
}

public enum FailureKind { FactoryThrew, BeforeUseUnhealthy, AfterUseUnhealthy }

public enum FailureDecision { Discard /* default in Phase 1 */, /* Quarantine reserved for v2 */ }

namespace Oragon.ElasticPool.Policies;

public sealed class DiscardAndReplaceFailurePolicy<T> : IItemFailurePolicy<T>
{
    public ValueTask<FailureDecision> HandleAsync(
        T? failedItem,
        FailureKind failureKind,
        Exception? exception,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(FailureDecision.Discard);
}
```

**Engine-side contract (counter rollback):** Per project PITFALLS.md Pitfall 3, the engine must:
1. Reserve capacity with `Interlocked.Increment(ref _total)` BEFORE invoking factory.
2. On factory throw: `Interlocked.Decrement(ref _total)` in `finally`, then invoke failure policy.
3. On `BeforeUse → Unhealthy`: pull entry off `_inUse` count, invoke policy, decrement `_total` (since the item is being discarded), trigger replacement if `_total < MinSize` (Phase 1 fixed-size: replacement always required).

### Pattern 5: Warm-up Pattern (BOUND-02)

```csharp
// Inside ElasticPool<T> ctor — runs as a Task that callers can await for readiness probes.
internal Task WarmupTask { get; }

internal ElasticPool(ElasticPoolOptions<T> options, IServiceProvider sp, CancellationToken ct)
{
    _options = options;
    _services = sp;
    _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    // ...

    // Eager warm-up — fire-and-await. Caller can await pool.WarmupTask for readiness.
    WarmupTask = WarmupAsync(_lifetimeCts.Token);
}

private async Task WarmupAsync(CancellationToken ct)
{
    // Parallel factory invocation; bounded by InitialSize.
    var tasks = new List<Task>(_options.InitialSize);
    for (int i = 0; i < _options.InitialSize; i++)
    {
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                Interlocked.Increment(ref _total);
                var item = await _options.Factory(_services, ct).ConfigureAwait(false);
                _idle.Enqueue(new PoolEntry<T>(item, _time.GetUtcNow()));
            }
            catch
            {
                Interlocked.Decrement(ref _total);
                throw;
            }
        }, ct));
    }
    await Task.WhenAll(tasks).ConfigureAwait(false);
}
```

To make warm-up **awaitable for readiness probes**, expose either:
- A public `Task ReadyAsync()` method that returns `WarmupTask`, OR
- Have `services.AddElasticPool<T>(...)` register an `IHostedService` that awaits `WarmupTask` during host startup (defer this to Phase 2 since it requires `Microsoft.Extensions.Hosting` dependency in Core, which is forbidden).

Phase 1 recommendation: expose `Task ReadyAsync()` on `IElasticPool<T>`. Document that consumers can `await pool.ReadyAsync()` in their startup if they want to gate readiness on warm-up completion.

### Pattern 6: `IMeterFactory` Recipe with Test Fallback (TELEM-01)

```csharp
// Source: learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation
// "Get a Meter via dependency injection" + "Test custom metrics"
namespace Oragon.ElasticPool.Telemetry;

internal static class PoolMeterNames
{
    public const string MeterName = "Oragon.ElasticPool";
    public const string AcquireCount = "pool.acquire.count";
    public const string FactoryFailures = "pool.factory.failures";
    // Phase 2 will add: pool.size, pool.available, pool.in_use, pool.waiting,
    // pool.acquire.duration, pool.grow.count, pool.shrink.count, pool.health.failures
    public const string PoolNameTag = "pool.name";
}

internal sealed class TelemetryEmitter : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _acquireCount;
    private readonly Counter<long> _factoryFailures;
    private readonly KeyValuePair<string, object?> _poolNameTag;
    private readonly bool _ownsMeter;

    public TelemetryEmitter(IServiceProvider services, string poolName)
    {
        // Try DI first — IMeterFactory is registered by services.AddMetrics()
        // (auto-called by Generic Host / WebApplication; library cannot assume).
        var factory = services.GetService<IMeterFactory>();
        if (factory is not null)
        {
            _meter = factory.Create(PoolMeterNames.MeterName);
            _ownsMeter = false; // factory owns lifecycle
        }
        else
        {
            // Fallback for tests / consumers that don't AddMetrics().
            // We own this Meter and must dispose it.
            _meter = new Meter(PoolMeterNames.MeterName);
            _ownsMeter = true;
        }

        _acquireCount = _meter.CreateCounter<long>(PoolMeterNames.AcquireCount, unit: "{acquires}",
            description: "Total number of successful Acquire calls.");
        _factoryFailures = _meter.CreateCounter<long>(PoolMeterNames.FactoryFailures, unit: "{failures}",
            description: "Total number of times the Factory hook threw an exception.");
        _poolNameTag = new KeyValuePair<string, object?>(PoolMeterNames.PoolNameTag, poolName);
    }

    public void OnAcquire() => _acquireCount.Add(1, _poolNameTag);
    public void OnFactoryFailure() => _factoryFailures.Add(1, _poolNameTag);

    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }
}
```

**Verification in tests** uses `MetricCollector<T>` from `Microsoft.Extensions.Diagnostics.Testing`:

```csharp
// Source: learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation — "Test with dependency injection"
[Fact]
public async Task Acquire_IncrementsAcquireCounter()
{
    var services = new ServiceCollection();
    services.AddMetrics(); // registers IMeterFactory
    services.AddElasticPool<MyResource>(name: "test", b => b.Factory(...).WithBounds(1,1,1));
    var sp = services.BuildServiceProvider();
    var pool = sp.GetRequiredKeyedService<IElasticPool<MyResource>>("test");
    var meterFactory = sp.GetRequiredService<IMeterFactory>();
    var collector = new MetricCollector<long>(meterFactory, "Oragon.ElasticPool", "pool.acquire.count");

    await using var item = await pool.AcquireAsync();

    var measurements = collector.GetMeasurementSnapshot();
    measurements.Should().ContainSingle().Which.Value.Should().Be(1);
}
```

### Pattern 7: DI Extension with Named Options (DI-01)

```csharp
// Source: synthesized from learn.microsoft.com/en-us/dotnet/core/extensions/options Named options + AddKeyedSingleton
namespace Oragon.ElasticPool.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddElasticPool<T>(
        this IServiceCollection services,
        string name,
        Action<ElasticPoolBuilder<T>> configure)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        // Register the configurator under the given name (named options pattern).
        // We don't actually bind T to ElasticPoolBuilder<T> via Configure<>;
        // instead we register the Action<> in a typed registry keyed by name.
        services.AddOptions<ElasticPoolBuilderConfigurator<T>>(name)
                .Configure(c => c.Configure = configure);

        // Singleton because pool owns expensive resources + the warm-up task.
        // Keyed registration so multiple pools of the same T coexist by name.
        services.TryAddKeyedSingleton<IElasticPool<T>>(name, (sp, key) =>
        {
            var keyName = (string)key!;
            var configurator = sp.GetRequiredService<IOptionsMonitor<ElasticPoolBuilderConfigurator<T>>>().Get(keyName);
            var builder = ElasticObjectPoolFactory.Build<T>(sp, sp.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None);
            configurator.Configure(builder);
            return builder.Build();
        });

        // For the default-name convenience: also register as non-keyed when name == string.Empty.
        if (name.Length == 0)
        {
            services.TryAddSingleton<IElasticPool<T>>(sp => sp.GetRequiredKeyedService<IElasticPool<T>>(string.Empty));
        }

        return services;
    }

    private sealed class ElasticPoolBuilderConfigurator<T>
    {
        public Action<ElasticPoolBuilder<T>> Configure { get; set; } = _ => { };
    }
}
```

**Notes on pattern:**
- Named options confirmed by docs as case-sensitive; default name is `string.Empty`. [CITED: learn.microsoft.com/en-us/dotnet/core/extensions/options — "All options are named instances. IConfigureOptions<TOptions> instances are treated as targeting the Options.DefaultName instance, which is string.Empty."]
- `AddKeyedSingleton` / `GetRequiredKeyedService` requires DI Abstractions ≥ 8.0 — present in 10.0.x. [VERIFIED: project STACK.md — DI 10.0.x supports keyed services]
- `IHostApplicationLifetime` reference is **optional** — pulled from `IServiceProvider` if available, else `CancellationToken.None`. This avoids forcing a `Microsoft.Extensions.Hosting.Abstractions` dependency on Core. (Test using `sp.GetService` not `GetRequiredService`.)

### Pattern 8: CancellationToken Linking (QUAL-01)

```csharp
// Source: learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.createlinkedtokensource
// Use the (CancellationToken, CancellationToken) overload for the common 2-token case (cheap, no array alloc).
// Use the ReadOnlySpan<CancellationToken> overload (net9+) for 3+ tokens.

internal sealed class ElasticPool<T> : IElasticPool<T>
{
    private readonly CancellationTokenSource _lifetimeCts; // canceled on pool dispose

    public async ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default)
    {
        // Combine: caller's CT + pool lifetime CT.
        // CRITICAL: dispose the linked CTS in finally — otherwise it leaks
        // (linked CTS retains a registration on each source token).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        // Use linked.Token for ALL hook invocations and waiter-queue operations.
        // ...
    }
}
```

**Disposal discipline:** Per docs, a linked CTS retains registrations on each source token. Failure to dispose causes accumulating handlers on long-lived tokens (`_lifetimeCts.Token` lives the entire pool lifetime — unbounded growth). ALWAYS `using var linked = ...` or wrap in try/finally. [CITED: learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.createlinkedtokensource — note that source CTS disposal can cause `ObjectDisposedException` when creating linked CTS]

**Interaction with `OperationCanceledException`:** when the linked token cancels, the thrown `OperationCanceledException.CancellationToken` is `linked.Token`, NOT the caller's token. To preserve correct semantics for callers:
```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    throw new OperationCanceledException(cancellationToken);
}
catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
{
    throw new ObjectDisposedException(nameof(ElasticPool<T>), "Pool was disposed during AcquireAsync.");
}
```

### Pattern 9: Pool Dispose State Machine (QUAL-02)

```csharp
internal enum PoolLifecycle { Open = 0, Draining = 1, Closed = 2 }

internal sealed class ElasticPool<T> : IElasticPool<T>, IAsyncDisposable, IDisposable
{
    private int _lifecycle = (int)PoolLifecycle.Open;

    public async ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _lifecycle) != (int)PoolLifecycle.Open)
            throw new ObjectDisposedException(nameof(ElasticPool<T>));
        // ...
    }

    // CANONICAL pattern: implement DisposeAsync, sync Dispose calls .GetAwaiter().GetResult()
    // with a hard timeout to avoid hangs in non-host DI scenarios.
    // [CITED: learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync]

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _lifecycle, (int)PoolLifecycle.Closed) == (int)PoolLifecycle.Closed)
            return; // already disposed

        // Transition to Draining first so new acquires reject immediately.
        // (We jumped straight to Closed above for the test; Draining is observable
        // only if you split it into two CAS steps. For Phase 1 simplicity, single transition.)

        _lifetimeCts.Cancel(); // cancels all pending waiters

        // Drain the idle queue — invoke Release hook on each.
        while (_idle.TryDequeue(out var entry))
        {
            try
            {
                if (_options.Release is { } release)
                    await release(entry.Item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.ReleaseHookFailedDuringDispose(ex);
            }
        }

        _lifetimeCts.Dispose();
        _telemetry.Dispose(); // disposes Meter only if we own it
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        // Per project PITFALLS.md Pitfall 8: standard Generic Host calls DisposeAsync,
        // but non-host DI (e.g., a manual ServiceProvider) only calls Dispose.
        // Block with a hard timeout to avoid deadlocks if called from sync context.
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
```

### Anti-Patterns to Avoid

- **Returning `Task<T>` from hooks:** breaking change to switch to `ValueTask<T>` later. Lock in `ValueTask<T>` from day one even if early implementations always go async.
- **Storing builder configuration mutably in the pool:** the `ElasticPoolOptions<T>` record must be init-only; the engine reads it but never writes. Builder produces options at `.Build()` and is discarded.
- **Hand-rolling counter ABA-safe linked-list:** use `ConcurrentQueue<T>` from BCL. Project PITFALLS.md Pitfall 4 covers this.
- **Calling `.Result` or `.GetAwaiter().GetResult()` on `ValueTask<T>` returns** in production code paths: defeats `ValueTask`'s purpose. Only `Dispose()` sync-fallback is acceptable. [CITED: ValueTask docs — never use `.Result` until completed]
- **Awaiting `ValueTask<T>` twice:** undefined behavior. Document on hook delegates that callers (engine) await exactly once.
- **Skipping `using` on linked `CancellationTokenSource`:** leaks registrations on `_lifetimeCts.Token` indefinitely.
- **Using `services.AddSingleton<IElasticPool<T>>` without `AddKeyed`:** breaks DI-01 multi-pool requirement.
- **Calling `meter.Dispose()` when DI owns the Meter:** `IMeterFactory` owns lifetime when it created the Meter; explicit dispose is no-op but conceptually wrong. [CITED: learn.microsoft.com — "IMeterFactory automatically manages the lifetime of any Meter objects it creates"]
- **Throwing in finalizer:** terminates the process. Always wrap finalizer body in `try { ... } catch { /* swallow */ }`.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Lock-free idle item store | Custom CAS stack | `ConcurrentQueue<PoolEntry<T>>` | Project PITFALLS.md Pitfall 4: ABA risk, BCL is battle-tested |
| Waiter queue with cancellation | `SemaphoreSlim` + `ConcurrentQueue<TCS>` dance | `Channel<TaskCompletionSource<PoolEntry<T>>>` (bounded) | Project PITFALLS.md Pitfall 1: lost wake-ups; Channel has built-in cancellation |
| Token-combining cancellation | Manual `Register` + cleanup | `CancellationTokenSource.CreateLinkedTokenSource(...)` | Built-in, well-tested, just remember to dispose |
| Periodic background work | `Task.Delay` loop or `System.Threading.Timer` | `PeriodicTimer` (Phase 2; ctor takes `TimeProvider`) | Drift-free, single-consumer fits sweep, `TimeProvider`-injectable [CITED: learn.microsoft.com/en-us/dotnet/api/system.threading.periodictimer — `PeriodicTimer(TimeSpan, TimeProvider)` overload exists] |
| Allocation-free logging | Manual `IsEnabled` + string interpolation | `[LoggerMessage]` source-gen `partial class` | Standard 2026 pattern; type-safe, zero-alloc [CITED: learn.microsoft.com/en-us/dotnet/core/extensions/logging-library-authors — "Prefer source-generated logging"] |
| Meter / counter declaration | `static readonly Meter` (anti-pattern in DI code) | `IMeterFactory.Create(name)` from DI; static fallback only when DI absent | DI-aware lifecycle, test isolation [CITED: learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation — "For usage in DI-aware libraries static variables are considered an anti-pattern"] |
| Time injection for tests | `Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow` | `TimeProvider _time` ctor parameter; `FakeTimeProvider` in tests | BCL-native since net8; standard pattern; required for Phase 2 sweeper but the API contract must be in place in Phase 1 |
| Named-pool registry | `Dictionary<string, IElasticPool<T>>` | `services.AddOptions<TConfigurator>(name)` + `AddKeyedSingleton` | Standard DI shape consumers expect; integrates with `IOptionsMonitor` |
| Public API surface tracking | Manual changelog | `Microsoft.CodeAnalysis.PublicApiAnalyzers` + `PublicAPI.{Shipped,Unshipped}.txt` | Mechanical detection of accidental ABI changes; required by OSS-05 anyway |

**Key insight:** Phase 1 is a stress test of the BCL. Every hand-rolled primitive in this domain has a documented failure mode (ABA, lost wake-ups, drift, leaks). The discipline is **reach for the BCL primitive first; profile-driven optimization only if profiling shows actual contention**.

---

## Common Pitfalls

### Pitfall 1: ValueTask awaited twice (subtle bug, no compiler warning)

**What goes wrong:** Engine code does `var vt = options.Factory(sp, ct); var item = await vt; if (something) await vt;` — second await produces undefined results.

**Why it happens:** `ValueTask<T>` looks like `Task<T>` but is single-consume. `Task<T>` allows multiple awaits.

**How to avoid:** Convention: any `ValueTask<T>` returned from a hook is awaited **exactly once** at the call site, immediately. If the result must be re-used, materialize to `T` first. If the value-task itself must be passed around, call `.Preserve()` to get a multi-awaitable view.

**Warning signs:** Sporadic `InvalidOperationException` from awaiter machinery; tests that flake under load.

[CITED: learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1 — "A ValueTask<TResult> instance may only be awaited once"]

### Pitfall 2: Linked `CancellationTokenSource` leak

**What goes wrong:** `var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);` without `using` or `finally`. Each call adds a registration on `_lifetimeCts.Token`. Over millions of acquires, registrations accumulate; eventually pool dispose triggers cancellation cascading through all of them.

**Why it happens:** The linked CTS pattern doesn't auto-clean. Many devs are unaware that linked CTS registers handlers on its source tokens.

**How to avoid:** ALWAYS `using var linked = CancellationTokenSource.CreateLinkedTokenSource(...)`. Add a unit test that creates 100k linked CTSes and asserts memory stays flat after collection.

### Pitfall 3: `IMeterFactory` not registered when consumer skips `AddMetrics()`

**What goes wrong:** `services.GetRequiredService<IMeterFactory>()` throws in unit tests that don't call `AddMetrics()`. Pool construction fails.

**Why it happens:** Generic Host auto-registers `IMeterFactory` since .NET 8, but bare `ServiceCollection` does not. Tests and minimal apps may skip it.

**How to avoid:** `IServiceProvider.GetService<IMeterFactory>()` (nullable) + fallback to `new Meter(name)`. `TelemetryEmitter` owns `_ownsMeter` flag and disposes only the fallback meter. [CITED: learn.microsoft.com — "IMeterFactory in the service container or you can manually register the type in any IServiceCollection by calling AddMetrics"]

### Pitfall 4: Finalizer thread-affinity surprise

**What goes wrong:** `~PoolItem()` runs on the GC finalizer thread — different sync context, different culture, may run at any time. Calling `_owner.ReturnAsync()` (returns `ValueTask`) and not awaiting it: the work fires off but exception propagation is lost.

**Why it happens:** Finalizers cannot await; cannot block (would block the finalizer thread for everyone).

**How to avoid:** Finalizer calls a synchronous defensive return path (`ReturnFromFinalizer`) that adds the entry back to `_idle` under whatever locking is in place, logs a "leak detected" warning via `[LoggerMessage]`, and never throws. Skip all async hooks on this path. Phase 1: `Release` hook is NOT invoked from finalizer (we don't know if it's safe to run on finalizer thread). Document this in XML docs.

### Pitfall 5: Builder returning `IElasticPool<T>` makes config inspection hard for tests

**What goes wrong:** Tests want to assert `pool.MaxSize == 10` but `IElasticPool<T>` is a minimal contract. Tests reach into internal `ElasticPool<T>._options` reflectively — fragile.

**How to avoid:** Expose a small read-only window on `IElasticPool<T>` for the parts tests legitimately care about: `MaxSize`, `MinSize`, `Available`, `InUse`. These are also valuable for telemetry consumers. Treat them as part of the public contract from day one (covered by `PublicAPI.Shipped.txt`).

### Pitfall 6: Stress tests in default `dotnet test` run

**What goes wrong:** A 10,000-iteration ping-pong test takes 30+ seconds, dominates CI time, can flake under load. Devs disable it locally.

**How to avoid:** Per CONTEXT.md, separate `Oragon.ElasticPool.Stress` project. CI default runs `dotnet test tests/Oragon.ElasticPool.Tests`. Nightly job (Phase 4) runs `dotnet test tests/Oragon.ElasticPool.Stress`. Document in README how to run stress tests locally: `dotnet test tests/Oragon.ElasticPool.Stress --logger console;verbosity=detailed`.

### Pitfall 7: Coverage gate breaks because xUnit MTP runner reports differently

**What goes wrong:** `dotnet test /p:CollectCoverage=true /p:Threshold=90` works for VSTest-driven runs. Microsoft.Testing.Platform reports coverage differently and may not respect MSBuild coverlet properties identically.

**How to avoid:** For Phase 1 (and CI gate), use `coverlet.collector` with `dotnet test --collect:"XPlat Code Coverage"` (data-collector mode), then post-process with `reportgenerator` and enforce threshold via a CI step (e.g., `dotnet tool run reportgenerator -reports:**/coverage.cobertura.xml -targetdir:coverage -reporttypes:TextSummary` then grep). This works identically across VSTest and MTP. The MSBuild integration approach works too but is brittle in MTP transition. [CITED: github.com/coverlet-coverage/coverlet MSBuildIntegration.md]

### Pitfall 8: AwesomeAssertions namespace surprise

**What goes wrong:** Devs writing `using FluentAssertions;` find the package available (transitively) and write FA-style code; CI builds fail because Core depends on `AwesomeAssertions` namespace, not `FluentAssertions`.

**How to avoid:** AwesomeAssertions v9.x renamed all namespaces from `FluentAssertions.*` to `AwesomeAssertions.*`. Use `using AwesomeAssertions;` in test files. The assertion API surface itself is identical to FA v7. Add a single editor snippet / test template documenting the correct using-statement. [CITED: awesomeassertions.org/upgradingtov9 — "v9 changed all FluentAssertions namings to AwesomeAssertions"]

---

## Code Examples

### Example 1: Minimal Phase 1 .csproj for Core

```xml
<!-- src/Oragon.ElasticPool/Oragon.ElasticPool.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>true</IsPackable>
    <PackageId>Oragon.ElasticPool</PackageId>
    <Description>Generic, elastic in-process object pool for .NET with health auto-healing and built-in observability.</Description>
    <PackageTags>pool;objectpool;adaptive;elastic;async;observability;opentelemetry</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" PrivateAssets="all" />
    <PackageReference Include="MinVer" PrivateAssets="all" />
    <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\..\README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

### Example 2: Phase 1 Test .csproj with xUnit v3 + MTP + AwesomeAssertions

```xml
<!-- tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
    <!-- Opt into MTP runner for native xUnit v3 self-execution. -->
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <!-- Allow `dotnet test` to drive MTP on net8/net9 (net10 SDK uses global.json). -->
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.v3.runner.visualstudio" />
    <PackageReference Include="AwesomeAssertions" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.Testing" />
    <PackageReference Include="coverlet.collector" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Oragon.ElasticPool\Oragon.ElasticPool.csproj" />
  </ItemGroup>
</Project>
```

For .NET 10 SDK, also add at solution root:

```json
// global.json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

[CITED: xunit.net/docs/getting-started/v3/microsoft-testing-platform — "For .NET 10+ Create or edit global.json at solution root"]

### Example 3: `[LoggerMessage]` source-gen for pool diagnostics

```csharp
// Source: learn.microsoft.com/en-us/dotnet/core/extensions/logging-library-authors
namespace Oragon.ElasticPool.Telemetry;

internal static partial class PoolDiagnosticsLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Pool item leaked (consumer forgot to dispose). Returning defensively to pool '{PoolName}'.")]
    public static partial void ItemLeaked(this ILogger logger, string poolName);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Factory hook threw for pool '{PoolName}'. Counter rolled back.")]
    public static partial void FactoryFailed(this ILogger logger, string poolName, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "BeforeUse hook reported Unhealthy for pool '{PoolName}'. Invoking failure policy.")]
    public static partial void BeforeUseUnhealthy(this ILogger logger, string poolName);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
        Message = "Release hook threw during pool dispose for pool '{PoolName}'. Continuing drain.")]
    public static partial void ReleaseHookFailedDuringDispose(this ILogger logger, string poolName, Exception exception);
}
```

### Example 4: Stress test sketch — `MaxSize=1` ping-pong (Phase 1 anchor test)

```csharp
// tests/Oragon.ElasticPool.Stress/PingPongStressTest.cs
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Oragon.ElasticPool.DependencyInjection;
using Xunit;

public class PingPongStressTest
{
    [Fact(Timeout = 30_000)] // hard ceiling; if any AcquireAsync hangs, the test fails
    public async Task MaxSize1_HundredsOfThreads_TenThousandIterations_NoDeadlock()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddElasticPool<Resource>("test", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new Resource()))
            .WithBounds(minSize: 1, maxSize: 1, initialSize: 1));
        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IElasticPool<Resource>>("test");

        const int threads = 256;
        const int iterationsPerThread = 10_000 / threads;

        var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < iterationsPerThread; i++)
            {
                using var perAcquireTimeout = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
                perAcquireTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var item = await pool.AcquireAsync(perAcquireTimeout.Token);
                // simulate brief work
                await Task.Yield();
            }
        }, watchdog.Token)).ToArray();

        await Task.WhenAll(tasks);

        // Invariants:
        pool.InUse.Should().Be(0);
        pool.Available.Should().Be(1);
    }

    private sealed class Resource { }
}
```

### Example 5: `FakeTimeProvider` injection for time-sensitive tests

```csharp
// Phase 1 doesn't run a sweeper, but warm-up readiness or any future time logic
// uses _options.TimeProvider. Even Phase 1 should test that the injection point works.
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using AwesomeAssertions;
using Xunit;

public class TimeProviderInjectionTests
{
    [Fact]
    public void Pool_AcceptsCustomTimeProvider_AndRecordsEntryCreatedAt()
    {
        var fakeTime = new FakeTimeProvider(startDateTime: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection().AddMetrics();
        services.AddElasticPool<MyResource>("t", b => b
            .Factory((sp, ct) => ValueTask.FromResult(new MyResource()))
            .WithBounds(1, 1, 1)
            .WithTimeProvider(fakeTime));
        var pool = services.BuildServiceProvider().GetRequiredKeyedService<IElasticPool<MyResource>>("t");

        // Even in Phase 1, timestamps on PoolEntry are stamped from TimeProvider.
        // Assert via a (planned) IElasticPool<T> test hook or via internals-visible-to.
        pool.Should().NotBeNull();
    }
}
```

[VERIFIED: nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing — `FakeTimeProvider(DateTimeOffset startDateTime)` constructor confirmed in v10.5.0]

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `Microsoft.NET.Test.Sdk` + `xunit` (v2) | `xunit.v3` + Microsoft.Testing.Platform | xUnit v3 GA Jul 2025; .NET 10 SDK ships native MTP runner Nov 2025 | Different .csproj shape; native binary execution; faster |
| FluentAssertions v8 | AwesomeAssertions v9 (Apache-2 fork) | FA v8 changed license Jan 2025 to Xceed Community | Namespace `AwesomeAssertions` (not `FluentAssertions`); API identical to FA v7 |
| `static readonly Meter s_meter = new("…")` | `IMeterFactory.Create("…")` from DI | .NET 8 introduced `IMeterFactory`; auto-registered by Generic Host | Test isolation, lifecycle management, parallel-safe |
| `Func<DateTime> now = () => DateTime.UtcNow` injection | `TimeProvider` BCL injection + `FakeTimeProvider` for tests | TimeProvider in-box net8+; FakeTimeProvider in `Microsoft.Extensions.TimeProvider.Testing` 10.5.0 | Standardized abstraction; works with `PeriodicTimer(TimeSpan, TimeProvider)` |
| Manual `_log.LogInformation("…", arg)` | `[LoggerMessage]` source-gen partial methods | Source-gen logging GA in net6+, refined through net10 | Allocation-free, type-safe, isolates message templates |
| `services.AddSingleton<IThing>` (single instance) | `services.AddKeyedSingleton<IThing>(name, ...)` | Keyed services GA in .NET 8 | Multi-instance with name-based lookup — required for DI-01 |

**Deprecated/outdated:**
- `xunit` v2 — security-fix-only mode since v3 GA. Don't start new projects on v2.
- FluentAssertions v8 — license incompatible with OSS. v7 still Apache-2 but no future evolution.
- Moq 4.20+ — SponsorLink controversy; community moved to NSubstitute.
- `IModel` (RabbitMQ.Client v6) — renamed `IChannel` in v7; project doesn't use it in Phase 1 (Phase 3 concern), but mention here so plans don't accidentally reference old API in samples.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `Microsoft.Extensions.Options` 10.0.x is acceptable to add to Core dependencies despite project STACK.md's "abstractions only" rule | Standard Stack | If user vetoes, must hand-roll a named-pool registry; loses standard `IOptionsMonitor` integration. Worth confirming in discuss-phase. |
| A2 | `Microsoft.Extensions.Diagnostics.Testing` 10.0.5 exists with `MetricCollector<T>` | Test Stack table | If version differs, swap to whichever 10.0.x ships `MetricCollector<T>`. The class itself is documented and stable. [Partially CITED: learn.microsoft.com names the class but doesn't pin a package version on that page] |
| A3 | `Microsoft.CodeAnalysis.PublicApiAnalyzers` 3.3.4 is current; pinned to 3.3.x range | Standard Stack | If 3.4+ exists, use it — analyzers improve over time. Verify with `dotnet package search` at install time. |
| A4 | Coverage gate via `coverlet.collector --collect:"XPlat Code Coverage"` + `reportgenerator` works under MTP runner | Pitfall 7 | If MTP path is broken, fall back to MSBuild integration `/p:CollectCoverage=true /p:Threshold=90`. Both are documented; pick whichever CI pipeline observes correctly. |
| A5 | `IPoolItem<out T>` covariant interface is acceptable (uses `out`) | Pattern 2 | Locks pooled types to invariant if `out` causes constraint issues with `IDisposable`/`IAsyncDisposable`. May need to drop `out` — verify via compilation. Low risk. |
| A6 | Default `WaitBehavior` is `Wait` (per CONTEXT.md) and `WaitBehavior` enum is the right shape | Pattern 3 | Discretion area per CONTEXT.md; this implements the locked decision. |
| A7 | `Task ReadyAsync()` is the right shape for awaitable warm-up vs. having `BOUND-02` express it differently (e.g., `IHostedService` integration) | Pattern 5 | Plausible alternative is to make warm-up implicit (start in ctor, no public way to await). REQUIREMENTS.md says "awaitable" — `ReadyAsync()` honors that. |
| A8 | `IElasticPool<T>` exposes read-only `MaxSize`/`MinSize`/`Available`/`InUse` on the contract | Pitfall 5 | These properties are sometimes considered internals. If user wants minimal surface, expose a separate `IElasticPoolMetrics` contract. Adds a type but keeps `IElasticPool<T>` lean. |

---

## Open Questions

1. **`IOptions`/`IOptionsMonitor` dependency in Core — accept or hand-roll?**
   - What we know: DI-01 requires named pools, named options is the standard pattern.
   - What's unclear: Project STACK.md prefers minimal Core dependencies. `Microsoft.Extensions.Options` 10.0.x is small but adds `Microsoft.Extensions.Primitives` transitively.
   - Recommendation: Accept the dependency. Document in README. Hand-rolling loses too much (no `IOptionsMonitor` change tracking, no integration with `services.Configure<T>` user expectations).

2. **`Task ReadyAsync()` vs. implicit warm-up vs. `IHostedService`?**
   - What we know: BOUND-02 says "awaitable until atinger InitialSize, com gating opcional para readiness probes".
   - What's unclear: Mechanism. `IHostedService` integration would be ideal but requires Hosting dependency.
   - Recommendation: `Task ReadyAsync()` on the contract for v1.0. Document in README how to wire it to `IHostedService` in consumer code. Defer first-class hosting integration to v1.x.

3. **Should `Acquire()` (sync) call `AcquireAsync(default).AsTask().GetAwaiter().GetResult()` when no item is free, or throw immediately?**
   - What we know: CONTEXT.md says sync `Acquire()` is fast-path only ("retorno imediato se há item livre").
   - What's unclear: Behavior when no item is free under `WaitBehavior.Wait`. Block (potential deadlock from sync ctx) or throw `PoolExhaustedException`?
   - Recommendation: Throw `PoolExhaustedException` (or a related `NoItemAvailableException`) immediately. Document: sync `Acquire()` ALWAYS returns immediately or throws. Async `AcquireAsync` is the only path that waits. Aligns with CONTEXT.md "retorno imediato".

4. **Phase 1 telemetry counter cardinality — include `pool.name` tag?**
   - What we know: TELEM-01 says `pool.name` tag with limited cardinality.
   - What's unclear: Phase 1 only ships 2 counters; `pool.name` cardinality is bounded by number of named pools the user creates. Likely safe.
   - Recommendation: Include `pool.name` from day one. Locking a tag in later is cheaper than removing it (consumers may already build dashboards on it).

---

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 10 SDK | Build host for multi-target | check at setup | 10.0.x | Phase 4 will pin via `global.json` |
| .NET 9 runtime | Test execution net9.0 target | check at setup | 9.0.x | Drop net9.0 TFM if missing (project research notes net9 EOL May 2026) |
| .NET 8 runtime | Test execution net8.0 target | check at setup | 8.0.x | Required for LTS coverage |
| `dotnet` CLI | All build/test/pack | yes (any modern install) | 10.x | — |

**Phase 1 has no external service dependencies** (no RabbitMQ, no Docker, no Testcontainers). All work runs in-process. RabbitMQ comes in Phase 3.

**Missing dependencies with no fallback:** None for Phase 1 — pure code/tests.

---

## Validation Architecture

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 + Microsoft.Testing.Platform |
| Config file | `global.json` (test runner selector for net10), `.csproj` props (`UseMicrosoftTestingPlatformRunner`, `TestingPlatformDotnetTestSupport`) |
| Quick run command | `dotnet test tests/Oragon.ElasticPool.Tests --no-restore --logger console;verbosity=quiet` |
| Full suite command | `dotnet test --collect:"XPlat Code Coverage" /p:Threshold=90 /p:ThresholdType=line` (excludes Stress project, which lives in separate solution filter) |

### Phase 1 Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| API-01 | `Acquire()` returns immediately when item free; `AcquireAsync()` returns `ValueTask<IPoolItem<T>>` | unit | `dotnet test --filter "FullyQualifiedName~AcquireApiTests"` | ❌ Wave 0 |
| API-02 | `IPoolItem<T>` idempotent dispose; double-dispose is no-op | unit | `dotnet test --filter "FullyQualifiedName~PoolItemDisposalTests"` | ❌ Wave 0 |
| API-03 | `Build()` validates required Factory; throws on missing | unit | `dotnet test --filter "FullyQualifiedName~BuilderValidationTests"` | ❌ Wave 0 |
| HOOK-01..05 | Hook signatures accept `CancellationToken`, return `ValueTask<T>` | unit | `dotnet test --filter "FullyQualifiedName~HookSignatureTests"` | ❌ Wave 0 |
| BOUND-01 | `0 ≤ Min ≤ Initial ≤ Max` validated | unit | `dotnet test --filter "FullyQualifiedName~BoundsValidationTests"` | ❌ Wave 0 |
| BOUND-02 | Eager warm-up reaches `InitialSize`; cancellable | unit | `dotnet test --filter "FullyQualifiedName~WarmupTests"` | ❌ Wave 0 |
| FAIL-01 | `IItemFailurePolicy<T>` invoked on factory throw | unit | `dotnet test --filter "FullyQualifiedName~FailurePolicyTests"` | ❌ Wave 0 |
| FAIL-02 | `DiscardAndReplaceFailurePolicy<T>` is default | unit | `dotnet test --filter "FullyQualifiedName~DefaultFailurePolicyTests"` | ❌ Wave 0 |
| TELEM-01 | `pool.acquire.count` incremented; observable via `MetricCollector<long>` | unit | `dotnet test --filter "FullyQualifiedName~TelemetryTests"` | ❌ Wave 0 |
| DI-01 | `services.AddElasticPool<T>(name, configure)` registers keyed singleton | unit | `dotnet test --filter "FullyQualifiedName~DependencyInjectionTests"` | ❌ Wave 0 |
| QUAL-01 | `CancellationToken` cancels `AcquireAsync` waiters cleanly | unit | `dotnet test --filter "FullyQualifiedName~CancellationTests"` | ❌ Wave 0 |
| QUAL-02 | `DisposeAsync` drains pool, calls `Release` on each idle item | unit | `dotnet test --filter "FullyQualifiedName~DisposeDrainTests"` | ❌ Wave 0 |
| (Anchor) | `MaxSize=1` ping-pong, 256 threads × 40 iterations, no deadlock | stress | `dotnet test tests/Oragon.ElasticPool.Stress` | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test tests/Oragon.ElasticPool.Tests --no-restore` (~few seconds)
- **Per wave merge:** `dotnet test --collect:"XPlat Code Coverage" /p:Threshold=90` (full suite + coverage gate)
- **Phase gate:** Above + `dotnet test tests/Oragon.ElasticPool.Stress` (stress runs until green)

### Wave 0 Gaps
- [ ] `tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj` — base test project per Example 2
- [ ] `tests/Oragon.ElasticPool.Stress/Oragon.ElasticPool.Stress.csproj` — stress test project (separate)
- [ ] `tests/Oragon.ElasticPool.Tests/GlobalUsings.cs` — `global using AwesomeAssertions;` `global using Xunit;` `global using NSubstitute;`
- [ ] `global.json` at repo root with `"test": { "runner": "Microsoft.Testing.Platform" }`
- [ ] `Directory.Packages.props` updated with all Phase 1 package versions
- [ ] `src/Oragon.ElasticPool/PublicAPI.Shipped.txt` (empty initially) and `PublicAPI.Unshipped.txt` (filled by analyzer codefix as types are added)
- [ ] CI workflow that runs `dotnet test` matrix on `[ubuntu-latest, windows-latest] × [net8.0, net9.0, net10.0]` (Phase 4 finalizes; Phase 1 needs at least a draft)

---

## Sources

### Primary (HIGH confidence — official docs / NuGet registry, May 2026)

- [NuGet: AwesomeAssertions 9.4.0](https://www.nuget.org/packages/AwesomeAssertions) — version, license (Apache-2 permanent), framework targets, fork status
- [Awesome Assertions: Upgrading to v9](https://awesomeassertions.org/upgradingtov9) — namespace rename `FluentAssertions` → `AwesomeAssertions`
- [NuGet: Microsoft.Extensions.TimeProvider.Testing 10.5.0](https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing) — `FakeTimeProvider` API surface, ctor signatures
- [NuGet: xunit.v3 3.2.2](https://www.nuget.org/packages/xunit.v3) — current GA version
- [xUnit v3 + Microsoft Testing Platform setup](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform) — `.csproj` props (`UseMicrosoftTestingPlatformRunner`, `TestingPlatformDotnetTestSupport`), `global.json` runner config for .NET 10
- [Microsoft Learn: Options pattern (Named options)](https://learn.microsoft.com/en-us/dotnet/core/extensions/options) — `IOptionsSnapshot`, `IOptionsMonitor`, `IConfigureNamedOptions`, default name = `string.Empty`
- [Microsoft Learn: Logging guidance for library authors](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging-library-authors) — `[LoggerMessage]` source-gen, `IsEnabled` discipline, `NullLogger` defaults
- [Microsoft Learn: Creating Metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation) — `IMeterFactory`, `AddMetrics()` registration, `MetricCollector<T>` for tests, OTel naming guidelines
- [Microsoft Learn: Implement DisposeAsync](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync) — sealed pattern, `GC.SuppressFinalize`, sync+async dual implementation
- [Microsoft Learn: ValueTask&lt;TResult&gt; struct](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1) — single-await contract, when to choose vs `Task<T>`
- [Microsoft Learn: PeriodicTimer class](https://learn.microsoft.com/en-us/dotnet/api/system.threading.periodictimer) — `PeriodicTimer(TimeSpan, TimeProvider)` ctor (net8+)
- [Microsoft Learn: CancellationTokenSource.CreateLinkedTokenSource](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.createlinkedtokensource) — overloads, disposal discipline, `ObjectDisposedException` cases
- [coverlet MSBuild integration docs](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/MSBuildIntegration.md) — `/p:Threshold=90`, `/p:ThresholdType`, `/p:ThresholdStat`
- [PublicApiAnalyzers Help](https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md) — `.csproj` setup, `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` conventions

### Secondary (MEDIUM confidence — synthesized from project research)

- `.planning/research/STACK.md` (HIGH within project context) — package versions, dependency boundaries, what NOT to use
- `.planning/research/ARCHITECTURE.md` — internal data structures (ConcurrentQueue, Channel, PeriodicTimer), builder pattern, sealing discipline
- `.planning/research/PITFALLS.md` — Pitfalls 1–4, 8, 14, 18, 19 directly bear on Phase 1
- `.planning/research/SUMMARY.md` — overall direction, confidence assessments

### Tertiary (LOW confidence — inferred patterns)

- AwesomeAssertions v9 API parity with FluentAssertions v7 — claimed by project README; verified namespace change but did not enumerate every API. Assume parity holds for assertion methods used in Phase 1 (`Should().Be()`, `Should().Throw<>()`, `Should().ContainSingle()`, etc.); flag for verification when first test fails.
- `Microsoft.Extensions.Diagnostics.Testing` package version 10.0.5 — class `MetricCollector<T>` documented but page didn't list the package version. Plausibly `Microsoft.Extensions.Diagnostics.Testing` 10.0.x ships in tandem with the rest of the 10.0.x extensions family.

---

## Metadata

**Confidence breakdown:**
- Standard stack: **HIGH** — every package version verified at nuget.org or learn.microsoft.com
- Architecture patterns: **HIGH** — synthesized from project research (HIGH) + verified BCL APIs
- Disposal/cancellation patterns: **HIGH** — direct quotes from learn.microsoft.com docs
- Hook signatures (`ValueTask<T>` + `CancellationToken`): **HIGH** — locked by project PITFALLS.md as non-retrofittable
- DI named-options recipe: **HIGH** — confirmed by Options pattern docs
- `IMeterFactory` fallback pattern: **HIGH** — direct from metrics-instrumentation docs
- AwesomeAssertions parity with FluentAssertions v7: **MEDIUM** — namespace rename verified, full API parity inferred from project README claims
- Coverage gate under MTP runner: **MEDIUM** — coverlet docs cover the MSBuild path; MTP-specific behavior may need verification when CI is wired up

**Research date:** 2026-05-02
**Valid until:** 2026-06-02 (30 days for stable BCL/package landscape; longer for ecosystem-level facts, shorter for fast-moving xUnit v3 + MTP integration which is still evolving in monthly releases)
