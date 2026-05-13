# Project Research Summary

**Project:** Oragon.ElasticPool
**Domain:** Multi-target .NET OSS NuGet library — generic in-process object pool + RabbitMQ adapter
**Researched:** 2026-05-03
**Confidence:** HIGH

## Executive Summary

Oragon.ElasticPool fills a genuine gap in the .NET ecosystem: `Microsoft.Extensions.ObjectPool` is fixed-size and health-blind; Polly handles resilience but not pooling; RabbitMQ.Client v7 has no connection/channel pool. The design space is well-charted by HikariCP, Apache commons-pool2, and node generic-pool — all converging on the same core primitives: min/max bounds, async borrow/return, on-borrow validation, background health sweep, and idle eviction. What Oragon.ElasticPool adds on top of that established baseline is the trifecta that earns its keep: composite-signal elasticity (grow using waiter-queue depth + sustained utilization + wait latency together), pluggable failure policy (discard-and-replace vs. quarantine+backoff vs. custom), and five-stage lifecycle hooks (Factory / BeforeUse / Check / AfterUse / Release). These three together — not any single one — are the stated Core Value and cannot be trimmed.

The recommended implementation is BCL-centric and deliberately lean. Core depends on exactly two `Microsoft.Extensions.*` packages (Logging.Abstractions, DependencyInjection.Abstractions); every other required primitive — `System.Threading.Channels`, `ConcurrentQueue<T>`, `PeriodicTimer`, `Meter`, `ActivitySource`, `ValueTask`, `IAsyncDisposable`, `IMeterFactory` — is in-box on every target (`net8.0`/`net9.0`/`net10.0`) with zero polyfills. The internal engine is a single sealed `ElasticPool<T>` built on three BCL primitives: `ConcurrentQueue<PoolEntry<T>>` for idle items, `Channel<TaskCompletionSource<PoolEntry<T>>>` for the waiter queue (direct-handoff pattern, eliminates TOCTOU races), and `PeriodicTimer` for the background sweep. All concrete classes are sealed; extension is via hooks and `IItemFailurePolicy<T>` — not subclassing. Public surface is approximately 12 types.

The critical risks all concentrate in Phase 1, before any user touches the API. Cancellation tokens must appear in every hook signature from day one — adding them later is a breaking change. `ValueTask` return semantics must be documented from day one. The `ConcurrentQueue`+`Channel` waiter design must be chosen before any other concurrency code is written — the wrong choice here cascades into every phase. Factory exceptions must do counter rollback (`Interlocked.Decrement`) or the pool will report "full" while holding nothing. All other risks (RabbitMQ autorecovery conflict, metric cardinality, elastic oscillation) are real but phase-localised and have clear mitigations documented in PITFALLS.md.

## Key Findings

### Recommended Stack

The 2026 stack for a .NET OSS pooling library is stable and BCL-centric. Build host is .NET 10 SDK targeting `net10.0;net9.0;net8.0`. Central Package Management (`Directory.Packages.props`) keeps version governance clean from day one. Test tooling uses xUnit v3 (v2 is security-fix-only since July 2025), Shouldly for assertions (FluentAssertions v8 changed to a $129/dev/yr commercial license — disqualified for OSS), NSubstitute for mocking, and Testcontainers.RabbitMq for integration tests. MinVer 6.0 handles tag-driven SemVer; SourceLink + `snupkg` symbol packages handle source-level debugging for consumers.

**Core technologies:**
- `.NET 10 SDK / net10.0;net9.0;net8.0 multi-target`: covers both active LTS releases (net8 EOL Nov 2026, net10 LTS to Nov 2028); zero polyfills required across the matrix
- `System.Threading.Channels` (BCL): the waiter queue primitive — lock-free, async-aware, cancellation-built-in; chosen over `SemaphoreSlim`+`ConcurrentQueue` hand-rolling
- `System.Diagnostics.Metrics.Meter` + `IMeterFactory` (BCL): OpenTelemetry-friendly metrics; `IMeterFactory` makes meters testable and DI-lifetime-safe
- `System.Diagnostics.ActivitySource` (BCL): distributed tracing surface; declare once per assembly as `internal static readonly`, never disposed
- `PeriodicTimer` (BCL, net6+): drift-free background sweep, single-consumer model, `TimeProvider`-injectable for deterministic tests
- `Microsoft.Extensions.Logging.Abstractions 10.0.x`: `ILogger<T>` + `[LoggerMessage]` source-gen; only the abstraction, never the implementation
- `Microsoft.Extensions.DependencyInjection.Abstractions 10.0.x`: `IServiceCollection` extensions; only the abstraction, never the container
- `RabbitMQ.Client 7.2.1`: async-first v7; `IModel` renamed `IChannel`; all methods have `Async` suffix; `AutomaticRecoveryEnabled` must be explicitly disabled in pool-managed connections
- `xUnit v3 3.2.2` + `Shouldly 4.3` + `NSubstitute 5.3`: test stack (all OSS-safe licenses)
- `MinVer 6.0.0` + `Microsoft.SourceLink.GitHub 8.0.0`: build-only (`PrivateAssets="all"`), tag-driven SemVer
- `Testcontainers.RabbitMq 4.6+`: real-broker integration tests in CI
- `BenchmarkDotNet 0.15.8+`: first version with .NET 10 support; microbenchmarks for acquire hot path

