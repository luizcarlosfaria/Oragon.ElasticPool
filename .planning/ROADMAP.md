# Roadmap: Oragon.ElasticPool

**Created:** 2026-05-03
**Granularity:** coarse (3-5 phases, 1-3 plans each)
**Core Value:** Pool genérico .NET que entrega simultaneamente elasticidade real, auto-cura via lifecycle pluggável, e DX fluente — os três pilares juntos são o produto.

## Phases

- [x] **Phase 1: Core Skeleton — Fixed-Size Pool** - Public API surface, builder, fixed-size engine with hooks, DI, basic telemetry, all unrecoverable design decisions locked in
- [ ] **Phase 2: Elasticity & Health** - Background sweeper, composite-signal grow, hysteretic shrink, health checks, full Meter/ActivitySource/ILogger telemetry, stress validation
- [x] **Phase 3: RabbitMQ Adapter** - Layered IConnection/IChannel pools validating Core abstractions against a real lifecycle-sensitive scenario, bursty publisher sample (completed 2026-05-03)
- [ ] **Phase 4: Polish & v1.0 Release** - OSS hardening, CI matrix, README + OTel example, MinVer + SourceLink + snupkg, PublicApiAnalyzers baseline, NuGet publish
- [ ] **Phase 5: Elastic Production Hardening** - Close the production gaps found in the 0-to-100 burst analysis: bounded waiters/backpressure, burst ramp-up, faster shrink-to-zero presets, return-path self-healing, and RabbitMQ tuning DX

## Phase Details

### Phase 1: Core Skeleton — Fixed-Size Pool
**Goal**: Deliver a working, fully-tested fixed-size pool with all foundational architecture decisions (hook signatures, ValueTask contract, dispose semantics, waiter-queue design, counter rollback) locked in correctly — these are non-retrofittable without breaking changes.
**Depends on**: Nothing (first phase)
**Requirements**: API-01, API-02, API-03, HOOK-01, HOOK-02, HOOK-03, HOOK-04, HOOK-05, BOUND-01, BOUND-02, FAIL-01, FAIL-02, DI-01, QUAL-01, QUAL-02, TELEM-01
**Success Criteria** (what must be TRUE):
  1. Consumer can call `services.AddElasticPool<T>(name, b => b.Factory(...).BeforeUse(...).Release(...))` and receive an `IElasticPool<T>` from DI that survives sync `Acquire()` (free item present), `await using` of `IPoolItem<T>` returning to pool, and idempotent double-dispose without leaking counters
  2. Pool with `MaxSize=1` survives 10,000-cycle ping-pong stress test with hundreds of concurrent threads — no deadlocks, no lost wake-ups, every `AcquireAsync` completes within 5s watchdog or honors its `CancellationToken`
  3. When `Factory` throws, pool's `_total` counter is correctly decremented (verified by subsequent successful `Acquire` reaching `MaxSize` capacity); `BeforeUse` returning `Unhealthy` invokes `IItemFailurePolicy<T>` and `DiscardAndReplaceFailurePolicy<T>` discards + replaces the item
  4. Pool exposes `Meter` named `"Oragon.ElasticPool"` via `IMeterFactory` with at least the basic counters (`pool.acquire.count`, `pool.factory.failures`) and is consumable by an OTel listener
  5. Pool implements both `IDisposable` and `IAsyncDisposable` with drain semantics: stops accepting new `Acquire`, waits for in-flight items, then releases all pooled items via `Release` hook
  6. Eager warm-up to `InitialSize` is awaitable and cancellable; configuration `0 ≤ Min ≤ Initial ≤ Max` is validated at `.Build()` and throws on invalid bounds
**Plans**: 2 plans
- [ ] 04-01-PLAN.md — NuGet metadata + per-package READMEs + LICENSE + CHANGELOG + icon (OSS-02, OSS-03, OSS-04 metadata half)
- [ ] 04-02-PLAN.md — CI evolution (RabbitMQ unit+integration) + release.yml + PublicAPI.Shipped freeze + final acceptance (OSS-01, OSS-03, OSS-04, OSS-05)

