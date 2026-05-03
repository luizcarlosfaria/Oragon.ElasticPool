---
phase: 03-rabbitmq-adapter
plan: 01
subsystem: rabbitmq-adapter
tags: [rabbitmq, adapter, di, connection-pool, builder, options]
requires:
  - "Oragon.AdaptivePool.Core ServiceCollectionExtensions.AddAdaptivePool<T>"
  - "Oragon.AdaptivePool.Core Builder.AdaptivePoolBuilder<T>"
  - "Oragon.AdaptivePool.Core Hooks (Factory/BeforeUse/Check/Release)"
provides:
  - "Oragon.AdaptivePool.RabbitMQ project (net10/9/8 multi-target, packable)"
  - "AddAdaptiveConnectionPool(name, configureFactory, configurePool) DI extension"
  - "AdaptiveConnectionPoolBuilder (WithBounds/WithIdleTimeout)"
  - "AdaptiveConnectionPoolOptions (HostName/Port/UserName/Password/VirtualHost/RequestedHeartbeat)"
  - "ConnectionFactoryResolver (3-mode probe: keyed > closure > IOptions)"
  - "AdapterDiagnosticsLog (EventId 2001 - source-gen [LoggerMessage])"
affects:
  - "Directory.Packages.props (RabbitMQ.Client 7.2.1 pinned)"
  - "Oragon.AdaptivePool.sln (new project + configuration block + nesting)"
tech-stack:
  added:
    - "RabbitMQ.Client 7.2.1"
  patterns:
    - "3-mode resolver probe (keyed singleton > closure > IOptions binding)"
    - "Source-gen [LoggerMessage] (allocation-free, EventId 2001+ adapter range)"
    - "Lifecycle ownership: AutomaticRecoveryEnabled forced false at every connection creation"
    - "Hook composition: BeforeUse/Check on IsOpen, Release calls CloseAsync then DisposeAsync (swallow close)"
key-files:
  created:
    - "src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj (31 lines)"
    - "src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt (empty - new project)"
    - "src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt (25 lines)"
    - "src/Oragon.AdaptivePool.RabbitMQ/Options/AdaptiveConnectionPoolOptions.cs (40 lines)"
    - "src/Oragon.AdaptivePool.RabbitMQ/Builder/AdaptiveConnectionPoolBuilder.cs (59 lines)"
    - "src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs (89 lines)"
    - "src/Oragon.AdaptivePool.RabbitMQ/Internals/AdapterDiagnosticsLog.cs (17 lines)"
    - "src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs (105 lines)"
  modified:
    - "Directory.Packages.props (+2 lines: RabbitMQ.Client 7.2.1 pin)"
    - "Oragon.AdaptivePool.sln (+15 lines: project entry, configuration block, nesting)"
decisions:
  - "Adapter builder restates Core defaults (MinSize=0, MaxSize=8, InitialSize=0, IdleTimeout=60s) so public DX is self-contained per RMQ-04 sister-library convention"
  - "AdaptiveConnectionPoolBuilder exposes only WithBounds and WithIdleTimeout for v1 — Core's Grow/Shrink/Sweep knobs deliberately not re-exported (consumers needing them go through Core directly)"
  - "AutomaticRecoveryEnabled override happens INSIDE the Factory delegate per acquire (not at registration) — protects against keyed-singleton mutation by another component (T-03-01 mitigation)"
  - "ConnectionFactoryResolver throws InvalidOperationException citing pool name when all 3 probe modes fail"
metrics:
  duration: "~10 minutes"
  completed: "2026-05-02"
---

# Phase 3 Plan 01: RabbitMQ Adapter Bootstrap Summary

Established the `Oragon.AdaptivePool.RabbitMQ` adapter project — multi-target net10/9/8, packable as NuGet — with `AddAdaptiveConnectionPool` DI extension that delegates to Core's `AddAdaptivePool<IConnection>` and wires Factory/BeforeUse/Check/Release hooks for RabbitMQ connections, including the canonical 3-mode `IConnectionFactory` resolver and the `AutomaticRecoveryEnabled = false` lifecycle-ownership override.

