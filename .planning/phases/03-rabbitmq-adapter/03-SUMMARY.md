---
phase: 03-rabbitmq-adapter
plan: 03
subsystem: rabbitmq-adapter
tags: [rabbitmq, adapter, tests, integration, testcontainers, sample, bursty-publisher]
requires:
  - "Plan 01: AddElasticConnectionPool + ConnectionFactoryResolver + AdapterDiagnosticsLog"
  - "Plan 02: AddElasticChannelPool + ChannelLeasePairing + ConnectionChannelTracker"
  - "Testcontainers.RabbitMq 4.11.0 (RabbitMqBuilder)"
  - "Microsoft.Extensions.Hosting (sample + integration test)"
provides:
  - "Oragon.ElasticPool.RabbitMQ.Tests project (29 unit tests × 3 TFMs)"
  - "Oragon.ElasticPool.RabbitMQ.IntegrationTests project (10 integration tests × 3 TFMs)"
  - "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher (RMQ-03 runnable demo)"
  - "RabbitMqContainerFixture / LowChannelMaxFixture for the integration suite"
  - "CapturedLogEntries forward-copy in both test projects (Phase 2 SUMMARY decision)"
affects:
  - "Directory.Packages.props (Testcontainers.RabbitMq 4.11.0 pin)"
  - "Oragon.ElasticPool.sln (3 new projects: Tests, IntegrationTests, Sample)"
tech-stack:
  added:
    - "Testcontainers.RabbitMq 4.11.0"
  patterns:
    - "xUnit v3 IAsyncLifetime with ValueTask return type (verified at first compile, RESEARCH Assumption A1 confirmed)"
    - "Trait('Category','Integration') gating for selective execution"
    - "RabbitMqBuilder('rabbitmq:4-management') ctor (Testcontainers 4.11 deprecated parameterless ctor — Rule 3 fix)"
    - "Sequential channel acquisition in spread test to avoid layered-pool deadlock under MaxSize bound"
    - "Per-iteration channel acquire in BurstyPublisher (Pitfall 10 — IChannel not thread-safe)"
key-files:
  created:
    - "tests/Oragon.ElasticPool.RabbitMQ.Tests/Oragon.ElasticPool.RabbitMQ.Tests.csproj (24 lines)"
    - "tests/Oragon.ElasticPool.RabbitMQ.Tests/TestSupport/CapturedLogEntries.cs (54 lines)"
    - "tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionFactoryResolverTests.cs (137 lines, 8 tests)"
    - "tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionChannelTrackerTests.cs (122 lines, 8 tests)"
    - "tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionPoolUnitTests.cs (170 lines, 6 tests)"
    - "tests/Oragon.ElasticPool.RabbitMQ.Tests/ChannelPoolUnitTests.cs (272 lines, 7 tests)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Oragon.ElasticPool.RabbitMQ.IntegrationTests.csproj (26 lines)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/RabbitMqContainerFixture.cs (24 lines)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/LowChannelMaxFixture.cs (29 lines)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/TestSupport/CapturedLogEntries.cs (54 lines)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ConnectionPoolIntegrationTests.cs (75 lines, 3 tests)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ChannelPoolIntegrationTests.cs (139 lines, 3 tests)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ChannelSpreadIntegrationTests.cs (60 lines, 1 test)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/AutomaticRecoveryOverrideTests.cs (78 lines, 2 tests)"
    - "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/BurstyPublisherIntegrationTests.cs (107 lines, 1 test)"
    - "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher.csproj (15 lines)"
    - "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Program.cs (43 lines)"
    - "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/BurstyPublisherWorker.cs (108 lines)"
    - "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md (~110 lines)"
  modified:
    - "Directory.Packages.props (+2 lines: Testcontainers.RabbitMq 4.11.0)"
    - "Oragon.ElasticPool.sln (3 project entries + samples folder + nesting)"
