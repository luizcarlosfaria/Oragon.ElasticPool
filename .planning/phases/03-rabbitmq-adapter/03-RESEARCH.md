# Phase 3: RabbitMQ Adapter — Research

**Researched:** 2026-05-02
**Domain:** RabbitMQ.Client v7.2.1 adapter on top of Oragon.AdaptivePool.Core (layered IConnection + IChannel pools, Testcontainers integration, bursty publisher sample)
**Confidence:** HIGH (RabbitMQ v7 API and Testcontainers v4 verified live; Core API surface verified directly from Phase 1+2 source)

## Summary

The RabbitMQ adapter is a thin DX layer over Core. Core already exposes everything needed
(generic `IAdaptivePool<T>`, `Factory`/`BeforeUse`/`Check`/`Release` hooks with `CancellationToken`,
`DiscardAndReplaceFailurePolicy<T>`, `services.AddAdaptivePool<T>(name, configure)` with named
keyed-singleton resolution + non-keyed fallback for `name = string.Empty`, telemetry tagged by
`pool.name`). The adapter contributes (a) two opinionated extension methods that wire RabbitMQ-specific
hooks into Core's builder; (b) a strict default of `AutomaticRecoveryEnabled = false`; (c) a layered
channel-pool composition where the channel `Factory` acquires from the connection pool and pairs the
channel-to-connection lease via `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>`; (d) a
"channels per connection" spreading policy to dodge the broker's negotiated `channel_max` ceiling;
(e) a Testcontainers-driven integration test reproducing the bursty-publisher cycle; (f) a runnable
sample showing the same.

The only **risky** piece is cross-pool invalidation when a connection dies under leased channels.
The architecture document specifies this is handled lazily — the channel pool's `BeforeUse` re-checks
`connLease.Object.IsOpen` via the `ConditionalWeakTable` and returns `Unhealthy` when the underlying
connection is dead. CONTEXT.md adds an **eager** path: when the connection pool's `Check` discards a
connection, the channel pool is notified via callback and pre-marks all channels on that connection
as Unhealthy so they're discarded on return rather than handed back. The eager path requires either
a new public Core surface (an `OnItemDiscarded` event) **or** a reverse-index living entirely in the
adapter (`ConcurrentDictionary<IConnection, ImmutableHashSet<IChannel>>`). **Recommendation: ship the
lazy path only for Phase 3 v1**; the eager path adds Core API pressure (which the phase explicitly
wants to avoid before publish). Document as a v2 enhancement and revisit after seeing real-world
failure modes in the integration test. This is flagged as Open Question Q1.