## Tasks Executed

| Task | Name | Commit |
| ---- | ---- | ------ |
| 1    | Pin RabbitMQ.Client 7.2.1, scaffold project, register in solution | `c45f961` |
| 2    | Add Options + Builder + 3-mode ConnectionFactoryResolver + diagnostics log | `b08b102` |
| 3    | Wire AddAdaptiveConnectionPool DI extension delegating to Core | `2b0738f` |

## Verification Results

### Build (Release)

```
dotnet build Oragon.AdaptivePool.sln -c Release --no-restore
ok dotnet build: 6 projects, 0 errors, 12 warnings
```

The 12 warnings are pre-existing carry-forward SourceLink "no remote" advisories from Phase 1+2 (this WSL workspace has no git remote configured); they are not regressions and were accepted at Phase 1.

Per-TFM artifacts confirmed:

```
src/Oragon.AdaptivePool.RabbitMQ/bin/Release/net8.0/Oragon.AdaptivePool.RabbitMQ.dll  (16.5K)
src/Oragon.AdaptivePool.RabbitMQ/bin/Release/net9.0/Oragon.AdaptivePool.RabbitMQ.dll  (16.5K)
src/Oragon.AdaptivePool.RabbitMQ/bin/Release/net10.0/Oragon.AdaptivePool.RabbitMQ.dll (16.5K)
```

### PublicApiAnalyzers

Zero `RS0016`/`RS0017` after declaring all 22 public symbol entries in `PublicAPI.Unshipped.txt` (1 type + builder ctor + 4 properties + 2 methods for `AdaptiveConnectionPoolBuilder`; 1 type + ctor + 12 property accessors for `AdaptiveConnectionPoolOptions`; 1 type + extension method for the DI extensions class).

### 3-mode probe verified by code grep

```
$ grep -c "GetKeyedService<IConnectionFactory>" src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs
1
$ grep -n "AutomaticRecoveryEnabled = false" src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs
86:            cf.AutomaticRecoveryEnabled = false;
$ grep -c "AddAdaptivePool<IConnection>" src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs
1
$ grep -c "ForceAutomaticRecoveryDisabled" src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs
1
```

All three probe modes are implemented in the documented order; `ForceAutomaticRecoveryDisabled` is invoked inside the `Factory` delegate so it runs at every connection creation.

### Phase 1+2 regression check

```
dotnet test --project tests/Oragon.AdaptivePool.Core.Tests/Oragon.AdaptivePool.Core.Tests.csproj -c Release --no-build
total: 432
failed: 0
succeeded: 432
skipped: 0
```

432 unit tests still pass across net8/net9/net10 — zero regression introduced by Plan 01.

## Core API Gap Surfaced

**None.** Core's `AddAdaptivePool<T>(name, configure)` and the `AdaptivePoolBuilder<T>` fluent surface (`Factory`/`BeforeUse`/`Check`/`Release`/`WithBounds`/`IdleTimeout`) covered everything Plan 01 needed to compose the RabbitMQ adapter. The `IPoolItem<T>.Value` accessor matched the Phase 1 implementation (the `ARCHITECTURE.md` `Object` mention was indeed stale, as flagged in carry-forward — code never references it). No blocker filed; Plan 02 may proceed.

## Public Surface Established

**Public types (3):**
- `Oragon.AdaptivePool.RabbitMQ.Builder.AdaptiveConnectionPoolBuilder` — pool-shape fluent builder.
- `Oragon.AdaptivePool.RabbitMQ.Options.AdaptiveConnectionPoolOptions` — IOptions-bindable connection settings (with `Password` security note).
- `Oragon.AdaptivePool.RabbitMQ.DependencyInjection.AdaptiveConnectionPoolServiceCollectionExtensions` — hosts `AddAdaptiveConnectionPool` extension.