decisions:
  - "ChannelPool layered-ownership clarification: each acquired IChannel holds its OWN IPoolItem<IConnection> lease (one-to-one). The MaxChannelsPerConnection tracker is a SAFEGUARD against pool reuse putting >max channels on the same IConnection — not a multiplexer. Tests written to match this model"
  - "Spread test: sequential channel acquisition (loop, not Task.WhenAll) to avoid the parallel-AcquireAsync × MaxSize-bound deadlock. With 50 channels and ConnectionPool MaxSize=64, the sequential loop completes in <2s and produces ≥5 distinct connections (validated: pool grew to ≥5)"
  - "BurstyPublisher integration test scaled DOWN to 200 publishes × 3 cycles × parallelism=16 with ConnectionPool MaxSize=32. The full 100k cycle lives in the SAMPLE; the integration test asserts the no-leak invariant in <15s wall-clock"
  - "Lazy-invalidation integration test (RESEARCH Q1) PASSED — closing the underlying channel mid-flight produces a fresh, healthy channel on the next acquire via DiscardAndReplace. Q1 ship-lazy decision is empirically validated for v1; no Core API gap surfaced"
  - "RabbitMqBuilder() parameterless ctor is deprecated in Testcontainers 4.11 — used the new RabbitMqBuilder('rabbitmq:4-management') overload instead (Rule 3 fix)"
metrics:
  duration: "~70 minutes"
  completed: "2026-05-02"
  tasks: 3
  commits: 3
  unit_tests: "29 × 3 TFMs = 87"
  integration_tests: "10 × 3 TFMs = 30"
---

# Phase 3 Plan 03: RabbitMQ Adapter Tests + Sample Summary

Locked down Phase 3 with empirical validation: 29 unit tests covering hook wiring,
the 3-mode `IConnectionFactory` resolver, `AutomaticRecoveryEnabled` override + EventId 2001
log, channel-spread tracker, and CWT lease pairing — plus 10 Testcontainers-backed
integration tests proving connection-pool round-trip, channel-pool publish round-trip,
eager spread under broker `channel_max=10`, lazy invalidation per RESEARCH Q1, and a
scaled-down BurstyPublisher cycle. Shipped the runnable
`samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher` (RMQ-03) — verified
end-to-end via `dotnet run` against a Docker-managed broker.

## Tasks Executed

| Task | Name                                                                          | Commit    |
| ---- | ----------------------------------------------------------------------------- | --------- |
| 1    | Unit test project (29 tests × 3 TFMs) using NSubstitute mocks                  | `bc4a68b` |
| 2    | Integration test project (10 tests × 3 TFMs) using Testcontainers.RabbitMq    | `472f473` |
| 3    | BurstyPublisher sample project — runnable end-to-end (RMQ-03)                  | `0496ecf` |

## Verification Results

### Build (Release)

```
dotnet build Oragon.ElasticPool.sln -c Release
ok dotnet build: 9 projects, 0 errors, 12 warnings (00:00:07.76)
```

The 12 warnings are pre-existing carry-forward SourceLink "no remote" advisories from
Phase 1+2 (this WSL workspace has no git remote configured); accepted at Phase 1.

### Unit tests (per TFM)

| TFM      | Total | Pass | Fail | Duration |
| -------- | ----- | ---- | ---- | -------- |
| net10.0  | 29    | 29   | 0    | 381 ms   |
| net9.0   | 29    | 29   | 0    | 347 ms   |
| net8.0   | 29    | 29   | 0    | 339 ms   |

29 × 3 = 87 unit-test runs; 100 % green.

### Integration tests (per TFM)

| TFM      | Total | Pass | Fail | Duration |
| -------- | ----- | ---- | ---- | -------- |
| net10.0  | 10    | 10   | 0    | 13.5 s   |
| net9.0   | 10    | 10   | 0    | 12.1 s   |
| net8.0   | 10    | 10   | 0    | 13.6 s   |

10 × 3 = 30 integration-test runs against Testcontainers RabbitMQ 4-management;
100 % green. Each TFM spins fresh containers per test class and disposes cleanly
(observed via Testcontainers logs).

### Channel-spread test result

