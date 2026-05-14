---
phase: 03-rabbitmq-adapter
plan: 03
type: execute
wave: 3
depends_on: ["03-01", "03-02"]
files_modified:
  - Oragon.ElasticPool.sln
  - tests/Oragon.ElasticPool.RabbitMQ.Tests/Oragon.ElasticPool.RabbitMQ.Tests.csproj
  - tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionPoolUnitTests.cs
  - tests/Oragon.ElasticPool.RabbitMQ.Tests/ChannelPoolUnitTests.cs
  - tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionFactoryResolverTests.cs
  - tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionChannelTrackerTests.cs
  - tests/Oragon.ElasticPool.RabbitMQ.Tests/TestSupport/CapturedLogEntries.cs
  - tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Oragon.ElasticPool.RabbitMQ.IntegrationTests.csproj
  - tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/RabbitMqContainerFixture.cs
  - tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/LowChannelMaxFixture.cs
  - tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ConnectionPoolIntegrationTests.cs
  - tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ChannelPoolIntegrationTests.cs
  - tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ChannelSpreadIntegrationTests.cs
  - tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/AutomaticRecoveryOverrideTests.cs
  - tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/BurstyPublisherIntegrationTests.cs
  - samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher.csproj
  - samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Program.cs
  - samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/BurstyPublisherWorker.cs
  - samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md
  - Directory.Packages.props
autonomous: true
requirements: [RMQ-01, RMQ-02, RMQ-03, RMQ-04]
must_haves:
  truths:
    - "Unit tests cover the connection pool DI extension behavior (factory probe, AutomaticRecoveryEnabled override + log, BeforeUse/Release wiring) using NSubstitute mocks — no broker required."
    - "Unit tests cover the channel pool DI extension behavior (layered Factory acquires from connection pool, ConditionalWeakTable pairing roundtrip, eager-spread retry, BeforeUse double-IsOpen check, Release cleanup order)."
    - "Integration test (Testcontainers.RabbitMq 4.x) verifies: connection pool basic acquire/release against real broker."
    - "Integration test verifies: channel pool acquires real IChannel from layered connection pool, publish round-trip works."
    - "Integration test verifies: forced `channel_max=10` produces connection pool growth (at least 2 distinct connections used) when 50 channels are simultaneously acquired."
    - "Integration test verifies: when a connection is forcibly closed mid-pool, subsequent BeforeUse marks affected channels Unhealthy and the failure policy replaces them (no AlreadyClosedException leaks to consumer)."
    - "Integration test verifies: BurstyPublisher cycle (idle → burst → idle) completes without leaked channels/connections; pool size grows during burst, shrinks during idle, regrows on next burst."
    - "Sample project `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher` is runnable end-to-end via `dotnet run --project samples/...` against a Docker-managed RabbitMQ 4 broker."
    - "All Phase 1+2 Core tests still pass after this plan completes (no regression)."
  artifacts:
    - path: "tests/Oragon.ElasticPool.RabbitMQ.Tests/Oragon.ElasticPool.RabbitMQ.Tests.csproj"
      provides: "Unit test project, multi-target net10/9/8, NSubstitute mocks of IConnection/IChannel"
      contains: "<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>"
    - path: "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Oragon.ElasticPool.RabbitMQ.IntegrationTests.csproj"
      provides: "Integration test project, multi-target, Testcontainers.RabbitMq fixture"
      contains: "Testcontainers.RabbitMq"
    - path: "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/RabbitMqContainerFixture.cs"
      provides: "IClassFixture wrapping RabbitMqBuilder().WithImage(\"rabbitmq:4-management\")"
    - path: "tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/LowChannelMaxFixture.cs"
      provides: "Variant fixture with channel_max=10 for spread test (sets RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS or rabbitmq.conf override)"
    - path: "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Program.cs"
      provides: "Generic Host setup with AddElasticConnectionPool + AddElasticChannelPool + AddHostedService<BurstyPublisherWorker>"
    - path: "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/BurstyPublisherWorker.cs"
      provides: "BackgroundService cycling 5min idle → 30s burst → 5min idle, 3 cycles"
    - path: "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md"
      provides: "Quick start: docker prerequisite, dotnet run command, expected log output"
  key_links:
    - from: "tests/...IntegrationTests/Fixtures/RabbitMqContainerFixture.cs"
      to: "Testcontainers.RabbitMq RabbitMqBuilder"
      via: "WithImage(\"rabbitmq:4-management\").Build() + IAsyncLifetime"
      pattern: "RabbitMqBuilder"
    - from: "BurstyPublisherWorker"
      to: "IElasticPool<IChannel>"
      via: "[FromKeyedServices(\"sample\")] constructor injection"
      pattern: "FromKeyedServices"
    - from: "BurstyPublisherWorker.ExecuteAsync"
      to: "IChannel.BasicPublishAsync"
      via: "Parallel.ForEachAsync per-iteration acquire (Pitfall 10 — never share IChannel)"
      pattern: "BasicPublishAsync"
---