**One `#if` concern resolved:** `System.Threading.Lock` (net9+) — decision is to use `object` lock targets across all TFMs; negligible perf delta, zero `#if` clutter.

### Expected Features

Based on cross-ecosystem analysis (HikariCP, commons-pool2, node generic-pool, Microsoft.Extensions.ObjectPool, Reactor Pool), the feature landscape divides cleanly.

**Must have (table stakes — serious .NET devs will reject the library without these):**
- Generic `IElasticPool<T>` with async-first `AcquireAsync(CancellationToken)` + fast-path sync `Acquire()`
- `IPoolItem<T>` disposable wrapper (prevents leaks; `await using` enforced in all docs/samples)
- `MinSize` / `MaxSize` / `InitialSize` bounds with eager warm-up (awaitable)
- Thread-safety under high concurrency (validated by stress tests)
- Acquire timeout via `CancellationToken` (idiomatic .NET; no separate timeout config)
- Idle eviction (shrink to `MinSize` after `IdleTimeout`)
- On-borrow validation (`BeforeUse` hook — cheap in-process check only, never a server round-trip)
- Object factory + cleanup hooks (`Factory`, `Release`)
- `IAsyncDisposable` + `IDisposable` on the pool itself with drain semantics (both must be implemented)
- DI extension `services.AddElasticPool<T>(...)`
- Built-in `Meter` metrics (`oragon.pool.size`, `oragon.pool.available`, `oragon.pool.in_use`, `oragon.pool.pending_requests`, `oragon.pool.acquire.duration`, plus event counters)
- `ILogger<T>` structured logging on state transitions (source-gen `[LoggerMessage]`)
- CancellationToken plumbed through every async path including all hooks
- Documented quickstart + working RabbitMQ sample
- Multi-TFM CI matrix + symbol packages + SourceLink

**Should have (differentiators — competitive moat vs. MS ObjectPool and roll-your-own):**
- Composite-signal elasticity: grow using waiter-queue depth AND sustained utilization % AND acquire-wait p95 — not any single signal (the headline differentiator)
- Five-stage lifecycle hooks: Factory / BeforeUse / Check / AfterUse / Release — more granular than any reference pool; matches RabbitMQ channel lifecycle
- Pluggable `IItemFailurePolicy<T>`: ship `DiscardAndReplaceFailurePolicy<T>` as default
- Background health sweep (`Check` hook on idle items via `PeriodicTimer`) with adaptive backoff under failure storms
- Native OpenTelemetry telemetry: `Meter` + `ActivitySource` + documented `oragon.pool.*` metric names (public contract)
- Layered pools: `IChannel` pool whose Factory acquires `IConnection` from an inner connection pool
- Graceful drain on `DisposeAsync` (state machine: Open → Draining → Closed)

**Defer to v1.x / v2+:**
- `QuarantineWithBackoffFailurePolicy<T>` — add when user feedback confirms need
- Per-item `MaxLifetime` / `MaxUses` rotation — add after stale-connection reports in production
- `DrainAsync(TimeSpan)` explicit graceful drain public API — add when k8s SIGTERM integration is requested
- Polly glue package — add when users request retry+circuit-breaker around Acquire
- Additional adapters (HttpClient, Npgsql, gRPC channels) — only after RabbitMQ adapter validates the abstraction
- Keyed sub-pools — only with strong demand evidence

### Architecture Approach