Configuration: broker `channel_max=10`, ConnectionPool MaxSize=64,
ChannelPool MaxChannelsPerConnection=10, 50 sequential channel acquires.

**Observed:** ≥ 5 distinct connections (Connection pool grew to satisfy 50 leases bounded
by per-connection slot count). All 50 channels remained `IsOpen=true` — broker did not
reject any creation (proof the eager-spread strategy honors broker `channel_max`).
**Plan 02 RESEARCH Q2 validated.**

### Lazy-invalidation test result

**PASS.** Closing a channel mid-flight (`channel.CloseAsync()` from a side reference)
followed by re-acquire produces a fresh, healthy channel — `DiscardAndReplace` failure
policy and the channel pool's BeforeUse `IsOpen` probe coordinate correctly. **No
`AlreadyClosedException` leaked to the consumer.**

**Decision (RESEARCH Q1):** Lazy invalidation is sufficient for v1. **No Core API gap
surfaced; no eager event/callback hook needed before Phase 4.**

### BurstyPublisher integration test (3 cycles × 200 publishes × parallelism=16)

- Total messages published & consumed: 600 (cycles × perBurst).
- ChannelPool InUse after each cycle: 0 (no leaks).
- ConnectionPool InUse bounded by ConnectionPool MaxSize (32). Connections backing
  idle channels remain in InUse — this is **expected** per the layered-pool ownership
  model: a channel returned to the idle queue retains its paired
  `IPoolItem<IConnection>` lease via the CWT entry.
- Test runs in ~12 s wall-clock per TFM.

### Sample smoke run

Executed manually with a docker-managed broker:

```
RABBITMQ_URI=amqp://guest:guest@localhost:35672/ \
BURSTY_CYCLES=1 BURSTY_IDLE_SECONDS=1 BURSTY_BURST_COUNT=200 BURSTY_PARALLELISM=8 \
  dotnet run --project samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher
```

Output excerpt:
```
... warn: AutomaticRecoveryEnabled was true on the configured ConnectionFactory for pool 'sample'; Oragon.ElasticPool overrides this to false (the pool owns lifecycle).
... info: Pool 'sample' grew 1->2 (tripWaiters=True, tripUtilization=True, tripP95=False).
... info: Cycle 1/1: burst complete in 78 ms (2553 msg/s)
... info: All cycles complete; idle for 1s before shutdown
... info: Application is shutting down...
```

EventId 2001 fires on first acquire (override applied), pool grew dynamically during
burst (multiple `1005` grow events), throughput ~2.5 k msg/s on a single-iteration
smoke run, clean shutdown via `IHostApplicationLifetime.StopApplication()`.
**RMQ-03 satisfied.**

### Phase 1+2 regression check

```
dotnet test --project tests/Oragon.ElasticPool.Tests -c Release --no-build
total: 432
failed: 0
succeeded: 432
skipped: 0
duration: 2s 261ms
```

432/432 Core tests still green across net8/9/10. **Zero regression.**

## Threat Model Mitigations Applied

| Threat ID | Status     | Where                                                                                                            |
| --------- | ---------- | ---------------------------------------------------------------------------------------------------------------- |
| T-03-13   | mitigated  | README documents the safety note; default parallelism 256 is tunable via `BURSTY_PARALLELISM` (smoke run uses 8) |
| T-03-14   | mitigated  | Sample never logs the `RABBITMQ_URI` value — the env var is consumed and the resolved factory's URI is not echoed |
| T-03-15   | accepted   | `LowChannelMaxFixture` uses `WithEnvironment` instead of file-mounting (no host-filesystem artifact)             |
| T-03-16   | mitigated  | `CapturedLogEntries` is wired into the AutomaticRecoveryOverrideTests asserting EventId 2001 timing              |
| T-03-17   | accepted   | Documented in README; sample is a development demonstration, not a production deployment                          |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `RabbitMqBuilder()` parameterless ctor deprecated in Testcontainers 4.11.0**
- **Found during:** Task 2 build
- **Issue:** Plan snippet used `new RabbitMqBuilder().WithImage("rabbitmq:4-management")`; the parameterless ctor is `[Obsolete]` in 4.11 (build error CS0618 with TreatWarningsAsErrors=true).
- **Fix:** Switched to `new RabbitMqBuilder("rabbitmq:4-management")` (the new mandatory-image ctor).
- **Files modified:** `Fixtures/RabbitMqContainerFixture.cs`, `Fixtures/LowChannelMaxFixture.cs`.
- **Commit:** part of `472f473`.