### Phase 2: Elasticity & Health
**Goal**: Deliver the headline differentiator — composite-signal grow, hysteretic shrink, background health sweep — on top of the proven Phase 1 engine, with full observability and deterministic test coverage via FakeTimeProvider.
**Depends on**: Phase 1
**Requirements**: ELASTIC-01, ELASTIC-02, TELEM-02, TELEM-03, QUAL-03
**Success Criteria** (what must be TRUE):
  1. Pool grows under sustained pressure when configured composite signals (waiter-queue depth + sustained utilization % over a window + acquire-wait p95) cross thresholds, respecting `MaxSize`; growth is observable via `pool.grow.count` counter and `Grow` ActivitySource span
  2. Pool shrinks idle items past `IdleTimeout` down to `MinSize` only after N consecutive low-utilization sweep windows (hysteresis cooldown since last grow) — verified via `FakeTimeProvider`-driven test asserting no shrink occurs during the cooldown window even with idle items present
  3. Background sweeper runs `Check` hook on idle items via `PeriodicTimer`; under simulated downstream outage (sweep failures), sweep backs off exponentially (30s → 60s → 120s, capped at 5 min) instead of amplifying load
  4. Stress test (centuries of threads, thousands of acquire/release cycles) covering simultaneous grow/shrink/sweep paths completes without deadlocks, starvation, or counter inconsistency; coverage includes burst → idle → burst lifecycle
  5. ActivitySource `"Oragon.ElasticPool"` emits spans for `Acquire`, `Release`, `HealthCheck`, `Grow`, `Shrink` using `HasListeners()` guard; `[LoggerMessage]` source-generated `ILogger<T>` entries fire on every state transition, factory failure, eviction, and policy decision (allocation-free verified by benchmark)
**Plans**: 2 plans
- [ ] 04-01-PLAN.md — NuGet metadata + per-package READMEs + LICENSE + CHANGELOG + icon (OSS-02, OSS-03, OSS-04 metadata half)
- [ ] 04-02-PLAN.md — CI evolution (RabbitMQ unit+integration) + release.yml + PublicAPI.Shipped freeze + final acceptance (OSS-01, OSS-03, OSS-04, OSS-05)

### Phase 3: RabbitMQ Adapter
**Goal**: Validate that Core's hook/policy abstractions are sufficient for a real, layered, lifecycle-sensitive scenario (RabbitMQ IConnection + IChannel) — surface any Core gaps cheaply before NuGet publish, deliver the motivating bursty-publisher demo.
**Depends on**: Phase 2
**Requirements**: RMQ-01, RMQ-02, RMQ-03, RMQ-04
**Success Criteria** (what must be TRUE):
  1. Consumer calls `services.AddElasticConnectionPool(name, configure)` and receives a working `IElasticPool<IConnection>` whose `BeforeUse`/`Check` hooks consult `IConnection.IsOpen`, `Release` calls `CloseAsync()`, and the underlying `ConnectionFactory.AutomaticRecoveryEnabled` is forced to `false` by default with a documented warning
  2. Consumer calls `services.AddElasticChannelPool(name, configure)` and receives a layered `IElasticPool<IChannel>` where the channel factory acquires a connection from the inner pool; channel `Dispose`/`Release` returns the borrowed connection to its pool via `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` pairing — verified by integration test forcing low `channel_max=10` and asserting connection spread
  3. Testcontainers.RabbitMq integration test reproduces the bursty cycle (few/hour → 100k simultaneous publish → idle → repeat) using the layered pool; broker observes connection growth under pressure, shrink during idle, no leaked channels or connections
  4. Sample project `samples/PublisherSample` is runnable end-to-end against a Testcontainers RabbitMQ instance, demonstrates the bursty-publisher scenario, and uses the same fluent builder/DI conventions as the sister `Oragon.RabbitMQ` library (naming, factory pattern, async-first)
  5. If any Core API gap is surfaced during adapter implementation (e.g., insufficient hook context, missing policy invocation point), it is resolved by refactoring Core BEFORE proceeding to Phase 4 — adapter does NOT add Core abstractions itself
**Plans**: 3 plans
- [x] 03-01-PLAN.md — RabbitMQ adapter project + connection pool DI extension (RMQ-01, RMQ-04 partial)
- [ ] 03-02-PLAN.md — Channel pool DI extension layered on connection pool (RMQ-02, RMQ-04)
- [ ] 03-03-PLAN.md — Unit + integration tests (Testcontainers) + BurstyPublisher sample (RMQ-01..04 empirical validation)