The architecture is two NuGet packages with a thin public surface (~12 types) and an internal engine (`sealed ElasticPool<T>`) built entirely on BCL primitives. All concrete classes are sealed; extension points are `IItemFailurePolicy<T>` and the five lifecycle hook delegates — never subclassing. The RabbitMQ adapter is a pure consumer of the Core builder API; it contributes no new Core abstractions. Layered pools (channel over connection) are composed entirely through Core hooks using `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` to tie channel lifecycle to connection lease lifecycle. Telemetry is centralized in `TelemetryEmitter` (single `Meter` + single `ActivitySource`, both named `"Oragon.ElasticPool"`); logging uses `[LoggerMessage]` source-gen throughout for allocation-free hot paths.

**Major components:**
1. `IElasticPool<T>` / `IPoolItem<T>` / `IItemFailurePolicy<T>` — public contracts; the only types consumers code against
2. `ElasticObjectPoolFactory` + `ElasticPoolBuilder<T>` + `ElasticPoolOptions<T>` — fluent builder → frozen config record → engine construction
3. `sealed ElasticPool<T>` (internal engine) — `ConcurrentQueue<PoolEntry<T>>` for idle items, `Channel<TCS<PoolEntry<T>>>` for waiter direct-handoff, `Interlocked` counters, state machine (Open / Draining / Closed)
4. `BackgroundSweeper` (internal) — `PeriodicTimer`-driven loop: shrink pass + health-check pass; `TimeProvider`-injected for test determinism via `FakeTimeProvider`
5. `PressureSampler` (internal) — composite-signal logic: sliding window of utilization %, waiter-queue depth, acquire-wait p95; drives grow decisions inline on the acquire slow path
6. `TelemetryEmitter` (internal) — owns `Meter` (via `IMeterFactory` if present) + `ActivitySource`; all instrument constants in `PoolMeterNames`
7. `PoolDiagnosticsLog` (internal static partial) — `[LoggerMessage]` source-gen; zero-allocation logging
8. `ServiceCollectionExtensions` (Core + RabbitMQ) — DI wiring; pool registered as `Singleton` (owns background task + expensive resources); also `AddKeyedSingleton` for named pools
9. `Oragon.ElasticPool.RabbitMQ` package — `AddElasticConnectionPool` + `AddElasticChannelPool`; `ChannelLease` + `ConditionalWeakTable` for layered lifecycle; `AutomaticRecoveryEnabled = false` enforced

### Critical Pitfalls

1. **Lost wake-up in waiter queue (deadlock under burst-then-drain)** — use `Channel<TaskCompletionSource<PoolEntry<T>>>` with direct-handoff pattern; never split the free-list and waiter-list across two independent synchronization primitives. Test with `MaxSize=1` ping-pong stress test + watchdog timeout that fails if any `AcquireAsync` exceeds 5 s. Phase: P1 (foundational, unrecoverable if wrong).

2. **CancellationToken missing in hook signatures** — all five hook delegates must accept `CancellationToken` from day one; this is a breaking API change if added later. Pool creates a linked CTS (acquire ct + pool-lifetime ct + per-factory timeout) and passes it to every hook. Phase: P1.

3. **Factory exception leaves counters inconsistent ("ghost" full pool)** — reserve capacity with `Interlocked.Increment(_total)` before calling the factory; wrap factory in try/catch that decrements `_total` on failure and rethrows. Same pattern for `BeforeUse` validation discard. Phase: P1 (basic rollback) + P2 (grow-path rollback).

4. **Health-check sweep amplifies load during downstream outage** — sweep must track rolling failure rate; when above threshold, back off exponentially (30 s → 60 s → 120 s, max 5 min); cap concurrent factory creation with `SemaphoreSlim(maxConcurrentCreations)` to prevent connection storms. Phase: P2.

5. **RabbitMQ autorecovery vs. pool discard — double-management** — `AddElasticConnectionPool` must set `ConnectionFactory.AutomaticRecoveryEnabled = false` and document why; let the pool's failure policy own connection lifecycle, not the client library's recovery. Phase: P4.

6. **Elastic oscillation (grow/shrink thrash)** — require N consecutive sweep windows of low utilization before shrinking (hysteresis); shrink one item per sweep tick; expose tuning knobs (`ShrinkBackoffWindows`, `ShrinkBatchSize`). Phase: P2.

7. **Meter/ActivitySource lifetime mismatch** — use `IMeterFactory` from DI for `Meter` lifetime (tied to DI scope, handles dispose); declare `ActivitySource` as `internal static readonly` per assembly (process lifetime, never disposed explicitly). Phase: P3.