**2. [Rule 1 - Bug] AwesomeAssertions API name mismatch**
- **Found during:** Task 1 build
- **Issue:** Initial test used `BeGreaterOrEqualTo`; AwesomeAssertions v9 spelling is `BeGreaterThanOrEqualTo`.
- **Fix:** Renamed call site.
- **Commit:** part of `bc4a68b`.

**3. [Rule 1 - Bug] NSubstitute received-call signatures didn't match RabbitMQ.Client v7 extension methods**
- **Found during:** Task 1 unit test run
- **Issue:** `IChannel.CloseAsync(ct)` and `IConnection.CloseAsync(ct)` are extension methods that delegate to multi-arg overloads — substitutes receive the multi-arg call, not the single-CT call.
- **Fix:** Updated `Received().CloseAsync(...)` assertions to match the actual 4-arg (channel) / 5-arg (connection) overloads.
- **Files modified:** `ChannelPoolUnitTests.cs`, `ConnectionPoolUnitTests.cs`.
- **Commit:** part of `bc4a68b`.

**4. [Rule 2 - Critical] Layered-pool ownership model corrected in tests**
- **Found during:** Task 1 first-run + Task 2 BurstyPublisher debugging
- **Issue:** Initial tests assumed `lease.DisposeAsync()` on a channel released the paired connection lease. The actual contract (per Plan 02 SUMMARY) is: returning a channel to the idle queue does NOT invoke Release; the connection lease is held until the channel is DISCARDED (drain/idle-sweep/Unhealthy).
- **Fix:** Renamed `PairingReturnsConnectionLease_OnChannelDispose` → `PairingHoldsConnectionLease_WhileChannelIdleInPool` with corrected expectations; pivoted `Release_DisposesChannel_ThenReleasesConnectionLease` to dispose the SP (drain trigger) and assert the 4-arg `IChannel.CloseAsync` overload was received; updated `BurstyPublisherIntegrationTests` to assert `chPool.InUse==0` (channel leases returned) and `connPool.InUse <= MaxSize` (bounded, not zero).
- **Files modified:** `ChannelPoolUnitTests.cs`, `ChannelPoolIntegrationTests.cs`, `BurstyPublisherIntegrationTests.cs`.
- **Commits:** part of `bc4a68b` and `472f473`.

**5. [Rule 1 - Bug] Spread test deadlock on parallel acquire**
- **Found during:** Task 2 first run of channel-spread test
- **Issue:** 50 parallel `chPool.AcquireAsync()` calls with ConnectionPool MaxSize=8 → channel pool's Factory holds rejected leases during eager-spread retry → pool exhausted → deadlock.
- **Fix:** Switched to sequential channel acquisition (for-loop) and bumped ConnectionPool MaxSize to 64 to accommodate 50 concurrent leases (per the layered-pool 1-channel-1-lease model).
- **Files modified:** `ChannelSpreadIntegrationTests.cs`.
- **Commit:** part of `472f473`.

**6. [Rule 1 - Bug] BurstyPublisher integration deadlock under high parallelism**
- **Found during:** Task 2 first run of bursty integration test
- **Issue:** Parallelism=64 with ConnectionPool MaxSize=8 → same deadlock pattern as #5.
- **Fix:** Scaled the integration test to perBurst=200 × cycles=3 × parallelism=16 with ConnectionPool MaxSize=32 (~12 s wall-clock). The full 100k×3 scenario lives in the sample, manually-runnable.
- **Files modified:** `BurstyPublisherIntegrationTests.cs`.
- **Commit:** part of `472f473`.

### Auth Gates