**Internal types (2):**
- `Oragon.AdaptivePool.RabbitMQ.Internals.ConnectionFactoryResolver` — 3-mode probe + `ForceAutomaticRecoveryDisabled` helper.
- `Oragon.AdaptivePool.RabbitMQ.Internals.AdapterDiagnosticsLog` — source-gen `[LoggerMessage]` partial; EventId 2001 reserved for the recovery-override warning. EventId range 2001+ is reserved for adapter diagnostics (Core uses 1xxx).

## Hook Behaviors Wired

| Hook | Behavior |
|------|----------|
| `Factory` | Resolve `IConnectionFactory` via 3-mode probe; invoke `ForceAutomaticRecoveryDisabled` (logs Warning EventId=2001 if override applied); call `factory.CreateConnectionAsync(ct)`. |
| `BeforeUse` | Returns `Healthy` when `IConnection.IsOpen`; `Unhealthy` otherwise (cheap, no server roundtrip). |
| `Check` | Same as `BeforeUse` for now; the Phase 2 sweeper consumes `Check` for background probing. |
| `Release` | `CloseAsync(ct)` (try/catch swallows); then `DisposeAsync()` unconditionally. |

## Threat Model Mitigations Applied

| Threat ID | Status | Where |
|-----------|--------|-------|
| T-03-01 (Tampering) | mitigated | `ForceAutomaticRecoveryDisabled` runs INSIDE the `Factory` delegate per acquire — survives keyed-singleton mutation. |
| T-03-03 (Info disclosure / password) | mitigated | XML doc on `AdaptiveConnectionPoolOptions.Password` directs to secret stores; password is never written to any log statement. |
| T-03-04 (DoS / unreachable broker) | mitigated | Hook signature accepts `CancellationToken`; `factory.CreateConnectionAsync(ct)` propagates the consumer's CT. |
| T-03-06 (Repudiation) | mitigated | Source-gen log entry with stable EventId=2001 enables consumer filtering. |

## Deviations from Plan

**None.** Plan 01 executed exactly as written. Auto mode was active; no checkpoints required.

## Heads-up to Plan 02

The connection pool is wired but **untested by code** — Plan 03 Task 1 will add the unit tests with NSubstitute mocks; Plan 03 Task 2 will run integration tests against Testcontainers RabbitMQ. **Plan 02 (channel pool) layers on top of this connection pool**; Plan 02's author should not be surprised if Task 1 of Plan 03 surfaces wiring bugs that cascade. If a wiring fix is needed mid-Plan 02, prefer to file it as a Rule 1 deviation in Plan 02's SUMMARY rather than re-opening Plan 01.

The 3-mode probe is the canonical pattern — Plan 02's channel-pool DI extension should also resolve its `IConnectionFactory` (or, more likely, its parent connection pool) via the same probe ordering for consistency.

## Self-Check: PASSED

Files exist on disk:

```
[ -f src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj ] -> FOUND
[ -f src/Oragon.AdaptivePool.RabbitMQ/Options/AdaptiveConnectionPoolOptions.cs ] -> FOUND
[ -f src/Oragon.AdaptivePool.RabbitMQ/Builder/AdaptiveConnectionPoolBuilder.cs ] -> FOUND
[ -f src/Oragon.AdaptivePool.RabbitMQ/Internals/ConnectionFactoryResolver.cs ] -> FOUND
[ -f src/Oragon.AdaptivePool.RabbitMQ/Internals/AdapterDiagnosticsLog.cs ] -> FOUND
[ -f src/Oragon.AdaptivePool.RabbitMQ/DependencyInjection/AdaptiveConnectionPoolServiceCollectionExtensions.cs ] -> FOUND
[ -f src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt ] -> FOUND
[ -f src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt ] -> FOUND
```

All three task commits present in git log: `c45f961`, `b08b102`, `2b0738f`.