**Primary recommendation:** Build two `Microsoft.Extensions.DependencyInjection` extension methods —
`AddAdaptiveConnectionPool(name, configureFactory, configurePool)` and `AddAdaptiveChannelPool(name, connectionPoolName, configureChannelPool)` — that compose Core's `AddAdaptivePool<IConnection>` / `AddAdaptivePool<IChannel>` with RabbitMQ-specific hooks. Force `AutomaticRecoveryEnabled = false` with a Warning log if the consumer set it true. Pair channels with their connection lease via a `ConditionalWeakTable`. Default `MaxChannelsPerConnection = 100`. Use `RabbitMqBuilder().WithImage("rabbitmq:4-management")` + `IAsyncLifetime` for the integration test. Sample is a `BackgroundService` cycling 5 min idle → 30 s burst of 10k–100k publishes → 5 min idle.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|---|---|---|---|
| TCP connection lifecycle to broker | Adapter (RabbitMQ) | Core (failure policy invokes Release) | Network ownership is RabbitMQ-domain; Core only knows "an opaque T" |
| Channel multiplexing over connection | Adapter (RabbitMQ) | Core (layered acquire via inner pool) | Channel ↔ connection pairing is RabbitMQ-specific; Core's hook contract suffices |
| Health probing (IsOpen) | Adapter (BeforeUse + Check hooks) | Core (sweeper invokes Check) | Health predicate is RabbitMQ-specific; sweeper schedule is Core's |
| Elastic grow / shrink | Core | — | Adapter is a pure consumer of Core's elasticity; no override |
| Failure policy (discard + replace) | Core (`DiscardAndReplaceFailurePolicy<T>`) | — | Default policy works unchanged for both `IConnection` and `IChannel` |
| DI registration | Adapter (extension methods) | Core (`AddAdaptivePool<T>`) | Adapter wraps Core's keyed-singleton DI pattern with RabbitMQ-specific configuration helpers |
| Telemetry emission | Core (Meter + ActivitySource) | Adapter (sets `pool.name` tag) | Telemetry surface is Core's; adapter only labels its pools (`"rabbitmq.connection"`, `"rabbitmq.channel"`) |
| Connection-spread across channels | Adapter (channel pool's Factory) | Core (grow signals trigger new factory calls) | Channel-per-connection ceiling is RabbitMQ-specific; Core has no concept |
| Cross-pool invalidation (eager) | Adapter (DEFERRED) | — | Recommended deferred to v2 — see Open Question Q1 |

## Standard Stack

### Core (new dependencies for adapter package)

| Library | Version | Purpose | Why Standard |
|---|---|---|---|
| `RabbitMQ.Client` | **7.2.1** | `IConnection`/`IChannel` types being pooled | Async-first v7 line; pinned in Phase 1 STACK [VERIFIED: rabbitmq.github.io API docs] |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | **10.0.6** | Adapter `services.Add...` extension surface | Already used by Core; consistent transitive surface [VERIFIED: Directory.Packages.props line 14] |
| `Microsoft.Extensions.Logging.Abstractions` | **10.0.6** | Adapter-level Warning when AutomaticRecoveryEnabled is overridden | Already used by Core [VERIFIED: Directory.Packages.props line 13] |
| `Microsoft.Extensions.Options` | **10.0.6** | Bind `AdaptiveConnectionPoolOptions` from `IConfiguration` (the third probe mode in CONTEXT D-IDX) | Already used by Core [VERIFIED: Directory.Packages.props line 15] |

### Supporting (test + sample only — no consumer cost)

| Library | Version | Purpose | When to Use |
|---|---|---|---|
| `Testcontainers.RabbitMq` | **4.11.0** (published 2026-03-12) | Spin up RabbitMQ 4.x in CI | Integration tests + sample readme [VERIFIED: nuget.org/packages/Testcontainers.RabbitMq] |
| `Microsoft.Extensions.Hosting` | 10.0.6 | `BackgroundService` base for sample worker + integration test publisher loops | Sample only [VERIFIED: already in Directory.Packages.props line 49] |
| Existing test stack | — | xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5, AwesomeAssertions 9.4.0, NSubstitute 5.3.0, coverlet.collector 6.0.4 | Same as Phase 1+2 [VERIFIED: Directory.Packages.props lines 23–43] |

**Versions to add to `Directory.Packages.props`:**

```xml
<PackageVersion Include="RabbitMQ.Client" Version="7.2.1" />
<PackageVersion Include="Testcontainers.RabbitMq" Version="4.11.0" />
```

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|---|---|---|
| `Testcontainers.RabbitMq` 4.11.0 | docker-compose + ad-hoc broker | Testcontainers is the lib's own canonical choice (docs show `RabbitMqBuilder` directly) [VERIFIED: dotnet.testcontainers.org/modules/rabbitmq/]; docker-compose adds CI/local divergence |
| `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` | Wrapper type `ChannelLease : IChannel` (decorator) | The decorator route forces `IAdaptivePool<IChannel>` callers to receive a wrapper type, breaking the "channel just looks like an IChannel" contract. ARCHITECTURE.md explicitly chose `ConditionalWeakTable` for this reason. [CITED: .planning/research/ARCHITECTURE.md line 485] |
| Forced `AutomaticRecoveryEnabled = false` (silent override) | Throw at registration if consumer set true | Throwing makes the adapter brittle for clients copying old config. CONTEXT D-AR specifies log Warning + override silently. [CITED: 03-CONTEXT.md "Connection factory configuration warning"] |
| Eager cross-pool invalidation (callback) | Lazy invalidation only (BeforeUse re-probes) | Eager wins on latency at the cost of new Core API surface or significant adapter-side reverse-index complexity. **Phase 3 v1 ships lazy only**; eager deferred to v2. See Q1. |

## Architecture Patterns

### System Architecture Diagram

```
                    Consumer Code (BackgroundService / minimal API endpoint / etc.)
                                  │
                                  │  await using var ch = await _channelPool.AcquireAsync(ct);
                                  │  await ch.Value.BasicPublishAsync(exchange, key, body, ct);
                                  ▼
        ┌───────────────────────────────────────────────────────────────┐
        │  Oragon.AdaptivePool.RabbitMQ (this phase)                    │
        │                                                               │
        │  ┌──────────────────────┐    ┌─────────────────────────────┐  │
        │  │  Connection pool     │    │  Channel pool (LAYERED)     │  │
        │  │  IAdaptivePool<      │◄───│  Factory(): acquires from   │  │
        │  │    IConnection>      │    │   connection pool, pairs    │  │
        │  │                      │    │   IChannel ↔               │  │
        │  │  Factory: connFac    │    │   IPoolItem<IConnection>    │  │
        │  │    .CreateConnection │    │   via ConditionalWeakTable  │  │
        │  │     Async(ct)        │    │                             │  │
        │  │  BeforeUse:          │    │  BeforeUse: ch.IsOpen       │  │
        │  │    conn.IsOpen       │    │    && conn.IsOpen (via      │  │
        │  │  Check: conn.IsOpen  │    │    weak-table lookup)       │  │
        │  │  Release:            │    │  Release: close ch then     │  │
        │  │    conn.CloseAsync   │    │    dispose conn lease       │  │
        │  │     + DisposeAsync   │    │    (returns conn to pool)   │  │
        │  └──────────────────────┘    └─────────────────────────────┘  │
        │           │                              │                    │
        └───────────┼──────────────────────────────┼────────────────────┘
                    │                              │
                    ▼                              ▼
        ┌──────────────────────────────────────────────────┐
        │  Oragon.AdaptivePool.Core                        │
        │  AdaptiveObjectPoolFactory.Build<T>(...)         │
        │  Builder: .Factory/.BeforeUse/.Check/.Release    │
        │  AddAdaptivePool<T>(name, configure)             │
        │  Sweeper, elasticity, telemetry (single Meter)   │
        └──────────────────────────────────────────────────┘
                    │                              │
                    ▼                              ▼
        ┌──────────────────────────────────────────────────┐
        │  RabbitMQ.Client 7.2.1                           │
        │  ConnectionFactory.CreateConnectionAsync(ct)     │
        │  IConnection.CreateChannelAsync(opts, ct)        │
        │  IConnection.IsOpen, IConnection.CloseAsync      │
        │  IChannel.IsOpen, IChannel.CloseAsync,           │
        │   IChannel.BasicPublishAsync(...)                │
        └──────────────────────────────────────────────────┘
                                  │
                                  ▼
                          ┌────────────────┐
                          │  RabbitMQ      │
                          │  broker (4.x)  │
                          └────────────────┘
```

### Recommended Project Structure

```
src/
├── Oragon.AdaptivePool.Core/                      # existing (Phase 1+2)
└── Oragon.AdaptivePool.RabbitMQ/                  # NEW
    ├── Builder/
    │   ├── AdaptiveConnectionPoolBuilder.cs       # connection-specific options + factory probe
    │   └── AdaptiveChannelPoolBuilder.cs          # channel-specific options (MaxChannelsPerConnection)
    ├── DependencyInjection/
    │   └── ServiceCollectionExtensions.cs         # AddAdaptiveConnectionPool / AddAdaptiveChannelPool
    ├── Internals/
    │   └── ChannelLeasePairing.cs                 # ConditionalWeakTable wrapper utility
    ├── Options/
    │   ├── AdaptiveConnectionPoolOptions.cs       # IOptions-bindable settings
    │   └── AdaptiveChannelPoolOptions.cs
    ├── Oragon.AdaptivePool.RabbitMQ.csproj
    ├── PublicAPI.Shipped.txt
    └── PublicAPI.Unshipped.txt

tests/
├── Oragon.AdaptivePool.RabbitMQ.Tests/            # unit (NSubstitute IConnection/IChannel)
└── Oragon.AdaptivePool.RabbitMQ.IntegrationTests/ # Testcontainers
    ├── Fixtures/
    │   └── RabbitMqContainerFixture.cs            # IClassFixture w/ IAsyncLifetime
    ├── ConnectionPoolIntegrationTests.cs
    ├── ChannelPoolIntegrationTests.cs
    └── BurstyPublisherIntegrationTests.cs

samples/
└── Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/
    ├── Program.cs                                 # builder.Services.Add* + console OTel exporter
    ├── BurstyPublisherWorker.cs                   # BackgroundService cycling idle→burst→idle
    └── Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher.csproj
```

### Pattern 1: ConnectionPool registration (with the three-mode `IConnectionFactory` probe)

**What:** `AddAdaptiveConnectionPool` resolves the `IConnectionFactory` in priority order: keyed singleton by `name` → closure callback → `IOptions<AdaptiveConnectionPoolOptions>`.

**When to use:** Every consumer call to `AddAdaptiveConnectionPool`.

**Example:**

```csharp
// Source: Phase 1 ServiceCollectionExtensions pattern + RabbitMQ.Client v7 docs.
public static IServiceCollection AddAdaptiveConnectionPool(
    this IServiceCollection services,
    string name,
    Action<ConnectionFactory>? configureFactory,
    Action<AdaptiveConnectionPoolBuilder> configurePool)
{
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(configurePool);

    var poolBuilder = new AdaptiveConnectionPoolBuilder();
    configurePool(poolBuilder);

    services.AddAdaptivePool<IConnection>(name, builder =>
    {
        builder
            .Factory(async (sp, ct) =>
            {
                // Probe order: 1) keyed IConnectionFactory by name 2) closure 3) IOptions
                var factory = ResolveConnectionFactory(sp, name, configureFactory);
                ForceAutomaticRecoveryDisabled(factory, sp);  // logs Warning if was true
                return await factory.CreateConnectionAsync(ct).ConfigureAwait(false);
            })
            .BeforeUse((conn, _) =>
                ValueTask.FromResult(conn.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))
            .Check((conn, _) =>
                ValueTask.FromResult(conn.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))
            .Release(async (conn, ct) =>
            {
                try { await conn.CloseAsync(ct).ConfigureAwait(false); } catch { /* swallow */ }
                await conn.DisposeAsync().ConfigureAwait(false);
            })
            .WithBounds(poolBuilder.MinSize, poolBuilder.MaxSize, poolBuilder.InitialSize)
            .IdleTimeout(poolBuilder.IdleTimeout);
    });

    return services;
}

private static IConnectionFactory ResolveConnectionFactory(
    IServiceProvider sp, string name, Action<ConnectionFactory>? configureFactory)
{
    // 1) Keyed singleton (preferred — Aspire-friendly, multi-broker scenarios)
    var keyed = sp.GetKeyedService<IConnectionFactory>(name);
    if (keyed is not null) return keyed;

    // 2) Closure callback
    if (configureFactory is not null)
    {
        var f = new ConnectionFactory();
        configureFactory(f);
        return f;
    }

    // 3) IOptions binding
    var opts = sp.GetService<IOptionsMonitor<AdaptiveConnectionPoolOptions>>()?.Get(name);
    if (opts is not null && !string.IsNullOrEmpty(opts.HostName))
    {
        return new ConnectionFactory
        {
            HostName = opts.HostName,
            Port = opts.Port,
            UserName = opts.UserName ?? ConnectionFactory.DefaultUser,
            Password = opts.Password ?? ConnectionFactory.DefaultPass,
            VirtualHost = opts.VirtualHost ?? ConnectionFactory.DefaultVHost,
            RequestedHeartbeat = opts.RequestedHeartbeat ?? TimeSpan.FromSeconds(60),
        };
    }

    throw new InvalidOperationException(
        $"AddAdaptiveConnectionPool: no IConnectionFactory found for pool '{name}'. " +
        "Provide one via keyed singleton, configureFactory closure, or IOptions binding.");
}

private static void ForceAutomaticRecoveryDisabled(IConnectionFactory factory, IServiceProvider sp)
{
    if (factory is ConnectionFactory cf && cf.AutomaticRecoveryEnabled)
    {
        sp.GetService<ILoggerFactory>()
          ?.CreateLogger("Oragon.AdaptivePool.RabbitMQ")
           .LogWarning(
               "AutomaticRecoveryEnabled was true on the configured ConnectionFactory; " +
               "Oragon.AdaptivePool overrides this to false (the pool owns lifecycle). " +
               "See https://github.com/.../docs/automatic-recovery.md");
        cf.AutomaticRecoveryEnabled = false;
    }
}
```

[VERIFIED: ConnectionFactory.CreateConnectionAsync signature has overload `CreateConnectionAsync(CancellationToken cancellationToken = default)` — confirmed via rabbitmq.github.io/rabbitmq-dotnet-client/api/RabbitMQ.Client.ConnectionFactory.html]
[VERIFIED: AutomaticRecoveryEnabled is a settable `bool` property defaulting to `true` — confirmed via same source]
[VERIFIED: Phase 1 `AddAdaptivePool<T>(name, configure)` accepts `Action<AdaptivePoolBuilder<T>>` — confirmed via src/Oragon.AdaptivePool.Core/DependencyInjection/ServiceCollectionExtensions.cs:16]

### Pattern 2: ChannelPool registration (LAYERED — Factory acquires from connection pool)

**What:** Channel pool's `Factory` hook acquires a connection from the inner pool; on success, pairs the channel with its connection-lease in a `ConditionalWeakTable`. On `Release`, close channel first, then dispose the connection-lease.

**When to use:** Whenever the consumer wants pooled channels. Typically paired 1:1 with a connection pool of the same `name`.

**Example:**

```csharp
// Source: ARCHITECTURE.md "Adapter Composition" section, adapted to Core's actual API.
public static IServiceCollection AddAdaptiveChannelPool(
    this IServiceCollection services,
    string name,
    string connectionPoolName,
    Action<AdaptiveChannelPoolBuilder> configure)
{
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configure);

    var chBuilder = new AdaptiveChannelPoolBuilder();
    configure(chBuilder);

    // ConditionalWeakTable lives in the closure captured by the AddAdaptivePool delegate.
    // It is created once per pool instance — same lifetime as the pool itself.
    var leaseMap = new ConditionalWeakTable<IChannel, IPoolItem<IConnection>>();

    services.AddAdaptivePool<IChannel>(name, builder =>
    {
        builder
            .Factory(async (sp, ct) =>
            {
                var connectionPool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>(connectionPoolName);
                var connLease = await connectionPool.AcquireAsync(ct).ConfigureAwait(false);
                try
                {
                    var ch = await connLease.Value.CreateChannelAsync(
                        chBuilder.ChannelOptions, ct).ConfigureAwait(false);
                    leaseMap.Add(ch, connLease);
                    return ch;
                }
                catch
                {
                    await connLease.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            })
            .BeforeUse((ch, _) =>
            {
                if (!ch.IsOpen) return ValueTask.FromResult(PoolState.Unhealthy);
                if (leaseMap.TryGetValue(ch, out var connLease) && !connLease.Value.IsOpen)
                    return ValueTask.FromResult(PoolState.Unhealthy);
                return ValueTask.FromResult(PoolState.Healthy);
            })
            .Check((ch, _) =>
                ValueTask.FromResult(ch.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))
            .Release(async (ch, ct) =>
            {
                try { await ch.CloseAsync(ct).ConfigureAwait(false); } catch { /* swallow */ }
                await ch.DisposeAsync().ConfigureAwait(false);
                if (leaseMap.TryGetValue(ch, out var connLease))
                {
                    leaseMap.Remove(ch);
                    await connLease.DisposeAsync().ConfigureAwait(false);
                }
            })
            .WithBounds(chBuilder.MinSize, chBuilder.MaxSize, chBuilder.InitialSize)
            .IdleTimeout(chBuilder.IdleTimeout);
    });

    return services;
}
```

[VERIFIED: `IConnection.CreateChannelAsync(CreateChannelOptions? options = null, CancellationToken cancellationToken = default)` — confirmed via WebSearch on rabbitmq.github.io API docs]
[VERIFIED: Core's `IAdaptivePool<T>.AcquireAsync(CancellationToken)` returns `ValueTask<IPoolItem<T>>` — src/Oragon.AdaptivePool.Core/Abstractions/IAdaptivePool.cs:32]
[VERIFIED: Core's `IPoolItem<T>` exposes `.Value` (NOT `.Object` as ARCHITECTURE.md draft showed) — src/Oragon.AdaptivePool.Core/Abstractions/IPoolItem.cs:9. The ARCHITECTURE.md sample is stale on this naming.]
[VERIFIED: Core's `name`-based DI uses `services.GetRequiredKeyedService<IAdaptivePool<T>>(name)` — confirmed via ServiceCollectionExtensions.cs:39]

### Pattern 3: `CreateChannelOptions` defaults (per CONTEXT and Pitfall 13)

**What:** Default the channel pool to `PublisherConfirmationsEnabled = true, PublisherConfirmationTrackingEnabled = true`, configurable via builder. Default `consumerDispatchConcurrency = 1` (irrelevant for publishers but a defensive value).

**Example:**

```csharp
// Source: rabbitmq.github.io API docs (verified) — CreateChannelOptions ctor.
public sealed class AdaptiveChannelPoolBuilder
{
    public CreateChannelOptions ChannelOptions { get; private set; } =
        new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true,
            outstandingPublisherConfirmationsRateLimiter: null,
            consumerDispatchConcurrency: 1);

    public AdaptiveChannelPoolBuilder WithChannelOptions(CreateChannelOptions options)
    { ChannelOptions = options ?? throw new ArgumentNullException(nameof(options)); return this; }
    // ... MinSize / MaxSize / InitialSize / IdleTimeout / MaxChannelsPerConnection
}
```

[VERIFIED: `CreateChannelOptions` ctor signature `public CreateChannelOptions(bool publisherConfirmationsEnabled, bool publisherConfirmationTrackingEnabled, RateLimiter? outstandingPublisherConfirmationsRateLimiter = null, ushort? consumerDispatchConcurrency = 1)` — confirmed via WebSearch result on rabbitmq.github.io/rabbitmq-dotnet-client/api/RabbitMQ.Client.CreateChannelOptions.html]

### Pattern 4: Testcontainers integration test fixture

**What:** Spin up RabbitMQ 4 management image once per test class via xUnit `IClassFixture` + `IAsyncLifetime`. Hand the connection URI to a fresh `ServiceCollection` per test method.

**Example:**

```csharp
// Source: dotnet.testcontainers.org/modules/rabbitmq/ + xUnit v3 IAsyncLifetime pattern.
public sealed class RabbitMqContainerFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } =
        new RabbitMqBuilder()
            .WithImage("rabbitmq:4-management")
            // Default user/pass are set by the module; container picks a random host port.
            .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async ValueTask InitializeAsync() => await Container.StartAsync();
    public async ValueTask DisposeAsync()    => await Container.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class ConnectionPoolIntegrationTests(RabbitMqContainerFixture fx)
    : IClassFixture<RabbitMqContainerFixture>
{
    [Fact]
    public async Task ConnectionPool_acquire_returns_open_connection_and_release_closes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdaptiveConnectionPool(
            name: "default",
            configureFactory: f => f.Uri = new Uri(fx.ConnectionString),
            configurePool: p => p.WithBounds(min: 0, max: 4, initial: 1));

        await using var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredKeyedService<IAdaptivePool<IConnection>>("default");

        await using (var lease = await pool.AcquireAsync(TestContext.Current.CancellationToken))
        {
            lease.Value.IsOpen.ShouldBeTrue();
        }

        // After dispose, the connection is back in the pool — IsOpen still true (pool keeps it).
        pool.Available.ShouldBe(1);
    }
}
```

[VERIFIED: `Testcontainers.RabbitMq` 4.11.0 ships `RabbitMqBuilder().Build()` returning `RabbitMqContainer` with `StartAsync` + `GetConnectionString()` — confirmed via dotnet.testcontainers.org/modules/rabbitmq/]
[ASSUMED: xUnit v3's `IAsyncLifetime` returns `ValueTask` (was `Task` in v2). Per xUnit v3 migration this is correct, but verify on first compile. If wrong, wrap with `Task.CompletedTask`.]

### Pattern 5: BurstyPublisher sample architecture

**What:** A `BackgroundService`-hosted worker cycling: 5 min idle → 30 s burst (10k publishes via `Parallel.ForEachAsync`) → 5 min idle, repeated 3 times. Each publish acquires a fresh channel, awaits `BasicPublishAsync` (publisher confirms), disposes channel.

**Example:**

```csharp
// Source: Composed from RabbitMQ tutorial-seven-dotnet + sister Oragon.RabbitMQ conventions.
public sealed class BurstyPublisherWorker(
    [FromKeyedServices("default")] IAdaptivePool<IChannel> channelPool,
    ILogger<BurstyPublisherWorker> logger)
    : BackgroundService
{
    private const string Exchange = "oragon.adaptivepool.sample";
    private const string RoutingKey = "bursty.demo";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Topology declared once — quick-and-dirty for the sample.
        await using (var setup = await channelPool.AcquireAsync(ct))
        {
            await setup.Value.ExchangeDeclareAsync(
                Exchange, ExchangeType.Direct, durable: true, autoDelete: false,
                arguments: null, cancellationToken: ct);
            await setup.Value.QueueDeclareAsync(
                queue: "oragon.adaptivepool.sample.queue",
                durable: true, exclusive: false, autoDelete: false,
                arguments: null, cancellationToken: ct);
            await setup.Value.QueueBindAsync(
                queue: "oragon.adaptivepool.sample.queue",
                exchange: Exchange, routingKey: RoutingKey,
                arguments: null, cancellationToken: ct);
        }

        for (int cycle = 0; cycle < 3 && !ct.IsCancellationRequested; cycle++)
        {
            logger.LogInformation("Cycle {Cycle}: idle for 5 min", cycle);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);

            logger.LogInformation("Cycle {Cycle}: BURST 10000 publishes", cycle);
            var sw = Stopwatch.StartNew();
            await Parallel.ForEachAsync(
                Enumerable.Range(0, 10_000),
                new ParallelOptions { MaxDegreeOfParallelism = 256, CancellationToken = ct },
                async (i, token) =>
                {
                    await using var ch = await channelPool.AcquireAsync(token);
                    var body = JsonSerializer.SerializeToUtf8Bytes(new { Idx = i, Cycle = cycle });
                    await ch.Value.BasicPublishAsync(
                        exchange: Exchange,
                        routingKey: RoutingKey,
                        mandatory: false,
                        basicProperties: new BasicProperties { Persistent = true },
                        body: body,
                        cancellationToken: token);
                });
            sw.Stop();
            logger.LogInformation("Cycle {Cycle}: burst done in {Elapsed} ms (size={Size}, available={Available})",
                cycle, sw.ElapsedMilliseconds, channelPool.Available + channelPool.InUse, channelPool.Available);
        }
    }
}
```

[VERIFIED: `BasicPublishAsync` accepts a `CancellationToken` — confirmed in WebSearch result discussing publisher confirms]
[VERIFIED: `BasicProperties` is instantiated directly via `new BasicProperties { ... }` (not `CreateBasicProperties()`) in v7 — confirmed via Phase 1 STACK.md research line 65]
[ASSUMED: `ExchangeDeclareAsync`, `QueueDeclareAsync`, `QueueBindAsync` exist on `IChannel` with similar signatures (replacing v6 `IModel.ExchangeDeclare` etc.). The v7 migration guide states "all public methods are async-first" — but the exact parameter ordering should be verified at first compile. The Phase 3 task author should consult `RabbitMQ.Client` v7.2.1 IntelliSense and the migration guide directly.]

### Anti-Patterns to Avoid

- **Sharing one channel across publishers:** RabbitMQ explicitly forbids concurrent publishing on a single `IChannel` — frame interleaving causes broker-side `FRAMING_ERROR`. The pool **dimensions by concurrency**: each publish operation acquires its own channel. The sample MUST demonstrate this, not the inverse. [CITED: PITFALLS.md Pitfall 10]
- **Letting `AutomaticRecoveryEnabled = true`:** RabbitMQ.Client's transparent reconnection conflicts with the pool's discard-and-replace lifecycle. Force `false` and document. [CITED: PITFALLS.md Pitfall 9]
- **Calling a server round-trip in `BeforeUse`:** Adds 50 ms+ to every Acquire. Use only `IsOpen` (zero-cost in-process boolean read). [CITED: PITFALLS.md Pitfall 6]
- **Letting all channels concentrate on a single connection:** RabbitMQ default `channel_max = 2047`. The channel pool factory should keep a soft "channels per connection" target (default 100) and acquire a NEW connection from the inner pool when the current one is at the soft limit. [CITED: PITFALLS.md Pitfall 11; CONTEXT.md "Channel-per-connection ceiling default = 100"]
- **Running Testcontainers in CI without checking Docker availability:** The integration test class should use `[Trait("Category", "Integration")]` so unit-test runs (no Docker) skip them by filter; CI runs add `--filter "Category=Integration"`.
- **Returning `IChannel` directly from `IPoolItem<IChannel>.Value` and forgetting to dispose:** Already handled by Core's wrapper, but tests should explicitly assert that not disposing leaks a channel and triggers the finalizer-warning path (per Pitfall 2). For Phase 3 this is an integration test scenario, not new behavior.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---|---|---|---|
| Connection lifecycle (TCP/AMQP handshake, heartbeat, close) | Custom AMQP client | `RabbitMQ.Client` 7.2.1 | The official client handles 50+ edge cases (TLS, SASL, broker close codes, frame parsing) |
| Pairing channels with their parent connection | `Dictionary<IChannel, IPoolItem<IConnection>>` (strong refs) | `ConditionalWeakTable<TKey, TValue>` | Strong refs leak if the IChannel is forgotten; CWT's weak keying lets dropped channels be GC'd cleanly. [VERIFIED: CWT signature in BCL] |
| Spinning up a RabbitMQ broker in tests | docker-compose script | `Testcontainers.RabbitMq` 4.11.0 | The lib's own canonical example uses Testcontainers; cleaner cross-platform CI; isolated per-test-class instance |
| Publisher confirms tracking | Hand-rolled sequence-number map | `CreateChannelOptions { PublisherConfirmationsEnabled = true, PublisherConfirmationTrackingEnabled = true }` | v7 client tracks confirms and throws `PublishException` on Nack/Return — exactly what we want |
| `IConnection` keyed-by-name resolution | Custom registry singleton | `services.AddKeyedSingleton<IConnectionFactory>(name, ...)` (.NET 8+ keyed services) | Standard since .NET 8; Aspire-friendly; multi-broker just works |
| Cross-pool eager invalidation (callback when connection dies) | Event/callback API on Core | **Defer to v2 — use lazy `BeforeUse` re-probe via `ConditionalWeakTable`** | Eager invalidation requires Core API surface (event) or a complex reverse-index in the adapter. Lazy re-probe is sufficient for v1: when channel pool's `BeforeUse` runs, it checks `connLease.Value.IsOpen` and discards if dead. The window between "connection died" and "next acquire of a channel on it" is bounded by the consumer's borrow rate. [Recommendation: see Q1] |

**Key insight:** Core already exposes everything the adapter needs through public hooks — keep adapter code tight and avoid pushing back into Core unless an integration test surfaces a real gap (per CONTEXT.md success criterion 5).

## Common Pitfalls

These are extracted from `.planning/research/PITFALLS.md` (Pitfalls 9–13 are RabbitMQ-specific, Pitfalls 5–6 are health-check pitfalls that apply here, Pitfalls 1–2 from Core apply transitively). All have already been investigated; this section recapitulates the **mitigations the adapter must encode**.

### Pitfall A: AutomaticRecoveryEnabled vs pool's discard-replace (Pitfall 9)

**Mitigation:** `AddAdaptiveConnectionPool` overrides `factory.AutomaticRecoveryEnabled = false` after the consumer's `configureFactory` runs. Logs Warning if previously true. Document in XML doc on the extension method and in README.

### Pitfall B: Channel sharing for concurrent publishing (Pitfall 10)

**Mitigation:** Sample uses `Parallel.ForEachAsync` with each iteration acquiring its OWN channel. README anti-pattern section explicit. Optional debug-build proxy that throws on second concurrent call from a different thread is **deferred** (v2).

### Pitfall C: Channel-per-connection ceiling (Pitfall 11)

**Mitigation:**
1. `AdaptiveChannelPoolBuilder.MaxChannelsPerConnection` (default 100, soft limit, well below broker's 2047 default).
2. Channel pool's Factory hook tracks "channels per connection" via `ConcurrentDictionary<IConnection, int>`. When the leased connection is at limit, the factory calls `connectionPool.AcquireAsync` again to get a different connection, releasing the saturated one back. **The simple version (no tracking) is acceptable for v1**: relying on the connection pool's own elasticity to grow when channel pool growth pressure cascades — but document the tradeoff. The spread-test (`channel_max=10`) below validates this.
3. Integration test forces low `channel_max=10` via Testcontainers env var (`RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS` or `definitions.json`) and asserts connection count > 1 when publishing 50 simultaneously.

### Pitfall D: Heartbeat misconfiguration (Pitfall 12)

**Mitigation:** Default `RequestedHeartbeat = TimeSpan.FromSeconds(60)` (RabbitMQ.Client default — don't change). If consumer sets > 2 min, log Warning. Document NAT/firewall implications in README.

### Pitfall E: Publisher confirms with concurrent publishes on same channel (Pitfall 13)

**Mitigation:** Same as Pitfall B — pool channels per publish operation. Default `CreateChannelOptions` enables tracking so each channel independently correlates its in-flight publish.

### Pitfall F: Health-check sweep amplifies load (Pitfall 5)

**Mitigation:** `Check` hook reads `IsOpen` only — no server round-trip. Pool's existing exponential backoff (Phase 2) handles cascade. No adapter-specific code needed.

### Pitfall G: Health check inside Acquire (Pitfall 6)

**Mitigation:** `BeforeUse` reads `IsOpen` and (for channels) checks `connLease.Value.IsOpen` via the weak-table lookup — both are O(1) in-process boolean reads. No I/O.

### Pitfall H: Connection.IsOpen reliability — silent dead connections

**What goes wrong:** Per RabbitMQ docs and community reports, `IsOpen == true` can persist briefly after a network partition until the next heartbeat misses (up to 2× heartbeat interval = 120 s with default). Consumer borrows what looks like a healthy connection, then the publish fails.

**Mitigation:**
1. Accept this as a known limitation; document in README ("worst-case detection latency is ~120 s with default heartbeat — tune heartbeat shorter for stricter SLAs").
2. Background `Check` hook is the primary line of defense — if a connection's `IsOpen` flips to false during sweep, the failure policy discards it before any consumer borrows it again.
3. Consumer-side retry (Polly + `await pool.AcquireAsync`) handles the rare race window.

[CITED: rabbitmq.com/client-libraries/dotnet-api-guide and PITFALLS.md "IConnection.IsOpen reliability" notes (LOW confidence on quantitative timing — verified directionally only)]

## Code Examples

(Already covered inline in Architecture Patterns 1–5 above with provenance tags. No additional examples needed.)

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|---|---|---|---|
| `IModel` interface | `IChannel` (rename) | RabbitMQ.Client 7.0 (released 2024) | All v6-and-earlier samples mislead — must consult v7 migration guide [VERIFIED: STACK.md line 62, v7-MIGRATION.md] |
| Sync `factory.CreateConnection()` + `connection.CreateModel()` | `factory.CreateConnectionAsync(ct)` + `connection.CreateChannelAsync(opts, ct)` | RabbitMQ.Client 7.0 | Pool's hook contract (`ValueTask`-returning, ct-accepting) maps directly [VERIFIED: rabbitmq.github.io API docs] |
| `channel.ConfirmSelect()` post-creation | `new CreateChannelOptions(publisherConfirmationsEnabled: true, ...)` at creation time | RabbitMQ.Client 7.0 | Adapter sets opinionated default; consumer overrides via `WithChannelOptions` [VERIFIED: API docs] |
| `channel.CreateBasicProperties()` | `new BasicProperties { ... }` | RabbitMQ.Client 7.0 | Sample uses direct construction [VERIFIED: STACK.md line 67] |
| Manual TCP heartbeat tuning experiments | Default 60 s; documented NAT-timeout interplay | RabbitMQ.Client 6.x → 7.x (no change in default) | Adapter keeps default; warns on >2 min |
| FluentAssertions for tests | AwesomeAssertions 9.4.0 (Apache-2 fork of FA v7) | Jan 2025 (FA license change) | Already adopted in Phase 1+2 [VERIFIED: Directory.Packages.props line 30] |

**Deprecated/outdated:**
- Anything using `IModel` — gone in v7
- `BasicConsumer` (replaced by `IAsyncBasicConsumer` exclusively)
- Pre-v7 blog posts showing shared-channel publishing — wrong even in v6, now also non-compiling

## Assumptions Log

> Listed for the planner / discuss-phase to confirm before locking in.

| # | Claim | Section | Risk if Wrong |
|---|---|---|---|
| A1 | xUnit v3's `IAsyncLifetime` returns `ValueTask` (not `Task` like v2). | Pattern 4 fixture | Compile error on first build; trivially fixed by changing return type. LOW risk. |
| A2 | `IChannel.ExchangeDeclareAsync` / `QueueDeclareAsync` / `QueueBindAsync` exist with parameters matching v6 sync versions but `Async`-suffixed and `CancellationToken`-accepting. | Pattern 5 sample | Sample won't compile if signatures differ; planner's task spec should require IntelliSense check. MEDIUM risk. |
| A3 | `Testcontainers.RabbitMq` 4.11.0's `RabbitMqBuilder().WithImage("rabbitmq:4-management").Build()` produces a container whose `GetConnectionString()` returns an `amqp://...` URI parseable by `ConnectionFactory.Uri`. | Pattern 4 fixture | If URI shape is non-standard (e.g., includes management UI), the integration test fails at first publish. Verify on first compile. LOW risk — module is mature. |
| A4 | The simple "no tracking" channel-spread strategy (relying on connection-pool elasticity to grow naturally) suffices for v1 — explicit per-connection tracking can wait for v2. | Pitfall C mitigation | If broker's `channel_max` is hit before connection pool grows, integration test fails. Forces eager spreading logic into v1. MEDIUM risk — the `channel_max=10` integration test will surface this. |
| A5 | `IConnection.IsOpen` worst-case false-positive window is ~2× heartbeat interval (~120 s default). | Pitfall H | Quantitative claim is from informal docs/community reports, not a contract. Document as approximate, not a guarantee. LOW risk for adapter design. |
| A6 | The sample's `Parallel.ForEachAsync` with `MaxDegreeOfParallelism=256` is a reasonable default that exercises the pool meaningfully without overwhelming a typical dev laptop. | Pattern 5 | If too high, dev box crashes; if too low, demo is unconvincing. Tunable via env var. LOW risk. |
| A7 | The "lazy invalidation only" strategy for cross-pool channel/connection death is acceptable for Phase 3 v1 (defer eager-invalidation to v2). | Don't Hand-Roll table; Open Question Q1 | If real workloads see consumers borrowing a "healthy" channel whose underlying connection died seconds before, perception of bugginess. Mitigated by `BeforeUse` re-probing both. MEDIUM risk — explicit gap that REQ-RMQ-02 success criterion 2 should be validated against. |

## Open Questions

### Q1. Cross-pool eager invalidation: Phase 3 v1 or defer to v2?

**What we know:**
- CONTEXT.md describes an eager mechanism: when the connection pool discards a connection, the channel pool is notified via callback and pre-marks all channels on that connection as `Unhealthy`.
- ARCHITECTURE.md describes only the lazy mechanism: channel pool's `BeforeUse` re-probes `connLease.Value.IsOpen` via the weak-table.
- Core has no public "OnItemDiscarded" event today (Phase 1+2 don't expose this).

**What's unclear:**
- Is the eager path needed for v1, or does the lazy path satisfy REQ-RMQ-02 success criteria?
- If eager IS needed, is the right shape (a) a new public Core API (`IAdaptivePool<T>.ItemDiscarded` event), or (b) a private adapter-side reverse-index (`ConcurrentDictionary<IConnection, ImmutableHashSet<IChannel>>`)?

**Recommendation:** **Ship lazy-only for Phase 3 v1.** Rationale:
1. CONTEXT.md success criterion 5 explicitly says: "If any Core API gap is surfaced ... it is resolved by refactoring Core BEFORE proceeding to Phase 4 — adapter does NOT add Core abstractions itself." Choosing eager forces a Core API decision; choosing lazy first lets the integration test reveal whether the gap is real.
2. Lazy semantics: when a connection dies under live channels, the next time any of those channels is acquired (`BeforeUse`), the weak-table lookup detects `connLease.Value.IsOpen == false` and the failure policy discards. The exposure window is bounded by the consumer's borrow rate.
3. If the integration test exposes a noticeable defect from the lazy path (e.g., bursty consumer hits 100 dead channels in a row before discard catches up), pivot to the eager path before Phase 4 — exactly per the phase mandate.

**Defer the decision** until after the integration test runs. Plan Task should encode this as a checkpoint: "after BurstyPublisher integration test runs once, evaluate whether eager invalidation is needed; if yes, design Core event API and add a follow-up plan."

### Q2. Should channel-spread be eager (per-connection tracking) or implicit (connection-pool elasticity catches up)?

**What we know:**
- CONTEXT.md mandates `MaxChannelsPerConnection = 100` default, with a `channel_max=10` integration test asserting connection spread.
- Implicit strategy: when channel pool needs to create channel #11 on connection-A, the Factory just calls `connectionPool.AcquireAsync()` again — if the connection pool gives back connection-A again (because it's the only one), eventually the broker rejects with `NOT_ALLOWED`, the failure policy fires, and the connection pool grows.
- Eager strategy: channel pool's Factory tracks `Dictionary<IConnection, int>` and forces a new connection acquire when the count nears `MaxChannelsPerConnection`.

**What's unclear:** Will the implicit strategy converge fast enough to pass the `channel_max=10` test, or will it produce a discard-storm before stabilizing?

**Recommendation:** Implement the **eager strategy** in v1. The cost is small (one `ConcurrentDictionary` and a count check in the Factory hook); the benefit is the integration test passes deterministically. Document as `MaxChannelsPerConnection` builder option.

### Q3. Should the adapter expose a builder helper for sensible `ConnectionFactory` defaults?

**What we know:**
- CONTEXT.md "Claude's Discretion": "Whether to expose `ConnectionFactoryDefaults` builder helper (set sensible defaults like `Heartbeat=60s`)."

**Recommendation:** **No** — `ConnectionFactory`'s own defaults are already sensible (60 s heartbeat, automatic recovery on, dispatch concurrency 1). The adapter only needs to flip `AutomaticRecoveryEnabled = false`. Adding a wrapper would create a parallel API surface to maintain for marginal benefit. If users want different defaults, they configure them via `configureFactory` closure or `IOptions`.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|---|---|---|---|---|
| .NET 10 SDK | Build host | (assumed available — Phase 1+2 use it) | 10.0.x | — |
| Docker | Testcontainers integration tests + sample | Verify on developer/CI box | — | Skip integration tests with `--filter "Category!=Integration"` |
| RabbitMQ.Client 7.2.1 NuGet | Adapter package | Yes — published, mainstream | 7.2.1 | None — required |
| Testcontainers.RabbitMq 4.11.0 NuGet | Integration tests + sample | Yes — published 2026-03-12 | 4.11.0 | docker-compose script (more friction) |
| Microsoft.Extensions.Hosting 10.0.6 | Sample's BackgroundService | Already in props | 10.0.6 | None |

**Missing dependencies with no fallback:** None for Phase 3 itself; CI must have Docker for integration tests, but unit tests (NSubstitute mocks) don't need it.

**Missing dependencies with fallback:** Docker — fallback is `--filter "Category!=Integration"` on the test runner.

## Project Constraints (from CONTEXT.md)

> CLAUDE.md is global RTK (Rust Token Killer) tooling instructions only — no project-specific directives that constrain Phase 3 design. Constraints below are entirely from CONTEXT.md.

### Locked Decisions (must not be revisited)

- **Layered composition strategy:** channel pool's `Factory` calls `connectionPool.AcquireAsync` internally; pairing via `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>`.
- **`MaxChannelsPerConnection` default = 100**, configurable.
- **`AutomaticRecoveryEnabled = false` always**, with Warning log on override.
- **Cross-pool channel-orphan handling:** notify channel pool when connection discarded — but per Q1, the **lazy** form ships in v1 and the eager form is deferred unless integration test surfaces a defect.
- **DI extension surface:**
  - `services.AddAdaptiveConnectionPool(name, configureFactory, configurePool)`
  - `services.AddAdaptiveChannelPool(name, connectionPoolName, configureChannelPool)`
- **3-mode `IConnectionFactory` resolution** in priority order: keyed singleton → closure → IOptions.
- **`pool.name` default = `string.Empty`** (consistent with Core).
- **Testcontainers image:** `rabbitmq:4-management`.
- **Fixture pattern:** `IClassFixture<RabbitMqContainer>` per test class.
- **Sample location:** `samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/`.
- **Sample scenario:** 5 min idle → 30 s burst of ~100k → 5 min idle, repeated 3×.
- **`AfterUse` hook MUST NOT swallow publisher-confirms exceptions** (per Pitfall 12 reasoning).
- **Conventions aligned with sister `Oragon.RabbitMQ`:** fluent builder, factory pattern, DI-first, RabbitMQ.Client v7+ async-first.

### Claude's Discretion (research recommends)

- **Cross-pool invalidation shape:** *Recommendation* — lazy `BeforeUse` re-probe via weak-table for v1; eager callback deferred to v2 unless integration test forces it (Q1).
- **Warning message + log level for AutomaticRecoveryEnabled override:** *Recommendation* — `LogLevel.Warning`, message points at a docs URL.
- **3-mode probe strategy:** *Recommendation* — runtime check inside the `Factory` delegate (probe each mode in order, throw if all three fail with a clear message). Avoid `IServiceCollection` scan at registration time — it adds complexity for little benefit.
- **Naming `AdaptiveConnectionPoolOptions` / `AdaptiveChannelPoolOptions`:** *Recommendation* — match Core convention (`AdaptivePoolOptions<T>` is internal-ish, public surface is the builder). Use the names verbatim — they read naturally and self-document.
- **Sample message shape:** *Recommendation* — small JSON `{Idx, Cycle}` (~30 bytes), exchange `"oragon.adaptivepool.sample"`, queue `"oragon.adaptivepool.sample.queue"`, durable + classic queue (avoid quorum for the demo — quorum requires 3-node cluster). Exchange type direct.
- **Whether to expose `ConnectionFactoryDefaults` helper:** *Recommendation* — **NO** (see Q3).

### Deferred Ideas (OUT OF SCOPE for Phase 3)

- HttpClient adapter
- Npgsql/DbConnection adapter
- Aspire integration package `Oragon.AdaptivePool.RabbitMQ.AspireClient`
- Per-tenant/keyed sub-pools (vhost-keyed)
- Built-in publisher confirms wrapper (adapter doesn't interfere with confirms; AfterUse never swallows confirm exceptions)
- Connection string parsing helper
- HealthChecks integration (`AddHealthChecks().AddRabbitMQ()` pool-aware)
- Eager cross-pool invalidation callback (deferred to v2 — see Q1)
- Debug-build "channel-thread-confined" proxy that throws on second concurrent call
- Conditional/IOptions-based broker selection at runtime

## Phase Requirements

| ID | Description | Research Support |
|---|---|---|
| RMQ-01 | `services.AddAdaptiveConnectionPool(name, configure)` configuring pool of `IConnection` with `BeforeUse`/`Check` baseados em `IsOpen`, `Release` chamando `CloseAsync()`, e `AutomaticRecoveryEnabled = false` por padrão | Architecture Pattern 1 (full code sketch); Pitfall A mitigation; verified RabbitMQ.Client API for IsOpen + CloseAsync + AutomaticRecoveryEnabled |
| RMQ-02 | `services.AddAdaptiveChannelPool(name, configure)` configurando pool de `IChannel` em camada (factory adquire connection do pool interno via `ConditionalWeakTable` para pareamento, release fecha channel e devolve connection) | Architecture Pattern 2 (full code sketch); Pitfall C mitigation; verified Core's `IPoolItem<T>.Value` API |
| RMQ-03 | Sample executável publicador-bursty demonstrando ciclo "algumas/hora → centenas-de-milhares simultâneas → ocioso" usando o pool em camadas | Architecture Pattern 5 (full BackgroundService sketch); Testcontainers Pattern 4 for end-to-end runnable; project structure section |
| RMQ-04 | Convenções de nomenclatura, builder, e DI consistentes com `Oragon.RabbitMQ` (sister library para o lado consumidor) | Pattern 1+2 follow `services.AddAdaptive...` naming; fluent builder mirrors Core's `AdaptivePoolBuilder<T>`; namespace `Oragon.AdaptivePool.RabbitMQ.*` matches sister-library prefix |

## Sources

### Primary (HIGH confidence)

- **Core source code (verified directly):**
  - `src/Oragon.AdaptivePool.Core/Abstractions/IAdaptivePool.cs` — `IAdaptivePool<T>` surface (Acquire, AcquireAsync, MaxSize/MinSize/Available/InUse, ReadyAsync)
  - `src/Oragon.AdaptivePool.Core/Abstractions/IPoolItem.cs` — `.Value` property (NOT `.Object`)
  - `src/Oragon.AdaptivePool.Core/Abstractions/PoolState.cs` — `Healthy` / `Unhealthy` enum
  - `src/Oragon.AdaptivePool.Core/Hooks/HookDelegates.cs` — exact delegate signatures (all `ValueTask`-returning, `CancellationToken`-accepting, `BeforeUse` returns `PoolState`)
  - `src/Oragon.AdaptivePool.Core/DependencyInjection/ServiceCollectionExtensions.cs` — keyed-singleton + non-keyed fallback for empty name
  - `src/Oragon.AdaptivePool.Core/Builder/AdaptivePoolBuilder.cs` — fluent surface (Factory, BeforeUse, Check, AfterUse, Release, WithBounds, IdleTimeout, etc.)
  - `Directory.Packages.props` — actual versions (xunit.v3 3.2.2, AwesomeAssertions 9.4.0, M.E.* 10.0.6, Hosting present)
- **CONTEXT.md** (`.planning/phases/03-rabbitmq-adapter/03-CONTEXT.md`) — locked decisions
- **ARCHITECTURE.md** (`.planning/research/ARCHITECTURE.md` lines 368–488) — adapter composition pattern (note: uses outdated `.Object` naming — corrected to `.Value` in this research)
- **PITFALLS.md** (`.planning/research/PITFALLS.md` Pitfalls 9, 10, 11, 12, 13) — RabbitMQ-specific mitigations
- **STACK.md** (`.planning/research/STACK.md` lines 53–69) — RabbitMQ.Client v7 specifics confirmed
- **rabbitmq.github.io ConnectionFactory API** (https://rabbitmq.github.io/rabbitmq-dotnet-client/api/RabbitMQ.Client.ConnectionFactory.html) — verified `CreateConnectionAsync` overloads, `AutomaticRecoveryEnabled` default true, `RequestedHeartbeat` default 60 s
- **rabbitmq.github.io CreateChannelOptions API** (https://rabbitmq.github.io/rabbitmq-dotnet-client/api/RabbitMQ.Client.CreateChannelOptions.html via WebSearch) — verified ctor signature with publisher-confirmation flags
- **dotnet.testcontainers.org/modules/rabbitmq/** — verified `RabbitMqBuilder` + `IAsyncLifetime` pattern + `GetConnectionString()`
- **nuget.org/packages/Testcontainers.RabbitMq** — verified version 4.11.0 published 2026-03-12, supports net8/net9/net10
- **rabbitmq.com/client-libraries/dotnet-api-guide** — channel-sharing forbidden quote; recovery section reference

### Secondary (MEDIUM confidence)

- **rabbitmq.com/tutorials/tutorial-seven-dotnet** — publisher-confirms patterns informing sample design
- **github.com/rabbitmq/rabbitmq-dotnet-client/discussions/1720** — v7 migration discussion
- **deepwiki.com/rabbitmq/rabbitmq-dotnet-client** — community-aggregated troubleshooting

### Tertiary (LOW — flagged for verification)

- Quantitative claim that `IConnection.IsOpen` false-positive window is ~120 s with default heartbeat (Pitfall H) — based on community reports, not a contract. Direction is confirmed; precise timing depends on broker + network.
- xUnit v3 `IAsyncLifetime` returning `ValueTask` — verify on first compile (Assumption A1).
- v7 `IChannel.ExchangeDeclareAsync` exact parameter ordering — verify on first compile (Assumption A2).

## Metadata

**Confidence breakdown:**
- Standard stack (RabbitMQ.Client 7.2.1, Testcontainers 4.11.0, Core dependencies): HIGH — versions verified live against NuGet/official docs
- Architecture (layered composition, ConditionalWeakTable, three-mode resolution, AutomaticRecoveryEnabled override): HIGH — patterns verified against Core source code and PITFALLS.md
- Pitfalls: HIGH — taxonomy already established in PITFALLS.md, mitigations directly mapped
- RabbitMQ.Client v7 API specifics (method signatures, parameter ordering for declare-style ops): MEDIUM — top-level signatures verified, leaf parameter ordering is Assumption A2

**Research date:** 2026-05-02
**Valid until:** 2026-06-01 (30 days; the only fast-moving piece is Testcontainers.RabbitMq, which has been on a stable monthly cadence)