8. **`IAsyncDisposable` not called by non-host DI** — implement both `IDisposable` (best-effort sync drain with hard timeout) and `IAsyncDisposable` (graceful drain); document that standard Generic Host calls `DisposeAsync` automatically. Phase: P1.

## Implications for Roadmap

Based on research, the architecture file's suggested build order is strongly validated across all four research dimensions. The pitfall mapping directly anchors phases: all P1 pitfalls (waiter-queue design, CancellationToken in hooks, counter rollback, ValueTask contract, ABA avoidance, dual dispose) are foundational decisions that cannot be retrofitted without breaking changes. This confirms a phase structure that front-loads correctness before differentiators.

### Phase 1: Core Skeleton — Fixed-Size Pool

**Rationale:** Validates API surface end-to-end before elasticity complexity is added. All foundational pitfalls live here. Ship a working, tested pool at `MaxSize`-only before adding any background machinery. Anything broken in the API surface is cheap to fix here, expensive after Phase 2.

**Delivers:**
- Public contracts: `IElasticPool<T>`, `IPoolItem<T>`, `IItemFailurePolicy<T>`, `HealthCheckResult`
- Builder: `ElasticObjectPoolFactory`, `ElasticPoolBuilder<T>`, `ElasticPoolOptions<T>`
- Engine: `ElasticPool<T>` with `ConcurrentQueue<PoolEntry<T>>`, `Channel<TCS>` direct-handoff waiter queue, `Interlocked` counters
- Factory + BeforeUse + Release hooks with `CancellationToken` in every signature
- `DiscardAndReplaceFailurePolicy<T>` (default, used from Phase 1)
- DI extension `AddElasticPool<T>(...)` + `AddKeyedElasticPool<T>(...)`
- Basic `Meter` counters + `[LoggerMessage]` source-gen logging
- Both `IAsyncDisposable` and `IDisposable` on pool
- Stress test: `MaxSize=1` ping-pong + factory-throws-on-Nth-call property test

**Avoids pitfalls:** #1 (waiter design), #2 (item leak / wrapper), #3 (factory counter rollback), #4 (ABA — use BCL), #8 (dual dispose), Pitfall 14 (CT in hooks), Pitfall 18 (lock strategy), Pitfall 19 (ValueTask contract)

### Phase 2: Elasticity and Health

**Rationale:** The headline differentiator. Depends on Phase 1's proven data structures and API shape. Adding sweep/grow/shrink to a working pool is incremental. Shipping as its own phase lets Phase 1 be validated in isolation before the more complex elastic algorithm is introduced.

**Delivers:**
- `MinSize`, `InitialSize`, eager warm-up (awaitable, cancellable)
- `BackgroundSweeper`: `PeriodicTimer`-driven, `TimeProvider`-injected (deterministic tests via `FakeTimeProvider`), handles shrink pass + health-check pass in one loop
- Idle eviction with hysteresis (N consecutive low-utilization windows before shrink; one item per tick)
- `Check` hook + background health-check pass with adaptive backoff on failure storm
- `PressureSampler`: composite-signal grow (waiter-queue depth + sustained utilization + wait p95)
- `AfterUse` hook + on-return validation
- Observable `Meter` gauges (`pool.size`, `pool.available`, `pool.in_use`, `pool.pending_requests`)
- `ActivitySource` spans (Acquire, Grow, HealthCheck, Release)

**Avoids pitfalls:** #5 (sweep amplification), #6 (health-check hot-path cost), #7 (oscillation / hysteresis), #8 (slow growth / composite signal), Pitfall 21 (quarantine never recovers), Pitfall 22 (cascading failure / degraded mode)

### Phase 3: RabbitMQ Adapter

**Rationale:** Validates that Core's hook/policy abstraction is sufficient for a real, layered, lifecycle-sensitive scenario. If Core is missing anything, Phase 3 surfaces it cheaply — before any community adoption. If Phase 3 requires a new Core API, that signals a refactor while the cost is still low.

**Delivers:**
- `Oragon.ElasticPool.RabbitMQ` package
- `AddElasticConnectionPool(...)` with `AutomaticRecoveryEnabled = false` default + heartbeat validation warning
- `AddElasticChannelPool(...)` with `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` lifecycle + channel-per-connection ceiling guidance
- `ChannelLease` internal helper
- Testcontainers integration tests (including `channel_max=10` forced-low test for ceiling validation)
- Bursty publisher sample (few/hour → 100k simultaneous)

