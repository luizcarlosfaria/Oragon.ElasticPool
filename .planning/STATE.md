---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: Release
status: Awaiting plan creation
last_updated: "2026-05-03T07:02:29.261Z"
progress:
  total_phases: 4
  completed_phases: 0
  total_plans: 3
  completed_plans: 2
  percent: 67
---

# State: Oragon.AdaptivePool

**Last updated:** 2026-05-03

## Project Reference

**Core Value:** Pool genérico .NET que entrega simultaneamente elasticidade real (min/max com crescimento e encolhimento automáticos), auto-cura (detecta e substitui objetos quebrados sem o cliente saber) e DX fluente (builder limpo, async-first, DI-first) — os três pilares juntos são o produto e nenhum pode ser sacrificado.

**Current Focus:** Phase 1 in progress. Plan 02 (Core Skeleton — Public API + Sealed Engine) complete; Plan 03 (Tests + Stress + Coverage) is the next executable.

## Current Position

**Phase:** 1 - Core Skeleton — Fixed-Size Pool (in progress)
**Plan:** 2 of 3
**Status:** Plan 02 complete — Plan 03 ready to execute
**Last Activity:** 2026-05-02
**Progress:** [██████░░░░] 67%

## Performance Metrics

| Metric | Value |
|--------|-------|
| Phases complete | 0/4 |
| Plans complete | 2/3 (Phase 1) |
| Requirements mapped | 30/30 |
| Requirements validated | 0/30 |

| Phase | Plan | Duration | Tasks | Files | Commits |
|-------|------|----------|-------|-------|---------|
| 1 | 01 | 10m37s | 3 | 14 | 3 |
| 1 | 02 | 14m22s | 3 | 22 | 3 |

## Accumulated Context

### Key Decisions (from PROJECT.md)

- Multi-target `net10.0;net9.0;net8.0` — covers active LTS + STS, zero polyfills required
- Two NuGet packages: `Oragon.AdaptivePool.Core` + `Oragon.AdaptivePool.RabbitMQ`
- Five-stage lifecycle hooks: `Factory` / `BeforeUse` / `Check` / `AfterUse` / `Release`
- Built-in telemetry trio: `Meter` + `ActivitySource` + `ILogger<T>` (OTel-native)
- Pluggable `IItemFailurePolicy<T>` (vs. fixed strategy)
- Composite-signal pressure detection (vs. single signal)
- BCL-centric stack: `ConcurrentQueue` + `Channel<TCS>` direct-handoff waiter + `PeriodicTimer` background sweep
- All public concrete classes sealed; extension via hooks and policy interfaces only
- OSS-first: MIT license, MinVer SemVer, SourceLink, snupkg symbol packages, PublicApiAnalyzers from day one

### Architecture Locked-In Decisions (Phase 1 Critical)

These cannot be changed without breaking API:

- `CancellationToken` parameter in every hook delegate signature
- `ValueTask` (not `Task`) return on async hooks
- `IPoolItem<T>` wrapper with both `IDisposable` and `IAsyncDisposable`
- Pool itself implements both `IDisposable` and `IAsyncDisposable`
- Counter rollback (`Interlocked.Decrement(_total)`) on Factory exception
- `Channel<TaskCompletionSource<PoolEntry<T>>>` direct-handoff for waiter queue (not split free-list/waiter-list)

### Decisions Made

- [Phase 1 Plan 01]: Repository scaffolding green-baseline (CPM, SourceLink deterministic, PublicApiAnalyzers wired, xUnit v3+MTP test/stress projects, multi-TFM CI workflow)
- [Phase 1 Plan 02]: 12 public types + sealed AdaptivePool<T> engine (Channel direct-handoff waiter, Interlocked counter rollback, dual IDisposable+IAsyncDisposable drain, eager warm-up via ReadyAsync(), IMeterFactory telemetry with Meter fallback, source-gen [LoggerMessage] logging) + DI extension `services.AddAdaptivePool<T>(name, configure)` with named-options + keyed singleton + non-keyed default-name fallback. PublicAPI.Unshipped.txt now has 75 declarations; full solution build green on net8/9/10.

### Open Todos

- Plan 03: Unit + stress test suite (one test per must_have truth from Plan 02), MaxSize=1 ping-pong stress test, MetricCollector counter assertions for `pool.acquire.count` + `pool.factory.failures`, 90% coverage gate on Core in CI, replace Plan 01 placeholder smoke test and stress fact

### Blockers

- None

### Research Flags (deferred to phase planning)

- **Phase 2:** Composite-signal threshold defaults (`GrowThresholdWaiters`, `GrowThresholdUtilizationPercent`, `ShrinkBackoffWindows`) need empirical calibration via BenchmarkDotNet + load simulation
- **Phase 3:** `ConditionalWeakTable` channel-per-connection selection algorithm needs detailed spec; concurrent channel discard + connection pool shrink edge cases need explicit integration tests
- **Phase 4:** Decide whether v1.0 ships a grace-period parameter on `DisposeAsync` (k8s SIGTERM concern from PITFALLS.md #20) even if `DrainAsync(TimeSpan)` is deferred to v1.x
- **Phase 4:** Plan a future milestone to drop `net9.0` TFM after STS EOL (was May 2026 — kept for transition only)

## Session Continuity

**Last session:** 2026-05-02 — completed Phase 1 Plan 02 (Core API surface + sealed engine + DI extension).
**Stopped at:** End of Plan 02 (commits 3c0e25f, fdb29d9, db7ae8a). Self-check PASSED.
**Resume file:** `.planning/phases/01-core-skeleton-fixed-size-pool/02-SUMMARY.md`
**Next action:** Execute Phase 1 Plan 03 (`.planning/phases/01-core-skeleton-fixed-size-pool/03-PLAN.md`) — unit + stress tests, MaxSize=1 ping-pong, MetricCollector assertions, 90% coverage gate.

**Files in `.planning/`:**

- `PROJECT.md` — vision, core value, constraints, key decisions
- `REQUIREMENTS.md` — 30 v1 requirements, traceability table
- `ROADMAP.md` — 4 phases with success criteria, coverage map
- `STATE.md` — this file
- `config.json` — `granularity: coarse`, mode: yolo
- `research/SUMMARY.md` + `STACK.md` + `FEATURES.md` + `ARCHITECTURE.md` + `PITFALLS.md`

---
*State initialized: 2026-05-03 after roadmap creation*