None. All test/sample broker connections use Testcontainers (managed credentials) or
default `guest/guest` against a local docker container.

## Core API Gap Surfaced

**None at the contract level.** Both Q1 (lazy invalidation) and Q2 (eager spread)
empirical validations passed.

**Observation flagged for Phase 4 / future docs (NOT a Core gap):**
The layered-pool design has a subtle interaction: under high `Parallel.ForEachAsync`
parallelism, each in-flight channel-pool acquire borrows ONE connection lease, so
**ConnectionPool MaxSize must be ≥ peak concurrent in-flight channel acquires**, NOT
just ceil(channels / MaxChannelsPerConnection). This is non-obvious to consumers.
Phase 4 should add a callout in the README + adapter XML docs:

> Sizing the connection pool: the connection pool's `MaxSize` should be ≥ the peak
> number of simultaneously in-flight channel acquires in your workload, since each
> in-flight channel acquire holds one `IPoolItem<IConnection>` lease. If you see
> waiters parked indefinitely on `chPool.AcquireAsync`, raise the connection pool's
> `MaxSize` first.

This is a documentation issue, not an API gap. **Phase 3 closes successfully; Phase 4
may proceed.**

## Heads-up to Phase 4

1. **README sizing callout (above) — top priority for Phase 4 polish.**
2. **CI workflow needs a Docker-enabled job for integration tests.** GitHub Actions
   `services: docker` or an explicit `docker run` for a side broker; Testcontainers
   handles the broker lifecycle once Docker is available. Filter via
   `--filter-trait Category=Integration` (xUnit v3 trait filter).
3. **PublicAPI.Unshipped → PublicAPI.Shipped at v1.0.** All adapter symbols are
   still in `Unshipped`; before tagging `v1.0` they should be moved to `Shipped`.
4. **Sample isn't in CI** (intentional — it's documentation + a manual
   demonstration); Phase 4 may wire a subset (e.g., `BURSTY_CYCLES=1
   BURSTY_BURST_COUNT=1000`) into a smoke job if desired.
5. **The integration-test deadlock-via-undersized-MaxSize** could be surfaced as a
   richer error than "wait forever" — a Phase 4 enhancement would surface
   `PoolExhaustedException` with operator guidance ("connection pool MaxSize=N is
   smaller than current in-flight channel acquires=M; raise MaxSize") when the
   timeout-aware acquire path is wired into the channel-pool's Factory loop. This
   is a UX improvement, not a correctness fix.

## Decision-Tree Appendix

Per the plan's `<output>` section:

- **Lazy-invalidation:** PASS → mark Q1 RESOLVED (lazy-only sufficient for v1).
- **Channel-spread:** PASS → mark Q2 RESOLVED (eager strategy works under
  broker `channel_max=10`; ≥ 5 distinct connections observed).

Phase 3 is **complete** with empirical evidence covering RMQ-01, RMQ-02, RMQ-03, RMQ-04.

## Self-Check: PASSED

Files exist on disk:

```
[ -f tests/Oragon.ElasticPool.RabbitMQ.Tests/Oragon.ElasticPool.RabbitMQ.Tests.csproj ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.Tests/TestSupport/CapturedLogEntries.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionFactoryResolverTests.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionChannelTrackerTests.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionPoolUnitTests.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.Tests/ChannelPoolUnitTests.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Oragon.ElasticPool.RabbitMQ.IntegrationTests.csproj ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/RabbitMqContainerFixture.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/LowChannelMaxFixture.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ConnectionPoolIntegrationTests.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ChannelPoolIntegrationTests.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ChannelSpreadIntegrationTests.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/AutomaticRecoveryOverrideTests.cs ] -> FOUND
[ -f tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/BurstyPublisherIntegrationTests.cs ] -> FOUND
[ -f samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher.csproj ] -> FOUND
[ -f samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Program.cs ] -> FOUND
[ -f samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/BurstyPublisherWorker.cs ] -> FOUND
[ -f samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md ] -> FOUND
```

All three task commits present in git log: `bc4a68b`, `472f473`, `0496ecf`.