### Phase 4: Polish & v1.0 Release
**Goal**: Cross the OSS quality bar and ship v1.0 to NuGet.org — README that converts evaluators in 60 seconds, multi-TFM CI green, public API surface frozen via analyzer, symbol packages and SourceLink working for consumer step-into debugging.
**Depends on**: Phase 3
**Requirements**: OSS-01, OSS-02, OSS-03, OSS-04, OSS-05
**Success Criteria** (what must be TRUE):
  1. GitHub Actions CI runs the full unit + stress + integration (Testcontainers.RabbitMq) test suite across the matrix `ubuntu-latest × {net8.0, net9.0, net10.0}` and is green on `main`
  2. README contains a working quickstart copy-pastable into a fresh `dotnet new console` project, an OpenTelemetry exporter integration example wiring `Meter` and `ActivitySource` to console/OTLP, a link to the bursty publisher sample, and a comparison table vs `Microsoft.Extensions.ObjectPool`
  3. Tagging `v1.0.0` on `main` triggers MinVer-driven SemVer 2.0 build producing `Oragon.ElasticPool.1.0.0.nupkg` + `.snupkg` and `Oragon.ElasticPool.RabbitMQ.1.0.0.nupkg` + `.snupkg` published to NuGet.org with SourceLink metadata enabling step-into to GitHub source
  4. `Microsoft.CodeAnalysis.PublicApiAnalyzers` is active on both packages with `PublicAPI.Shipped.txt` baselined for v1.0 surface; any future public API change requires explicit `PublicAPI.Unshipped.txt` update or build fails
  5. Consumer following the README quickstart can install both packages from NuGet.org, write a 20-line bursty publisher, and observe pool metrics in Aspire Dashboard or any OTel collector without additional configuration
**Plans**: 2 plans
- [ ] 04-01-PLAN.md — NuGet metadata + per-package READMEs + LICENSE + CHANGELOG + icon (OSS-02, OSS-03, OSS-04 metadata half)
- [ ] 04-02-PLAN.md — CI evolution (RabbitMQ unit+integration) + release.yml + PublicAPI.Shipped freeze + final acceptance (OSS-01, OSS-03, OSS-04, OSS-05)

### Phase 5: Elastic Production Hardening
**Goal**: Make the pool credible for highly variable workloads that move from zero idle objects to large bursts and back down again without unbounded memory pressure, slow ramp-up, or misleading health guarantees.
**Depends on**: Phase 4
**Requirements**: ELASTIC-03, ELASTIC-04, FAIL-03, RMQ-05, QUAL-04
**Success Criteria** (what must be TRUE):
  1. `AcquireAsync` can reject excess waiters through a configured queue cap, preserving the current unbounded behavior by default for source compatibility.
  2. Cold bursts can ramp from zero toward the configured ceiling without relying only on one successful CAS per caller race.
  3. Idle shrink can be tuned through documented presets for "always warm", "bursty", and "shrink to zero" workloads.
  4. Return-path health is consistent across `Dispose` and `DisposeAsync`; a failed `AfterUse` verdict cannot silently return a broken item.
  5. RabbitMQ builders expose the Core elasticity/backpressure knobs and document how `MaxChannelsPerConnection` controls connection spread.
**Plans**: 5 plans
- [ ] 05-01-PLAN.md - Bounded waiter backpressure (`MaxWaiterCount`) in Core
- [ ] 05-02-PLAN.md - Burst ramp-up strategy based on waiter debt / batch grow
- [ ] 05-03-PLAN.md - Shrink-to-zero presets and documentation
- [ ] 05-04-PLAN.md - Return-path self-healing consistency
- [ ] 05-05-PLAN.md - RabbitMQ adapter elasticity DX

## Progress

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Core Skeleton — Fixed-Size Pool | 3/3 | Complete | 2026-05-03 |
| 2. Elasticity & Health | 3/3 | Complete | 2026-05-03 |
| 3. RabbitMQ Adapter | 3/3 | Complete | 2026-05-03 |
| 4. Polish & v1.0 Release | 2/2 | Complete | 2026-05-03 |
| 5. Elastic Production Hardening | 0/5 | Active | - |

## Coverage

**v1 requirements:** 30 total — 30 mapped, 0 unmapped

| Category | Count | Phase Distribution |
|----------|-------|--------------------|
| API | 3 | Phase 1 (3) |
| HOOK | 5 | Phase 1 (5) |
| BOUND | 2 | Phase 1 (2) |
| ELASTIC | 2 | Phase 2 (2) |
| FAIL | 2 | Phase 1 (2) |
| TELEM | 3 | Phase 1 (1) + Phase 2 (2) |
| DI | 1 | Phase 1 (1) |
| QUAL | 3 | Phase 1 (2) + Phase 2 (1) |
| RMQ | 4 | Phase 3 (4) |
| OSS | 5 | Phase 4 (5) |

**Phase totals:** Phase 1 = 16, Phase 2 = 5, Phase 3 = 4, Phase 4 = 5 → 30 ✓

---
*Roadmap created: 2026-05-03*
*Last updated: 2026-05-03 after initial creation*
