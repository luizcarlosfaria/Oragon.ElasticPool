---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: Release
status: verifying
stopped_at: End of Plan 03 (commits 58c0916, 481fec1, c7b6191). Self-check PASSED. 70 unit tests + 1 stress test green on net8/9/10; 92.8 % line coverage on Core.
last_updated: "2026-05-03T14:52:14.181Z"
last_activity: 2026-05-03
progress:
  total_phases: 4
  completed_phases: 1
  total_plans: 3
  completed_plans: 3
  percent: 100
---

# State: Oragon.AdaptivePool

**Last updated:** 2026-05-03

## Project Reference

**Core Value:** Pool genérico .NET que entrega simultaneamente elasticidade real (min/max com crescimento e encolhimento automáticos), auto-cura (detecta e substitui objetos quebrados sem o cliente saber) e DX fluente (builder limpo, async-first, DI-first) — os três pilares juntos são o produto e nenhum pode ser sacrificado.

**Current Focus:** Phase 2 (Elasticity & Health) **IN PROGRESS**. Plan 01 (components + builder extensions scaffold) shipped green: 5 internal sealed components wired into AdaptivePool<T>, 8 new options properties, 7 new builder fluent methods, PingPongStressTest still passes on all 3 TFMs. Plan 02 (engine grow/shrink + telemetry) is next.

## Current Position

**Phase:** 2 - Elasticity & Health (IN PROGRESS)
**Plan:** 1 of 3 (Plan 01 complete: components + builder + engine wiring)
**Status:** Plan 01 complete — Plan 02 next
**Last Activity:** 2026-05-02
**Progress:** [███▏······] 33% (Phase 2)

## Performance Metrics

| Metric | Value |
|--------|-------|
| Phases complete | 1/4 |
| Plans complete | 3/3 (Phase 1) |
| Requirements mapped | 30/30 |
| Requirements validated | 16/30 (all Phase 1 — API/HOOK/BOUND/FAIL/DI/QUAL/TELEM) |

| Phase | Plan | Duration | Tasks | Files | Commits |
|-------|------|----------|-------|-------|---------|
| 1 | 01 | 10m37s | 3 | 14 | 3 |
| 1 | 02 | 14m22s | 3 | 22 | 3 |
| 1 | 03 | ~22m   | 3 | 21 | 3 |
| 2 | 01 | ~7m    | 3 | 11 | 3 |

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
- [Phase 1 Plan 03]: 70 unit tests across 13 files + 1 stress test (`MaxSize=1` 256-thread × 40-iter ping-pong, ~300 ms runtime) + CI coverage gate at 90 % line coverage on `Oragon.AdaptivePool.Core` (achieved 92.8 %). Coverage gate uses coverlet.console wrapped over `dotnet <testdll>` (MTP runner does not honor `dotnet test --collect:"XPlat Code Coverage"` — Plan-sanctioned alternative path). xUnit1051 NoWarn at test-csproj level. Stress project remains EXCLUDED from CI default per CONTEXT.md.

### Open Todos

- Phase 2 plan creation: Elasticity + Sweeper. Composite-signal grow/shrink (waiter-count + utilization + back-off windows), background `PeriodicTimer` sweep using Check hook, `pool.size.idle` / `pool.grow.count` / `pool.shrink.count` / `pool.health.checks` counters. Empirical calibration of thresholds via BenchmarkDotNet (research flag).
- Phase 4 follow-up: nightly Stress job in CI (currently manual-only); SourceLink "no remote" warnings will auto-resolve in actual CI runs.

### Blockers

- None

### Research Flags (deferred to phase planning)

- **Phase 2:** Composite-signal threshold defaults (`GrowThresholdWaiters`, `GrowThresholdUtilizationPercent`, `ShrinkBackoffWindows`) need empirical calibration via BenchmarkDotNet + load simulation
- **Phase 3:** `ConditionalWeakTable` channel-per-connection selection algorithm needs detailed spec; concurrent channel discard + connection pool shrink edge cases need explicit integration tests
- **Phase 4:** Decide whether v1.0 ships a grace-period parameter on `DisposeAsync` (k8s SIGTERM concern from PITFALLS.md #20) even if `DrainAsync(TimeSpan)` is deferred to v1.x
- **Phase 4:** Plan a future milestone to drop `net9.0` TFM after STS EOL (was May 2026 — kept for transition only)

## Session Continuity

**Last session:** 2026-05-03 — completed Phase 1 Plan 03 (Tests + Stress + Coverage Gate). Phase 1 closed.
**Stopped at:** End of Plan 03 (commits 58c0916, 481fec1, c7b6191). Self-check PASSED. 70 unit tests + 1 stress test green on net8/9/10; 92.8 % line coverage on Core.
**Resume file:** `.planning/phases/01-core-skeleton-fixed-size-pool/03-SUMMARY.md`
**Next action:** Plan Phase 2 (Elasticity + Sweeper) — see Open Todos.

**Files in `.planning/`:**

- `PROJECT.md` — vision, core value, constraints, key decisions
- `REQUIREMENTS.md` — 30 v1 requirements, traceability table
- `ROADMAP.md` — 4 phases with success criteria, coverage map
- `STATE.md` — this file
- `config.json` — `granularity: coarse`, mode: yolo
- `research/SUMMARY.md` + `STACK.md` + `FEATURES.md` + `ARCHITECTURE.md` + `PITFALLS.md`

---
*State initialized: 2026-05-03 after roadmap creation*
