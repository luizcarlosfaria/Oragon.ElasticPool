# State: Oragon.AdaptivePool

**Last updated:** 2026-05-03

## Project Reference

**Core Value:** Pool genérico .NET que entrega simultaneamente elasticidade real (min/max com crescimento e encolhimento automáticos), auto-cura (detecta e substitui objetos quebrados sem o cliente saber) e DX fluente (builder limpo, async-first, DI-first) — os três pilares juntos são o produto e nenhum pode ser sacrificado.

**Current Focus:** Roadmap initialized. Awaiting `/gsd-plan-phase 1` to begin Phase 1 planning.

## Current Position

**Phase:** 1 - Core Skeleton — Fixed-Size Pool (not started)
**Plan:** None
**Status:** Awaiting plan creation
**Progress:** [░░░░░░░░░░░░░░░░░░░░] 0% (0/4 phases complete)

## Performance Metrics

| Metric | Value |
|--------|-------|
| Phases complete | 0/4 |
| Plans complete | 0/0 |
| Requirements mapped | 30/30 |
| Requirements validated | 0/30 |

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

### Open Todos

- None yet — kicks off when Phase 1 planning begins

### Blockers

- None

### Research Flags (deferred to phase planning)

- **Phase 2:** Composite-signal threshold defaults (`GrowThresholdWaiters`, `GrowThresholdUtilizationPercent`, `ShrinkBackoffWindows`) need empirical calibration via BenchmarkDotNet + load simulation
- **Phase 3:** `ConditionalWeakTable` channel-per-connection selection algorithm needs detailed spec; concurrent channel discard + connection pool shrink edge cases need explicit integration tests
- **Phase 4:** Decide whether v1.0 ships a grace-period parameter on `DisposeAsync` (k8s SIGTERM concern from PITFALLS.md #20) even if `DrainAsync(TimeSpan)` is deferred to v1.x
- **Phase 4:** Plan a future milestone to drop `net9.0` TFM after STS EOL (was May 2026 — kept for transition only)

## Session Continuity

**Next action:** Run `/gsd-plan-phase 1` to decompose Phase 1 into executable plans.

**Files in `.planning/`:**
- `PROJECT.md` — vision, core value, constraints, key decisions
- `REQUIREMENTS.md` — 30 v1 requirements, traceability table
- `ROADMAP.md` — 4 phases with success criteria, coverage map
- `STATE.md` — this file
- `config.json` — `granularity: coarse`, mode: yolo
- `research/SUMMARY.md` + `STACK.md` + `FEATURES.md` + `ARCHITECTURE.md` + `PITFALLS.md`

---
*State initialized: 2026-05-03 after roadmap creation*