**Avoids pitfalls:** #9 (autorecovery conflict), #10 (channel sharing), #11 (channel_max ceiling), #12 (heartbeat), #13 (confirms + concurrent publishing)

### Phase 4: Polish and v1.0 Release

**Rationale:** OSS quality bar and discoverability. Standard well-understood patterns — no novel implementation risk. Must complete before first NuGet publish.

**Delivers:**
- README: quickstart, OTel integration example, anti-pattern section (shared channel warning), metric contract documentation, tuning playbook
- CI matrix: GitHub Actions, `ubuntu + windows` × `net8/net9/net10`
- MinVer + SourceLink + `snupkg` symbol packages
- `PublicAPI.Shipped.txt` baselined (both packages)
- `CHANGELOG.md` initialized
- SemVer 2.0 discipline established; `PublicApiAnalyzers` gates every PR on public surface changes
- NuGet publish (both packages)

**Avoids pitfalls:** Pitfall 15 (tag cardinality — document metric contract), Pitfall 16 (Meter lifetime — verify `IMeterFactory` path), Pitfall 17 (ActivitySource overhead — benchmark no-listener path), Pitfall 23 (SemVer violations), Pitfall 24 (symbol packages misconfigured)

### Phase Ordering Rationale

- Phase 1 must be first: all foundational pitfalls are non-retrofittable architectural decisions (waiter-queue design, CancellationToken in hook signatures, ValueTask contract, counter rollback, lock strategy). Getting these wrong means a breaking API change.
- Phase 2 depends on Phase 1's data structures but never restructures them — it adds on top. Tested in isolation with `FakeTimeProvider`-driven deterministic time before the real broker is involved.
- Phase 3 is an integration validation of Phase 2's abstractions with a real broker. Surfaces any Core gaps cheaply before community adoption.
- Phase 4 contains no implementation novelty — it is the pure quality and publication gate.

### Research Flags

Phases requiring deeper research during planning:
- **Phase 2 (Elasticity):** The composite-signal algorithm's default thresholds (`GrowThresholdWaiters`, `GrowThresholdUtilizationPercent`, `ShrinkBackoffWindows`) need empirical calibration via BenchmarkDotNet + load simulation. PITFALLS.md describes the problem space but not specific default values. Plan for a calibration milestone inside Phase 2.
- **Phase 3 (RabbitMQ Adapter):** The `ConditionalWeakTable` layered-lifecycle pattern needs stress testing under concurrent channel discard + connection pool shrink. ARCHITECTURE.md shows the solution but edge cases should be covered by explicit integration tests before considering this validated.

Phases with standard patterns (no deep research needed):
- **Phase 1 (Core Skeleton):** all patterns are established (BCL primitives, builder pattern, direct-handoff waiter queue). ARCHITECTURE.md provides near-complete implementation guidance including code sketches.
- **Phase 4 (Polish / v1.0):** entirely standard OSS hygiene. STACK.md provides exact `Directory.Build.props` configuration snippets for SourceLink, MinVer, Central Package Management, and CI matrix YAML.

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | All versions verified against NuGet.org and official docs as of May 2026. FluentAssertions v8 license issue verified (InfoQ). RabbitMQ.Client v7 API verified against official migration guide. Decision to use `object` lock targets (skip `System.Threading.Lock`) documented explicitly. |
| Features | HIGH | Cross-ecosystem reference set (HikariCP, commons-pool2, node generic-pool, MS ObjectPool, Reactor Pool) provides strong grounding. Table-stakes list matches what mature pools from multiple ecosystems converge on. Differentiator trio well-motivated by gap analysis. |
| Architecture | HIGH | All internal data structure choices (ConcurrentQueue, Channel, PeriodicTimer) are BCL-backed with documented concurrency semantics. Direct-handoff pattern validated by HikariCP's SynchronousQueue design. Layered pool composition pattern architecturally clean and lifecycle-analyzed in depth. |
| Pitfalls | HIGH (RabbitMQ) / MEDIUM (elastic defaults) | 24 pitfalls documented with phase mappings and test strategies. RabbitMQ claims verified against official docs. Elastic algorithm oscillation patterns extrapolated from HikariCP issue tracker and commons-pool2 community — implementation defaults need empirical calibration. |