<objective>
Validate the Phase 3 adapter end-to-end with three deliverables:
1. Unit tests using NSubstitute (no broker) covering hook wiring, the 3-mode factory probe, AutomaticRecoveryEnabled override + log, channel-spread tracker logic, and CWT lease pairing.
2. Integration tests using Testcontainers.RabbitMq 4.11.0 covering the locked CONTEXT scenarios — basic CRUD against a real broker, channel-spread under `channel_max=10`, lazy cross-pool invalidation when a connection dies, and the BurstyPublisher cycle (idle → burst → idle) without leaks.
3. The runnable sample `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher` (RMQ-03) — a `BackgroundService` cycling idle → 100k burst → idle, demonstrating the headline value proposition.

Purpose: Cover RMQ-03 (sample) and lock down RMQ-01/02/04 with empirical proof. The integration tests are the ANCHOR for Phase 3 — if any Core API gap was masked by Plans 01/02, the integration tests will surface it (per success criterion #5: refactor Core BEFORE Phase 4).

Output:
- 2 test projects + 7 test files + 1 in-memory log helper.
- 1 sample project (csproj + Program.cs + Worker + README).
- 2 new package pins: `Testcontainers.RabbitMq 4.11.0` (test only) — `Microsoft.Extensions.Hosting` is already pinned at 10.0.6.
</objective>

<execution_context>
@/mnt/p/dynamic-pool/.claude/get-shit-done/workflows/execute-plan.md
@/mnt/p/dynamic-pool/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/REQUIREMENTS.md
@.planning/research/PITFALLS.md
@.planning/research/STACK.md
@.planning/phases/03-rabbitmq-adapter/03-CONTEXT.md
@.planning/phases/03-rabbitmq-adapter/03-RESEARCH.md
@.planning/phases/03-rabbitmq-adapter/03-01-PLAN.md
@.planning/phases/03-rabbitmq-adapter/03-02-PLAN.md
@.planning/phases/02-elasticity-health/03-SUMMARY.md
@tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj
@src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticConnectionPoolServiceCollectionExtensions.cs
@src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs
@Directory.Packages.props
@Directory.Build.props

<interfaces>
<!-- Carry-forward from Plans 01 and 02. -->

Adapter public surface (what these tests exercise):
```csharp
namespace Oragon.ElasticPool.RabbitMQ.DependencyInjection;
public static class ElasticConnectionPoolServiceCollectionExtensions
{
    public static IServiceCollection AddElasticConnectionPool(this IServiceCollection services,
        string name,
        Action<ConnectionFactory>? configureFactory,
        Action<ElasticConnectionPoolBuilder> configurePool);
}

public static class ElasticChannelPoolServiceCollectionExtensions
{
    public static IServiceCollection AddElasticChannelPool(this IServiceCollection services,
        string name, string connectionPoolName,
        Action<ElasticChannelPoolBuilder> configurePool);
}
```

Test harness conventions inherited from Phase 1+2 (per Phase 2 SUMMARY "Heads-up to Phase 3"):
- `OutputType=Exe` + `UseMicrosoftTestingPlatformRunner=true` + `TestingPlatformDotnetTestSupport=true`.
- `xunit.v3` 3.2.2, `xunit.runner.visualstudio` 3.1.5, `AwesomeAssertions` 9.4.0, `NSubstitute` 5.3.0.
- `<NoWarn>$(NoWarn);xUnit1051</NoWarn>` for test projects.
- `CapturedLogEntries` is the canonical in-memory `ILoggerProvider` used in Phase 2; copy/paste forward from `tests/Oragon.ElasticPool.Tests/TestSupport/CapturedLogEntries.cs` (it's a 30-LOC helper) — duplication is acceptable per Phase 2's documented decision to avoid pulling FakeLogger.
- Run tests via direct dll execution per CONTEXT carry-forward: `dotnet bin/Release/net{TFM}/Oragon.ElasticPool.RabbitMQ.Tests.dll`.

Testcontainers.RabbitMq 4.11.0 surface (verified per RESEARCH Pattern 4):
```csharp
public sealed class RabbitMqBuilder
{
    public RabbitMqBuilder WithImage(string image);
    public RabbitMqBuilder WithEnvironment(string key, string value);
    public RabbitMqBuilder WithCommand(params string[] command);
    public RabbitMqContainer Build();
}

public sealed class RabbitMqContainer : DockerContainer
{
    public string GetConnectionString();   // amqp://...
    public Task StartAsync(CancellationToken ct = default);
    public override ValueTask DisposeAsync();
}

// xUnit v3 IAsyncLifetime returns ValueTask (NOT Task — verify on first compile per Assumption A1).
public interface IAsyncLifetime
{
    ValueTask InitializeAsync();
    ValueTask DisposeAsync();
}
```

RabbitMQ.Client v7.2.1 declarations needed in the sample (per RESEARCH Assumption A2 — verify exact param ordering at first compile):
```csharp
// IChannel members (verify via IntelliSense on first build — RESEARCH flagged param order as A2):
Task ExchangeDeclareAsync(string exchange, string type, bool durable, bool autoDelete, IDictionary<string, object?>? arguments = null, bool passive = false, bool noWait = false, CancellationToken cancellationToken = default);
Task QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? arguments = null, bool passive = false, bool noWait = false, CancellationToken cancellationToken = default);
Task QueueBindAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? arguments = null, bool noWait = false, CancellationToken cancellationToken = default);
Task BasicPublishAsync(string exchange, string routingKey, bool mandatory, BasicProperties basicProperties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default);

public sealed class BasicProperties
{
    public bool Persistent { get; set; }
    // ... (no CreateBasicProperties() — instantiate directly per v7)
}
```
</interfaces>
</context>

<tasks>

<task type="auto" tdd="true">
  <name>Task 1: Unit tests project + adapter unit tests using NSubstitute</name>
  <files>
    Directory.Packages.props,
    Oragon.ElasticPool.sln,
    tests/Oragon.ElasticPool.RabbitMQ.Tests/Oragon.ElasticPool.RabbitMQ.Tests.csproj,
    tests/Oragon.ElasticPool.RabbitMQ.Tests/TestSupport/CapturedLogEntries.cs,
    tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionFactoryResolverTests.cs,
    tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionChannelTrackerTests.cs,
    tests/Oragon.ElasticPool.RabbitMQ.Tests/ConnectionPoolUnitTests.cs,
    tests/Oragon.ElasticPool.RabbitMQ.Tests/ChannelPoolUnitTests.cs
  </files>
  <behavior>
    The unit test surface (each test self-describes via name):

    `ConnectionFactoryResolverTests`:
    - Resolve_PrefersKeyedSingleton_OverClosure: registers a keyed `IConnectionFactory` substitute AND provides a closure → resolver returns the keyed.
    - Resolve_FallsBackToClosure_WhenNoKeyed: no keyed registration, closure provided → resolver returns the closure-mutated factory.
    - Resolve_FallsBackToOptions_WhenNoKeyedAndNoClosure: only `IOptionsMonitor<ElasticConnectionPoolOptions>` registered with HostName → resolver returns a `ConnectionFactory` populated from options.
    - Resolve_ThrowsInvalidOperation_WhenAllModesFail: nothing registered → throws with message containing the pool name.
    - ForceAutomaticRecoveryDisabled_LogsWarning_AndOverridesToFalse_WhenTrue: substitute logger; assert EventId 2001 + LogLevel.Warning + final value false.
    - ForceAutomaticRecoveryDisabled_NoOp_WhenAlreadyFalse: no log entry, final value false.

    `ConnectionChannelTrackerTests`:
    - TryAcquireSlot_IncrementsBelowMax: 0 → 1 with max=2; returns true.
    - TryAcquireSlot_ReturnsFalse_AtMax: count=2, max=2 → returns false, count unchanged.
    - ReleaseSlot_DecrementsAndRemovesAtZero: count=1 → ReleaseSlot → tracker.CountFor returns 0 and dictionary is empty (use internal accessor or the public CountFor==0 check).
    - ReleaseSlot_NoOpWhenAbsent: ReleaseSlot on a connection never tracked → no throw.
    - Concurrent_TryAcquireSlot_NeverExceedsMax: 64 threads × 1000 attempts on same connection with max=10 → final CountFor exactly 10 (or fewer if some released).

    `ConnectionPoolUnitTests`:
    - AddElasticConnectionPool_RegistersResolvableKeyedSingleton: `BuildServiceProvider().GetRequiredKeyedService<IElasticPool<IConnection>>("default")` is non-null.
    - AddElasticConnectionPool_EmptyName_AlsoResolvableNonKeyed: `name=string.Empty` → `GetRequiredService<IElasticPool<IConnection>>()` works.
    - AddElasticConnectionPool_FactoryCallsCreateConnectionAsync: substitute `IConnectionFactory` keyed-registered; first acquire calls `CreateConnectionAsync` exactly once. Use `Substitute.For<IConnectionFactory>()` and `factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connSubstitute))`.
    - BeforeUse_ReturnsUnhealthy_WhenIsOpenFalse: substitute IConnection with `IsOpen` toggled false; the next AcquireAsync triggers failure-policy replacement (verify by counting Factory invocations — 2nd call). Use Core's `DiscardAndReplaceFailurePolicy<T>` (default).
    - Release_CallsCloseAsync_ThenDispose_EvenWhenCloseThrows: substitute `IConnection.CloseAsync(...)` throws; assert `IConnection.DisposeAsync()` is still received.
    - AutomaticRecoveryOverride_LogsWarning_OnFirstAcquire: register a substitute logger via `services.AddLogging(b => b.AddProvider(captured))`; configure closure that sets `cf.AutomaticRecoveryEnabled = true`; first acquire produces a captured EventId=2001 entry.

    `ChannelPoolUnitTests`:
    - AddElasticChannelPool_FactoryAcquiresFromConnectionPool: register both pools; first channel-acquire calls `connectionPool.AcquireAsync` exactly once via the substituted-but-real Core pool.
    - AddElasticChannelPool_PairingAddedOnFactory: after acquire, the channel is paired (verify by acquiring then disposing, then asserting the connection lease was returned — connection pool's `Available` reverts to original).
    - BeforeUse_ReturnsUnhealthy_WhenChannelIsOpenFalse: substitute IChannel.IsOpen=false; acquire triggers failure-policy.
    - BeforeUse_ReturnsUnhealthy_WhenPairedConnectionIsOpenFalse: channel.IsOpen=true but underlying connection.IsOpen=false (lazy invalidation per Q1) → Unhealthy.
    - Release_DisposesChannel_ThenReleasesConnectionLease: assert ordering: `IChannel.CloseAsync` → `IChannel.DisposeAsync` → connection pool `Available` increments (lease returned).
    - EagerSpread_NewConnection_AcquiredWhenChannelMaxReached: register connection pool with MaxSize=2 + InitialSize=2 returning two distinct substituted connections; channel pool with `MaxChannelsPerConnection=1`; acquire 2 channels — verify they came from 2 distinct connections (assert pairing tables hold 2 distinct IConnection refs).
    - CreateChannelThrows_ReleasesBorrowedConnection: substitute `IConnection.CreateChannelAsync` throws → connection pool's `Available` is unchanged (lease was returned).

    Tests use NSubstitute as the mocking library (per existing Phase 1+2 stack — DO NOT introduce Moq).
  </behavior>
  <action>
    1. Add `Testcontainers.RabbitMq` to `Directory.Packages.props` with `<PackageVersion Include="Testcontainers.RabbitMq" Version="4.11.0" />` (used by Task 2 integration tests).

    2. Create `tests/Oragon.ElasticPool.RabbitMQ.Tests/Oragon.ElasticPool.RabbitMQ.Tests.csproj` modeled on the existing `Core.Tests` csproj:
       - `<TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>`
       - `<IsPackable>false</IsPackable>`
       - `<OutputType>Exe</OutputType>` (Microsoft.Testing.Platform requirement — Phase 1 hit this)
       - `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` and `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`
       - `<NoWarn>$(NoWarn);xUnit1051</NoWarn>`
       - PackageReferences: `xunit.v3`, `xunit.runner.visualstudio`, `AwesomeAssertions`, `NSubstitute`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging.Console`, `Microsoft.Extensions.Diagnostics`, `RabbitMQ.Client` (needed for IConnection/IChannel substitutes), `coverlet.collector` (PrivateAssets="all").
       - ProjectReference: `..\..\src\Oragon.ElasticPool.RabbitMQ\Oragon.ElasticPool.RabbitMQ.csproj`.

    3. Add the test project to `Oragon.ElasticPool.sln` under the existing `tests` solution folder. Generate a fresh GUID. Replicate the Debug/Release × Any CPU/x64/x86 config block.

    4. Create `tests/Oragon.ElasticPool.RabbitMQ.Tests/TestSupport/CapturedLogEntries.cs` — copy verbatim from `tests/Oragon.ElasticPool.Tests/TestSupport/CapturedLogEntries.cs` (it's a 30-LOC ILoggerProvider; per Phase 2 SUMMARY decision, duplication beats pulling FakeLogger). Add a brief comment at top noting "Forward-copied from Core.Tests; Phase 2 documented this is intentional."

    5. Implement the 4 test classes (`ConnectionFactoryResolverTests`, `ConnectionChannelTrackerTests`, `ConnectionPoolUnitTests`, `ChannelPoolUnitTests`) covering ALL behaviors listed under <behavior>. Conventions:
       - Use `[Fact]` (or `[Theory]` where multiple inputs apply).
       - Per-test unique pool names: `$"test-{Guid.NewGuid():N}"` (Phase 2 SUMMARY pattern — process-static singletons leak across parallel tests).
       - For substituted `IPoolItem<IConnection>`, use NSubstitute's `Substitute.For<IPoolItem<IConnection>>()` and configure `Value` to return the substituted IConnection.
       - For Core-backed assertions (no substituted pool engine), `BuildServiceProvider()` and resolve through the real Core pool — that's the integration boundary we want.
       - Add `await using ServiceProvider sp` for clean disposal.

    6. Verify test project builds and runs:
       ```
       dotnet build tests/Oragon.ElasticPool.RabbitMQ.Tests/Oragon.ElasticPool.RabbitMQ.Tests.csproj -c Release
       dotnet bin/Release/net10.0/Oragon.ElasticPool.RabbitMQ.Tests.dll  # direct-dll run (Phase 1+2 carry-forward)
       ```
       Then on net9.0 and net8.0.
  </action>
  <verify>
    <automated>
      cd /mnt/p/dynamic-pool && \
      dotnet build Oragon.ElasticPool.sln -c Release && \
      cd tests/Oragon.ElasticPool.RabbitMQ.Tests && \
      for tfm in net10.0 net9.0 net8.0; do \
        dotnet bin/Release/$tfm/Oragon.ElasticPool.RabbitMQ.Tests.dll || exit 1; \
      done && \
      cd /mnt/p/dynamic-pool && \
      dotnet test tests/Oragon.ElasticPool.Tests/Oragon.ElasticPool.Tests.csproj -c Release --no-build
    </automated>
  </verify>
  <done>
    - All unit tests pass on net10.0, net9.0, net8.0 (3 TFMs × N tests, expect ~25-30 tests total based on the behavior list).
    - Phase 1+2 Core test suite still 141/141 green per TFM (no regression).
    - `Testcontainers.RabbitMq 4.11.0` is centrally pinned.
    - Solution gained `Oragon.ElasticPool.RabbitMQ.Tests` project.
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 2: Integration tests project (Testcontainers) — connection pool, channel pool, channel-spread, AutomaticRecoveryEnabled override, BurstyPublisher cycle</name>
  <files>
    Oragon.ElasticPool.sln,
    tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Oragon.ElasticPool.RabbitMQ.IntegrationTests.csproj,
    tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/RabbitMqContainerFixture.cs,
    tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Fixtures/LowChannelMaxFixture.cs,
    tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ConnectionPoolIntegrationTests.cs,
    tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ChannelPoolIntegrationTests.cs,
    tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/ChannelSpreadIntegrationTests.cs,
    tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/AutomaticRecoveryOverrideTests.cs,
    tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/BurstyPublisherIntegrationTests.cs
  </files>
  <behavior>
    All test classes carry `[Trait("Category", "Integration")]` so unit-runs (no Docker) skip them via filter.

    `ConnectionPoolIntegrationTests` (uses `RabbitMqContainerFixture`):
    - AcquireAsync_ReturnsOpenConnection_AndReleaseReturnsToPool: acquire one lease → `lease.Value.IsOpen == true`; dispose lease → connection pool `Available == 1`.
    - PoolGrowsToMaxSize_UnderConcurrentAcquire: 8 simultaneous AcquireAsync against pool with MinSize=0,MaxSize=4 → 4 succeed in parallel + 4 wait; never exceeds MaxSize.
    - DispiseConnectionPool_DrainsAndCloses: dispose service provider → all open IConnection instances closed (verify by recording `CloseAsync` invocations via a derived ConnectionFactory or by re-acquiring and asserting fresh ones).

    `ChannelPoolIntegrationTests` (uses `RabbitMqContainerFixture`):
    - AcquireChannel_PublishMessage_RoundTrips: declare a transient queue via the same channel pool; publish; consume from broker (use a separate IConnection for the consumer to keep the test isolated); assert message body matches.
    - ChannelDispose_ReturnsConnectionToPool: layered acquire of channel → dispose channel → connection pool `Available` reverts. Demonstrates ConditionalWeakTable pairing actually returns the lease.
    - DeadConnectionMarksChannelsUnhealthy_LazyInvalidation: acquire a channel; force-close the underlying IConnection via `connection.CloseAsync` from a side reference; subsequent `BeforeUse` of THAT channel returns Unhealthy and the failure policy replaces it. Validates Q1's lazy strategy works in a real-broker scenario.

    `ChannelSpreadIntegrationTests` (uses `LowChannelMaxFixture` — `channel_max=10`):
    - ChannelMax10_50ConcurrentChannels_SpreadAcrossMultipleConnections: connection pool MinSize=1,MaxSize=8; channel pool MaxChannelsPerConnection=10; 50 channels acquired in parallel → at least 5 distinct IConnection.ClientProvidedName/IConnection refs observed (assert via tracker.CountFor or by reading connection pool `Available + InUse` ≥ 5).
    - This test validates RESEARCH Q2 + Pitfall C and is the empirical justification for the eager-spread strategy.

    `AutomaticRecoveryOverrideTests` (uses `RabbitMqContainerFixture`):
    - ConsumerSetsAutomaticRecoveryTrue_AdapterOverridesAndLogs: configure closure with `cf.AutomaticRecoveryEnabled = true` → first acquire emits Warning EventId=2001; configured factory's value is now false; broker connection still works. Captures via `CapturedLogEntries`.
    - PoolReplacesDeadConnection_NotAutomaticRecovery: kill connection mid-flight (close from broker side via management plugin or `channel.AbortAsync` equivalent); next acquire's BeforeUse marks Unhealthy → DiscardAndReplace produces a fresh connection (verify NewConnection counter > 1).

    `BurstyPublisherIntegrationTests` (uses `RabbitMqContainerFixture`):
    - BurstIdleBurst_NoLeakedChannelsOrConnections: pool with MinSize=1,MaxSize=8; channel pool MinSize=0,MaxSize=64. Run 3 cycles: idle 2s → burst 1000 publishes (Parallel.ForEachAsync, MaxDegreeOfParallelism=128) → idle 5s. Assertions:
      - Final connection pool `InUse == 0`.
      - Final channel pool `InUse == 0`.
      - During burst, observe `pool.grow.count` (channel pool) > 0 via `MetricCollector<long>`.
      - During idle (after IdleTimeout elapse via real-time), observe `pool.shrink.count` > 0.
      - All published messages observed via a side-channel consumer (count == 3000).
      - NOTE: This is a SCALED-DOWN version of the sample (1k×3 instead of 100k×3) so the test runs in <60s wall-clock. The full 100k cycle lives in the SAMPLE, not the test.
  </behavior>
  <action>
    1. Create `tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests/Oragon.ElasticPool.RabbitMQ.IntegrationTests.csproj` modeled on the unit test project. Differences:
       - Add `<PackageReference Include="Testcontainers.RabbitMq" />`.
       - Add `<PackageReference Include="Microsoft.Extensions.Diagnostics.Testing" />` (for `MetricCollector<T>` per Phase 2 pattern).
       - Add `<PackageReference Include="Microsoft.Extensions.Hosting" />` (BurstyPublisher integration test uses Generic Host or at least IHostedService primitives).
       - ProjectReference: `..\..\src\Oragon.ElasticPool.RabbitMQ\Oragon.ElasticPool.RabbitMQ.csproj`.

    2. Add the integration test project to `Oragon.ElasticPool.sln` under `tests` folder. Fresh GUID.

    3. Create `Fixtures/RabbitMqContainerFixture.cs` — implements xUnit v3 `IAsyncLifetime` (returns `ValueTask`):
       ```csharp
       public sealed class RabbitMqContainerFixture : IAsyncLifetime
       {
           public RabbitMqContainer Container { get; } =
               new RabbitMqBuilder()
                   .WithImage("rabbitmq:4-management")
                   .Build();

           public string ConnectionString => Container.GetConnectionString();

           public async ValueTask InitializeAsync() => await Container.StartAsync();
           public async ValueTask DisposeAsync()    => await Container.DisposeAsync();
       }
       ```
       If on first compile xUnit v3's `IAsyncLifetime` actually expects `Task` (Assumption A1 falsified), wrap with `Task.CompletedTask` or change return types — the fix is mechanical.

    4. Create `Fixtures/LowChannelMaxFixture.cs` — same shape as `RabbitMqContainerFixture` but with broker `channel_max=10`. Approach: write a minimal `rabbitmq.conf` to a temp file that contains `channel_max = 10`, then `WithBindMount(tempFile, "/etc/rabbitmq/rabbitmq.conf")`. Alternative: `WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", "-rabbit channel_max 10")` — try the env var approach first (simpler). Document in XML comment that this is the channel-spread anchor fixture.

    5. Implement the 5 integration test classes per <behavior>. Conventions:
       - All classes carry `[Trait("Category", "Integration")]`.
       - Use `IClassFixture<RabbitMqContainerFixture>` (or `LowChannelMaxFixture` for spread test).
       - Each test runs in a fresh `ServiceCollection` to avoid shared state.
       - Use `MetricCollector<long>(meterFactory, "Oragon.ElasticPool", "pool.grow.count")` for grow/shrink assertions (Phase 2 SUMMARY pattern).
       - Use `await using` for service provider AND for individual leases.

    6. Verify build + run with Docker available:
       ```
       dotnet build Oragon.ElasticPool.sln -c Release
       cd tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests
       dotnet bin/Release/net10.0/Oragon.ElasticPool.RabbitMQ.IntegrationTests.dll --filter "Category=Integration"
       ```
       (If Docker unavailable in the executor's environment: STOP and report — this is RMQ-03's anchor, cannot be skipped without losing the phase's integration validation.)
  </action>
  <verify>
    <automated>
      cd /mnt/p/dynamic-pool && \
      dotnet build Oragon.ElasticPool.sln -c Release && \
      docker ps >/dev/null 2>&1 && \
      cd tests/Oragon.ElasticPool.RabbitMQ.IntegrationTests && \
      for tfm in net10.0 net9.0 net8.0; do \
        dotnet bin/Release/$tfm/Oragon.ElasticPool.RabbitMQ.IntegrationTests.dll || exit 1; \
      done
    </automated>
  </verify>
  <done>
    - All integration tests pass on net10.0, net9.0, net8.0.
    - The channel-spread test produces ≥ 5 distinct connection slots used (real broker enforcement under channel_max=10).
    - The lazy-invalidation test demonstrates Q1's strategy is sufficient — if it FAILS (consumer sees AlreadyClosedException leaking through), STOP per success criterion #5: file the gap and refactor Core (add an OnItemDiscarded event / eager invalidation path) BEFORE Phase 4.
    - The BurstIdleBurst test shows grow during burst, shrink during idle, regrow on next burst — this is the headline behavior for Phase 3.
    - Phase 1+2 Core tests still green; full solution build clean.
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 3: BurstyPublisher sample project — runnable end-to-end (RMQ-03)</name>
  <files>
    Oragon.ElasticPool.sln,
    samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher.csproj,
    samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Program.cs,
    samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/BurstyPublisherWorker.cs,
    samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md
  </files>
  <behavior>
    - Sample compiles for `net10.0` only (samples don't need multi-target — single-runtime keeps sample lean).
    - `Program.cs` wires Generic Host: `Host.CreateApplicationBuilder` → `services.AddLogging(... AddConsole())` → `services.AddElasticConnectionPool("sample", f => f.Uri = new Uri(connStr), p => p.WithBounds(min:1,max:8,initial:1))` → `services.AddElasticChannelPool("sample","sample", p => p.WithBounds(0,64,0).WithMaxChannelsPerConnection(50))` → `services.AddHostedService<BurstyPublisherWorker>()` → `host.RunAsync()`.
    - Connection string source: env var `RABBITMQ_URI` (default `amqp://guest:guest@localhost:5672/` if unset).
    - `BurstyPublisherWorker : BackgroundService` cycles 3 times: 5min idle → 30s burst (~100k publishes via `Parallel.ForEachAsync`, MaxDegreeOfParallelism = 256, each acquiring its OWN channel per Pitfall 10) → 5min idle. Logs cycle markers + elapsed/throughput per burst.
    - Topology: declare exchange `oragon.elasticpool.sample` (direct, durable), queue `oragon.elasticpool.sample.queue` (durable, classic — quorum requires 3-node cluster), binding to routing key `bursty.demo`. Done once at worker startup.
    - Message body: small JSON `{ "Idx": 1234, "Cycle": 0 }`, persistent.
    - Per-publish: `BasicProperties { Persistent = true }`, `mandatory: false`.
    - README: prerequisites (Docker for local broker), how to start broker (`docker run -d --rm -p 5672:5672 -p 15672:15672 rabbitmq:4-management`), how to run sample (`dotnet run --project samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher`), expected log output snippet (cycle 0/1/2 with throughput numbers).
    - The sample is NOT in CI; it's documentation + a runnable demonstration (per RMQ-03).
  </behavior>
  <action>
    1. Create `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher.csproj`:
       - `<TargetFramework>net10.0</TargetFramework>` (single-runtime sample).
       - `<OutputType>Exe</OutputType>`, `<IsPackable>false</IsPackable>`, `<RootNamespace>Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher</RootNamespace>`.
       - PackageReferences: `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging.Console`, `RabbitMQ.Client`.
       - ProjectReference: `..\..\src\Oragon.ElasticPool.RabbitMQ\Oragon.ElasticPool.RabbitMQ.csproj`.

    2. Create `Program.cs` per <behavior>. Use top-level statements (.NET 10 idiom). Include OTel-friendly logging configuration: `builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });`. Document in inline comment that the consumer can `.AddMeter("Oragon.ElasticPool")` and `.AddSource("Oragon.ElasticPool")` to a real OTel exporter for production scenarios.

    3. Create `BurstyPublisherWorker.cs` per <behavior>:
       - Use primary constructor: `public sealed class BurstyPublisherWorker([FromKeyedServices("sample")] IElasticPool<IChannel> channelPool, ILogger<BurstyPublisherWorker> logger) : BackgroundService`.
       - Topology declared in a separate setup acquire (not inside the burst loop).
       - Burst loop: `await Parallel.ForEachAsync(Enumerable.Range(0, 100_000), new ParallelOptions { MaxDegreeOfParallelism = 256, CancellationToken = ct }, async (i, token) => { await using var ch = await channelPool.AcquireAsync(token); var body = JsonSerializer.SerializeToUtf8Bytes(new { Idx = i, Cycle = cycle }); await ch.Value.BasicPublishAsync(Exchange, RoutingKey, mandatory: false, basicProperties: new BasicProperties { Persistent = true }, body: body, cancellationToken: token); });`.
       - Per Pitfall 10: comment near the inner lambda saying "// MUST acquire a fresh channel per iteration — IChannel is NOT thread-safe for publish (RabbitMQ docs)".
       - Cycle count: 3. Idle duration: 5 min between cycles. (Configurable via env vars `BURSTY_CYCLES`, `BURSTY_IDLE_SECONDS`, `BURSTY_BURST_COUNT` for dev convenience, but defaults match CONTEXT.)
       - Catch and log cancellation cleanly so Ctrl+C exits without stack traces.

    4. Create `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/README.md` per <behavior>. ~50 lines. Include a "What this demonstrates" section pointing back to PROJECT.md "Motivation real" paragraph.

    5. Add the sample to `Oragon.ElasticPool.sln` under a NEW `samples` solution folder (generate a folder GUID since none exists yet).

    6. Verify the sample BUILDS (don't actually run a 30-min cycle in verification — that's manual):
       ```
       dotnet build samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher.csproj -c Release
       ```
       Optionally run a smoke build with `BURSTY_CYCLES=1 BURSTY_IDLE_SECONDS=2 BURSTY_BURST_COUNT=100` against a Testcontainers-managed broker to prove end-to-end works (manual verify, not automated in this task — it would need orchestration).
  </action>
  <verify>
    <automated>
      cd /mnt/p/dynamic-pool && \
      dotnet build samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher.csproj -c Release && \
      grep -v '^#' samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/BurstyPublisherWorker.cs | grep -c "AcquireAsync" && \
      grep -v '^#' samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/BurstyPublisherWorker.cs | grep -c "Parallel.ForEachAsync" && \
      grep -v '^#' samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Program.cs | grep -c "AddElasticConnectionPool" && \
      grep -v '^#' samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/Program.cs | grep -c "AddElasticChannelPool" && \
      grep -c "samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher" Oragon.ElasticPool.sln
    </automated>
  </verify>
  <done>
    - Sample compiles clean on net10.0.
    - All grep markers fire ≥ 1 (uses both adapter extensions, Parallel.ForEachAsync per Pitfall 10, AcquireAsync per iteration).
    - Sample appears in solution under `samples` folder.
    - README documents prerequisites + run command.
    - Phase 1+2 Core tests still green.
    - Full solution build clean.
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Test → Testcontainers Docker daemon | Docker socket access; container lifecycle |
| Test → Real RabbitMQ broker (containerized) | AMQP boundary, network |
| Sample → User-supplied env var `RABBITMQ_URI` | Untrusted URI (user controls; could be malformed) |
| Sample → Real broker | Standard AMQP with credentials (sample uses default `guest/guest`) |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-03-13 | Denial of Service | BurstyPublisher's 256-parallelism burst overwhelms developer laptop | mitigate | Tunable via `BURSTY_*` env vars; default counts are documented; README warns "first run with reduced counts to size your machine" |
| T-03-14 | Information Disclosure | Sample logs full URI (could include password) | mitigate | Sample logs only the host portion of the URI, never the userInfo segment |
| T-03-15 | Tampering | Integration test using `LowChannelMaxFixture` mounts a config file — could be tampered with on host | accept | Test-only path; the temp file is created by the test itself and disposed when the container is disposed |
| T-03-16 | Repudiation | Lazy-invalidation integration test asserts behavior but doesn't capture the full timeline (when did connection die, when was BeforeUse run?) | mitigate | Add structured logging via CapturedLogEntries to the test asserting EventId 1010 (CheckUnhealthy) fires in the expected window |
| T-03-17 | Elevation of Privilege | Sample runs as the host user with full Docker access | accept | Standard for local development samples; no production deployment intended |
</threat_model>

<verification>
- `dotnet build Oragon.ElasticPool.sln -c Release` exits 0; produces 8 projects (Core, Core.Tests, Core.Stress, Core.Benchmarks, RabbitMQ, RabbitMQ.Tests, RabbitMQ.IntegrationTests, RabbitMQ.Sample.BurstyPublisher).
- Unit tests pass on net8/9/10 (direct-dll execution per Phase 1+2 carry-forward).
- Integration tests pass on net8/9/10 with Docker available (Testcontainers spins fresh broker per test class).
- The channel-spread integration test produces ≥ 5 distinct connections under broker `channel_max=10` (empirical proof of eager-spread strategy from Plan 02).
- The lazy-invalidation integration test passes — Q1's "ship lazy first" decision is empirically validated.
- The BurstIdleBurst integration test passes within ~30-60s wall-clock (downscaled vs sample).
- Sample compiles on net10.0; runs end-to-end (manual verify per RMQ-03).
- Phase 1+2 Core tests still 141/141 green per TFM (no regression).
- IF the lazy-invalidation integration test FAILS (e.g., consumer sees `AlreadyClosedException` propagated through), this is the success-criterion-#5 trigger: STOP, file a Core API gap (eager `OnItemDiscarded` event needed), and refactor Core BEFORE Phase 4. The plan author should NOT proceed to mark Phase 3 complete in that case.
- IF the channel-spread integration test FAILS, similarly file the gap and revisit.
</verification>

<success_criteria>
1. `Oragon.ElasticPool.RabbitMQ.Tests` project exists, runs on all 3 TFMs, all unit tests pass.
2. `Oragon.ElasticPool.RabbitMQ.IntegrationTests` project exists, all 5 test classes pass against Testcontainers RabbitMQ 4.x.
3. `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher` builds and is documented in its own README.
4. The channel-spread test empirically proves the eager strategy (Plan 02) handles `channel_max=10` correctly.
5. The lazy-invalidation test empirically proves Q1's strategy is sufficient — OR it fails and the failure surfaces a Core API gap to be fixed before Phase 4 (per phase success criterion #5).
6. No regression in Core tests.
7. Phase 3 covers RMQ-01, RMQ-02, RMQ-03, RMQ-04 with empirical evidence.
</success_criteria>

<output>
After completion, create `.planning/phases/03-rabbitmq-adapter/03-03-SUMMARY.md` documenting:
- Files created with line counts.
- Test counts per project (unit / integration).
- Total test runtime per TFM.
- Channel-spread test result: number of distinct connections observed under channel_max=10.
- Lazy-invalidation test result: PASS (Q1 validated; ship as-is) or FAIL (file Core API gap).
- BurstIdleBurst test: grow.count delta, shrink.count delta during cycles.
- Sample runnability confirmation (smoke run if performed).
- Heads-up to Phase 4: any pending Core API gap to address; CI workflow needs a Docker-enabled job for integration tests; PublicAPI files need to be moved from Unshipped→Shipped at v1.0.

Decision-tree appendix:
- If lazy-invalidation passes → mark Q1 resolved (lazy-only is sufficient for v1).
- If lazy-invalidation fails → mark Q1 escalated; file Core API gap proposal (event API or reverse-index in adapter); STOP Phase 3 completion until gap is closed.
- Same logic for channel-spread vs Q2: if eager strategy in Plan 02 produces < 2 distinct connections under channel_max=10, escalate Q2 — but Plan 02 implements eager so this should pass.
</output>