**Overall confidence: HIGH**

### Gaps to Address

- **Composite-signal grow/shrink threshold defaults:** PITFALLS.md and ARCHITECTURE.md describe the algorithm shape but not recommended default values for `GrowThresholdWaiters`, `GrowThresholdUtilizationPercent`, `ShrinkBackoffWindows`. These must be determined empirically during Phase 2 with BenchmarkDotNet + load simulation. Plan for a calibration milestone inside Phase 2.
- **Channel-per-connection selection algorithm:** PITFALLS.md (Pitfall 11) identifies that the channel pool must spread channels across multiple connections when average channels-per-connection exceeds a target. ARCHITECTURE.md does not detail the exact algorithm for which connection the channel pool's Factory selects. Specify during Phase 3 planning.
- **`DrainAsync(TimeSpan)` timing:** FEATURES.md defers an explicit public `DrainAsync` API to v1.x, but PITFALLS.md (Pitfall 20) notes `DisposeAsync` alone is insufficient for k8s SIGTERM. At minimum, expose a grace-period parameter on `DisposeAsync` in v1.0 even if a separate `DrainAsync` method is deferred. Resolve during Phase 4 planning.
- **net9.0 EOL scheduling:** net9 STS EOL was May 2026. Keep it in the CI matrix for transition but plan a milestone to drop the net9.0 TFM once EOL passes to reduce matrix size. Scheduling concern, not implementation risk.

## Sources

### Primary (HIGH confidence)

- [NuGet: RabbitMQ.Client 7.2.1](https://www.nuget.org/packages/rabbitmq.client/) — version, target frameworks confirmed
- [RabbitMQ v7 Migration Guide](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/main/v7-MIGRATION.md) — IModel→IChannel, async API, AutomaticRecovery, BasicProperties
- [RabbitMQ .NET Client API Guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) — channel sharing (hard requirement not to share publishing channels), confirms, recovery
- [RabbitMQ Channels docs](https://www.rabbitmq.com/docs/channels) — channel_max default 2047, channel-per-connection guidance
- [RabbitMQ Connections docs](https://www.rabbitmq.com/docs/connections) — heartbeat, connection lifecycle
- [.NET 10 Announcement (devblogs)](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/) — LTS through Nov 2028
- [.NET Observability with OpenTelemetry (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel) — IMeterFactory lifecycle, ActivitySource patterns
- [High-performance logging (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/high-performance-logging) — [LoggerMessage] source generator
- [Central Package Management (Microsoft Learn)](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management) — Directory.Packages.props
- [Producing Packages with Source Link (devblogs)](https://devblogs.microsoft.com/dotnet/producing-packages-with-source-link/) — ContinuousIntegrationBuild, snupkg
- [NuGet: xunit.v3 3.2.2](https://www.nuget.org/packages/xunit.v3) — GA status, v2 in security-fix mode
- [Fluent Assertions v8 license change (InfoQ)](https://www.infoq.com/news/2025/01/fluent-assertions-v8-license/) — OSS disqualification confirmed
- [NuGet: MinVer 6.0.0](https://www.nuget.org/packages/minver) — tag-driven SemVer
- [Microsoft.Extensions.ObjectPool (Microsoft Learn)](https://learn.microsoft.com/en-us/aspnet/core/performance/objectpool) — fixed-size, no health checks, gap analysis baseline

### Secondary (MEDIUM confidence)

- [HikariCP GitHub](https://github.com/brettwooldridge/HikariCP) — direct-handoff pattern, SynchronousQueue design, oscillation patterns from issue tracker
- [Apache commons-pool2 GenericObjectPool API](https://commons.apache.org/proper/commons-pool/apidocs/org/apache/commons/pool2/impl/GenericObjectPool.html) — 5-hook lifecycle, evictor thread, testOnBorrow
- [node-pool / generic-pool npm](https://www.npmjs.com/package/generic-pool) — acquireTimeoutMillis, testOnBorrow, soft idle eviction
- [Reactor Pool GracefulShutdownInstrumentedPool](https://projectreactor.io/docs/pool/snapshot/api/reactor/pool/decorators/GracefulShutdownInstrumentedPool.html) — drain + grace period model
- [Polly v8 documentation](https://www.pollydocs.org/) — orthogonal resilience; composition pattern with pool documented as the recommended approach

---
*Research completed: 2026-05-03*
*Ready for roadmap: yes*
