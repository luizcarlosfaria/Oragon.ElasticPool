# Feature Research

**Domain:** .NET adaptive object/connection pool library (Oragon.AdaptivePool + RabbitMQ adapter)
**Researched:** 2026-05-02
**Confidence:** HIGH (synthesis from mature pools across JVM, Node, .NET, plus RabbitMQ-specific guidance)

## Cross-Ecosystem Reference Set

To anchor "table stakes" in real, mature implementations rather than opinion, the recommendations below are calibrated against:

- **HikariCP** (JVM, default in Spring Boot) — the gold standard for high-perf connection pools; bytecode-tuned, JMX-instrumented, conservative API, "keepalive" probes on idle connections.
- **Apache commons-pool2 / GenericObjectPool** (JVM) — the prototypical generic factory-based pool with full lifecycle (`makeObject`/`activateObject`/`validateObject`/`passivateObject`/`destroyObject`), idle eviction thread, abandoned-object detection.
- **node generic-pool** (Node.js) — small, focused API with `min`/`max`, `acquireTimeoutMillis`, `idleTimeoutMillis`/`softIdleTimeoutMillis`, `testOnBorrow`, eviction interval, factory `create`/`destroy`/`validate`.
- **Microsoft.Extensions.ObjectPool** (.NET) — what the .NET ecosystem currently has: fixed `MaximumRetained`, no health checks, no elasticity, no metrics, recommended only for cheap-to-init reusable objects (e.g., `StringBuilder`).
- **Polly v8 resilience pipelines** (.NET) — orthogonal but adjacent: provides Retry / Circuit Breaker / Hedging / Timeout / Bulkhead / RateLimiter as composable strategies. Polly does NOT pool — it just adds resilience to calls.
- **Reactor Pool** (JVM) — modern reactive pool with explicit `GracefulShutdownInstrumentedPool` decorator (drain + grace period).

## Feature Landscape

### Table Stakes (Users Expect These)

Missing any of these and a serious .NET dev evaluating an OSS pool library in 2026 will reject it within 60 seconds of reading the README.

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| Generic `Pool<T>` with factory delegate | Universal pool API since commons-pool / generic-pool / `Microsoft.Extensions.ObjectPool` | LOW | The non-negotiable baseline. |
| `Acquire` / `Release` (or `IDisposable` wrapper) | Every pool ever | LOW | Project chose `IPoolItem<T>` disposable wrapper — ergonomic, prevents leaks. |
| Async-first API (`AcquireAsync(CancellationToken)`) | .NET 8/9/10 expectation; resource creation is I/O-bound | LOW | `ValueTask<IPoolItem<T>>` for hot path. Sync `Acquire()` only when free item exists, per PROJECT.md. |
| `MinSize` / `MaxSize` bounds | Every pool — `Microsoft.Extensions.ObjectPool` is the outlier (no min, "retained" not "max in flight") and is criticized for it | LOW | |
| Thread-safety under concurrent Acquire/Release | Pools are concurrency primitives by nature | MEDIUM | Validate via stress tests (PROJECT.md already lists this). Use lock-free where possible (e.g., `ConcurrentQueue`/`Channel<T>` for waiters). |
| Acquire timeout (`AcquireTimeout` / via CancellationToken) | generic-pool `acquireTimeoutMillis`, HikariCP `connectionTimeout` | LOW | CancellationToken covers it idiomatically — avoid duplicating with a separate option unless adding pool-level default. |
| Idle eviction (shrink to `MinSize` after `IdleTimeout`) | generic-pool, commons-pool2, HikariCP `idleTimeout`, sqlalchemy pool_recycle | MEDIUM | Background sweeper. Evict in batches; never below `MinSize`. |
| On-borrow validation (`testOnBorrow` / `BeforeUse` hook) | commons-pool2 default, generic-pool, HikariCP connection-test | LOW | Already in PROJECT.md spec. |
| Object factory + destroy (Factory + Release hooks) | commons-pool2 `makeObject`/`destroyObject`, generic-pool `create`/`destroy` | LOW | PROJECT.md covers both. |
| `IDisposable` / `IAsyncDisposable` on the pool itself | .NET BCL convention; releases all pooled items | LOW | Must drain on dispose. |
| DI integration (`services.AddAdaptivePool<T>(...)`) | .NET ecosystem expectation since .NET Core | LOW | Already in PROJECT.md. |
| Built-in metrics via `System.Diagnostics.Metrics.Meter` | OpenTelemetry-friendly is table stakes for serious .NET OSS in 2026 | MEDIUM | Already in PROJECT.md. Standard names: `pool.size`, `pool.available`, `pool.in_use`, `pool.waiting`, `pool.acquire.duration`. |
| Structured logging via `ILogger<T>` | .NET expectation | LOW | State transitions, factory failures, evictions. |
| Cancellation token plumbing through every async path | .NET expectation | LOW | Already in PROJECT.md. |
| Documented quickstart + working sample | OSS adoption depends on README within first scroll | MEDIUM | PROJECT.md acknowledges. |
| Multi-target TFM matrix CI | OSS quality bar; `net8.0`/`net9.0`/`net10.0` all active | MEDIUM | PROJECT.md commits to this. |
| Symbol packages + SourceLink on NuGet | Every serious .NET OSS lib does this in 2026 | LOW | Add to OSS checklist. |

### Differentiators (Competitive Advantage)

These are where Oragon.AdaptivePool earns its keep vs. `Microsoft.Extensions.ObjectPool` and "roll your own."

| Feature | Value Proposition | Complexity | Notes |
|---|---|---|---|
| **Composite-signal elasticity** (wait + utilization + waiter-queue length) | Single-signal elasticity (e.g., HikariCP only grows on demand) is fragile under bursty workloads. Combining signals = the real "adaptive" claim. | HIGH | This is the headline differentiator per PROJECT.md Core Value. Needs careful tuning; expose thresholds. |
| **Five-stage lifecycle hooks** (Factory / BeforeUse / Check / AfterUse / Release) | More granular than commons-pool2's 5-method factory; matches RabbitMQ realities (channel must be opened on a healthy connection, e.g., AfterUse to detect publisher confirms). | MEDIUM | Each hook optional except Factory. |
| **Pluggable failure policy** (`IItemFailurePolicy<T>`) — discard+replace, quarantine+backoff, custom | commons-pool2 only does discard. RabbitMQ wants discard; HTTP clients may want quarantine. Flexibility = adoption beyond v1. | MEDIUM | Ship two built-in policies (Discard, ExponentialBackoffQuarantine). |
| **Layered pools** (channel pool composes connection pool) | RabbitMQ-specific in v1, but the pattern (pool of B where B is built from a pool of A) is reusable. HikariCP doesn't do this; it's strictly L1. | MEDIUM | `IChannel` factory acquires `IConnection` from another pool. Hooks for both layers. |
| **Native OpenTelemetry-first telemetry** (`Meter` + `ActivitySource` + standard semantic names) | Most pools either lack telemetry (MS ObjectPool) or use library-specific APIs (HikariCP HikariMXBean via JMX). OTel-native is the 2026 standard. | MEDIUM | Already in PROJECT.md. Document semantic conventions in README. |
| **Background health sweep** (proactive `Check` hook on idle items) | HikariCP "keepalive" pattern but generalized. Catches dead RabbitMQ connections before the next publisher needs one. | MEDIUM | Already in PROJECT.md spec. |
| **Optional eager warm-up to `InitialSize`** | First-request latency under cold-start (Lambda/Azure Container Apps) is brutal for connection pools. HikariCP offers initialPoolSize. | LOW | Already implied by `InitialSize` in PROJECT.md. Make it `await`-able so apps can warm before serving traffic. |
| **Graceful shutdown / drain** (stop accepting, wait for in-flight, then dispose) | Reactor Pool's `GracefulShutdownInstrumentedPool` is the reference. Critical for k8s SIGTERM handling. | MEDIUM | `DrainAsync(TimeSpan)` returning whether all items returned cleanly. |
| **`PoolItemContext` / state bag on borrowed item** | Hooks need to share state (e.g., BeforeUse measures latency, AfterUse records it). | LOW | Small struct passed to all hooks. |
| **First-class `IServiceProvider` access in hooks** | DI-resolved factory/health checks (e.g., factory needs `IOptions<RabbitOptions>`) | LOW | Already implied by builder taking `IServiceProvider`. |
| **Builder fluent API** (per PROJECT.md sketch) | `Microsoft.Extensions.ObjectPool` requires writing a `PooledObjectPolicy<T>` — verbose. Fluent builder is what `Oragon.RabbitMQ` already does for consistency. | MEDIUM | `AdaptiveObjectPoolFactory.Build<T>(sp, ct).Factory(...).BeforeUse(...).Build()` |
| **Per-item max-lifetime / max-uses** (rotation) | HikariCP `maxLifetime`, sqlalchemy `pool_recycle` — rotates connections to prevent stale-state bugs (server-side timeouts, memory accumulation). | LOW | Optional config. Discard+replace via failure policy when exceeded. |

### Anti-Features (Commonly Requested, Often Problematic)

These will be requested in GitHub issues. Document the "no" with rationale up-front.

| Feature | Why Requested | Why Problematic | Alternative |
|---|---|---|---|
| Distributed pool (Redis/etcd-coordinated cross-process) | "We have 10 pods, why each its own pool?" | Different problem entirely (consensus, network partitions, eviction races). PROJECT.md explicitly out-of-scope. | Use a broker-side limit (RabbitMQ `connection_max`) and per-pod local pools. |
| Persistent pool state across restarts | "Don't want cold start every deploy" | Pool state is process-bound; serialized state of TCP connections is meaningless. | Eager warm-up via `InitialSize` + readiness probe gated on warmup completion. |
| Built-in dashboard UI / web endpoint | "Show me what's in the pool" | Maintenance burden; couples to UI framework; PROJECT.md explicitly out-of-scope. | Emit `Meter` data → Aspire Dashboard / Grafana / App Insights / OTel Collector. |
| Built-in retry/circuit-breaker around `Acquire` | "Add Polly to the pool" | Resilience is orthogonal — wrong layer. Polly + pool compose cleanly. Embedding leaks Polly version into our deps. | Document the recipe: wrap `AcquireAsync` in a Polly pipeline. Consider a tiny `Oragon.AdaptivePool.Polly` glue package later. |
| Generic adapters in v1 (HttpClient / Npgsql / DbConnection / Redis) | "Why only RabbitMQ?" | `HttpClient` already has `IHttpClientFactory` with SocketsHttpHandler pooling. ADO.NET providers pool internally. Duplicating = confusion. | Document explicitly: only pool what isn't already pooled. v2+ may add e.g. `gRPC` channels if community pulls. |
| Automatic per-tenant / per-key sub-pools | "I want a pool keyed by virtual host" | Adds keyed-pool semantics (cf. `GenericKeyedObjectPool`) — significant API surface, easy to misuse. | Caller composes: a `Dictionary<TKey, IAdaptivePool<T>>` works fine. Revisit if heavy demand. |
| "Smart" auto-tuning of `MinSize`/`MaxSize` (ML/heuristic) | "Just figure it out" | Hidden behavior, debuggability nightmare, false confidence. HikariCP's deliberate simplicity is its strength. | Surface metrics; let humans tune. Provide doc on tuning playbook. |
| Synchronous-only API mode | "I'm in a sync codebase" | Forces blocking on async resources (RabbitMQ v7 is async-only); creates deadlock surface. | Provide sync `Acquire()` only for the no-wait path; require async for grow path. PROJECT.md already nailed this. |
| Sharing a single pooled `IChannel` across threads | "Fewer channels = better" | RabbitMQ.Client v7 is "thread-safe to call" but frame interleaving on shared publish can still cause issues; the docs explicitly warn against publish-channel sharing. | Pool channels per-publish-operation; let pool size scale with concurrency. |
| Returning broken items "just in case" | "Don't waste resources" | Defeats auto-cure. Health-check failure must mean discard, not stash. | Failure policy decides discard vs. quarantine; never silently re-queue a broken item. |
| Built-in connection-string parsing for RabbitMQ adapter | "It's a RabbitMQ pool, parse my AMQP URI" | Duplicates `ConnectionFactory.Uri` from RabbitMQ.Client. Couples our config schema to theirs. | Take `ConnectionFactory` (or factory delegate) as input. Let user configure it however they like. |

## RabbitMQ Adapter — Specific Features

These belong to `Oragon.AdaptivePool.RabbitMQ`, not Core.

| Feature | Layer | Notes |
|---|---|---|
| `IConnection` pool with `IsOpen` health check | Connection adapter | BeforeUse + Check both consult `IsOpen`. Release calls `CloseAsync()`. |
| `IChannel` pool layered on `IConnection` pool | Channel adapter | Factory acquires connection from inner pool, calls `connection.CreateChannelAsync()`. Release closes channel; underlying connection returns to inner pool. |
| Channel-per-publisher discipline guidance | Docs | Per RabbitMQ docs: do not share publishing channels across threads even though v7 client is "thread-safe to call." |
| Recommended channel-per-connection ceiling | Docs / sample | "single-digit channels per connection" per RabbitMQ official guidance — encode as default `MaxSize` hint in sample, not as hard limit. |
| Publisher-confirms compatibility (no interference with confirm tracking) | Implementation | AfterUse hook should NOT swallow exceptions that publisher-confirm logic raises. |
| `services.AddAdaptiveConnectionPool(...)` extension | DI | Per PROJECT.md. |
| `services.AddAdaptiveChannelPool(...)` extension | DI | Per PROJECT.md. Wires both layers. |
| Sample: bursty publisher (handful/hour → 100k/sec) | Sample project | Per PROJECT.md. The motivating scenario from the Context section. |

## Feature Dependencies

```
[Acquire/Release async]
    └──requires──> [Thread-safe internal queue/channel of items]
                       └──requires──> [Factory hook]

[MinSize/MaxSize bounds]
    └──requires──> [Counter of total items + counter of in-use items]

[Composite-signal elasticity (grow)]
    └──requires──> [Waiter queue length tracking]
    └──requires──> [Utilization sampling over window]
    └──requires──> [Acquire-wait timing instrumentation]
                       └──requires──> [Built-in metrics infrastructure]

[Idle eviction (shrink)]
    └──requires──> [Per-item LastReturnedAt timestamp]
    └──requires──> [Background sweeper task]
                       └──requires──> [MinSize floor enforcement]

[Background health sweep]
    └──requires──> [Background sweeper task]      (shareable with shrink)
    └──requires──> [Check hook]
    └──requires──> [Failure policy]

[On-borrow validation]
    └──requires──> [BeforeUse hook]
    └──requires──> [Failure policy]

[Layered channel pool]
    └──requires──> [Connection pool] (pool composing pool)
    └──requires──> [Failure policy that propagates to outer layer when underlying connection dies]

[Eager warm-up]
    └──requires──> [Factory hook]
    └──requires──> [Async pool startup hook in DI lifecycle]

[Graceful shutdown / drain]
    └──requires──> [Pending-acquire tracking]
    └──requires──> [State machine: Open → Draining → Closed]

[OpenTelemetry metrics]
    └──requires──> [Internal counters/gauges in hot path]   (must be allocation-free)

[Failure policy]
    └──requires──> [Discard path (Release hook + decrement counters)]
    └──enables──> [Quarantine variant, custom policies]

[Per-item max-lifetime]
    └──requires──> [Per-item CreatedAt timestamp]
    └──requires──> [Failure policy invocation on rotation]

[Builder fluent API]
    └──enhances──> [DI integration]
    └──enhances──> [All hook configuration]
```

### Dependency Notes

- **Composite-signal elasticity is the most complex feature** and pulls in metrics, waiter tracking, and a windowed utilization sampler. It's the marquee differentiator and deserves its own implementation phase.
- **Background sweeper** can be a single task that handles BOTH shrink and health-check passes — design it as one component.
- **Failure policy is the keystone** for many features (validation, sweep, max-lifetime). Define `IItemFailurePolicy<T>` early, even if only one implementation ships in the first phase.
- **Layered pools** (channel pool over connection pool) don't require new Core features but DO stress the failure-policy contract: if the inner connection dies while a channel built on it is checked out, the outer pool needs to know.
- **Graceful shutdown conflicts with eager warm-up** when the warm-up is in-flight at shutdown time — be explicit: cancel warm-up on dispose.

## MVP Definition

### Launch With (v1.0)

Minimum viable product — proves the three-pillar Core Value (elasticity + auto-cure + DX) on the motivating RabbitMQ scenario.

**Core:**
- [ ] `IAdaptivePool<T>` with `Acquire()` (no-wait fast path) and `AcquireAsync(CancellationToken)` (waiting/growing path)
- [ ] `IPoolItem<T>` disposable wrapper
- [ ] Builder fluent API with all five hooks (Factory, BeforeUse, Check, AfterUse, Release)
- [ ] `MinSize`, `MaxSize`, `InitialSize` bounds + eager warm-up
- [ ] Composite-signal grow (wait + utilization + waiter-queue)
- [ ] Idle shrink to `MinSize` (`IdleTimeout`)
- [ ] On-borrow validation (BeforeUse) + background health sweep (Check)
- [ ] `IItemFailurePolicy<T>` interface + default discard+replace policy
- [ ] Release hook for cleanup
- [ ] Built-in metrics (`Meter`), tracing (`ActivitySource`), logging (`ILogger<T>`)
- [ ] DI extension `AddAdaptivePool<T>(...)`
- [ ] CancellationToken plumbed end-to-end
- [ ] `IAsyncDisposable` on pool with drain semantics

**RabbitMQ adapter:**
- [ ] `AddAdaptiveConnectionPool` + `AddAdaptiveChannelPool` extensions
- [ ] Layered channel-over-connection composition
- [ ] Bursty-publisher sample

**OSS quality:**
- [ ] CI matrix `net8.0` / `net9.0` / `net10.0`
- [ ] Stress tests for the burst→idle→burst cycle
- [ ] README with quickstart + OTel example
- [ ] SemVer 2.0
- [ ] Symbol packages + SourceLink on NuGet

### Add After Validation (v1.x)

- [ ] Quarantine-with-backoff failure policy (built-in alternative to discard+replace) — trigger: user feedback that some workloads benefit from retry
- [ ] On-return validation (AfterUse hook) opt-in
- [ ] Per-item `MaxLifetime` / `MaxUses` rotation — trigger: reports of stale-connection bugs in long-running deployments
- [ ] Graceful drain API beyond plain dispose (`DrainAsync(TimeSpan)`) — trigger: k8s lifecycle hook integration requests
- [ ] Polly integration sample / glue package — trigger: user requests for retry+circuit-breaker around acquire

### Future Consideration (v2+)

- [ ] Additional adapters (HttpClient, Npgsql, gRPC channels) — only after RabbitMQ adapter validates the abstraction
- [ ] Keyed pool variant (analogous to commons-pool2 `GenericKeyedObjectPool`) — only with strong demand
- [ ] Abandoned-object detection (commons-pool2 `removeAbandoned`) — only after evidence developers leak `IPoolItem<T>` in real apps
- [ ] Priority queue / weighted fairness — only if real workloads demonstrate need

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---|---|---|---|
| Generic pool + Factory + Acquire/Release async | HIGH | LOW | P1 |
| Disposable `IPoolItem<T>` wrapper | HIGH | LOW | P1 |
| `MinSize`/`MaxSize`/`InitialSize` bounds | HIGH | LOW | P1 |
| Eager warm-up | HIGH | LOW | P1 |
| Composite-signal grow | HIGH | HIGH | P1 (it's the differentiator) |
| Idle shrink (`IdleTimeout`) | HIGH | MEDIUM | P1 |
| On-borrow validation (BeforeUse) | HIGH | LOW | P1 |
| Background health sweep (Check) | HIGH | MEDIUM | P1 |
| `IItemFailurePolicy<T>` + discard policy | HIGH | MEDIUM | P1 |
| Built-in `Meter` metrics | HIGH | MEDIUM | P1 |
| `ActivitySource` tracing | MEDIUM | LOW | P1 |
| `ILogger<T>` logging | MEDIUM | LOW | P1 |
| Builder fluent API | HIGH | MEDIUM | P1 |
| DI integration | HIGH | LOW | P1 |
| `IAsyncDisposable` + basic drain | HIGH | MEDIUM | P1 |
| RabbitMQ connection pool adapter | HIGH | MEDIUM | P1 |
| RabbitMQ channel pool adapter (layered) | HIGH | MEDIUM | P1 |
| Bursty publisher sample | HIGH | MEDIUM | P1 |
| Quarantine+backoff failure policy | MEDIUM | MEDIUM | P2 |
| On-return validation (AfterUse) | MEDIUM | LOW | P2 |
| Per-item `MaxLifetime` / `MaxUses` | MEDIUM | LOW | P2 |
| `DrainAsync(TimeSpan)` graceful shutdown | MEDIUM | MEDIUM | P2 |
| Polly integration glue/docs | MEDIUM | LOW | P2 |
| HttpClient/Npgsql adapters | LOW (already pooled elsewhere) | HIGH | P3 |
| Keyed sub-pools | LOW | HIGH | P3 |
| Abandoned-object detection | LOW | MEDIUM | P3 |

**Priority key:**
- P1: Must have for v1.0 launch (validates the three-pillar Core Value)
- P2: Should have, add when validated by usage
- P3: Nice to have, future consideration

## Competitor Feature Analysis

| Feature | Microsoft.Extensions.ObjectPool | HikariCP (JVM) | commons-pool2 (JVM) | node generic-pool | Polly v8 (.NET) | **Oragon.AdaptivePool (target)** |
|---|---|---|---|---|---|---|
| Min/Max bounds | Only "MaximumRetained" | Yes (min/max idle, max total) | Yes | Yes | N/A (not a pool) | Yes (Min/Max/Initial) |
| Elasticity (grow under pressure) | No | On-demand only | On-demand only | On-demand only | N/A | **Composite-signal (differentiator)** |
| Auto-shrink on idle | No | Yes (`idleTimeout`) | Yes (evictor thread) | Yes (`idleTimeoutMillis`) | N/A | Yes |
| On-borrow health check | No | Yes (`connectionTestQuery`/JDBC4 isValid) | Yes (`testOnBorrow`) | Yes (`testOnBorrow`) | N/A | Yes (BeforeUse hook) |
| Background health probe | No | Yes ("keepalive") | Yes (evictor calls validate) | Yes (`testWhileIdle`) | N/A | Yes (Check hook) |
| Pluggable failure policy | No | Discard only | Discard only | Discard only | (own policies, different scope) | **Pluggable (differentiator)** |
| Lifecycle hooks granularity | 2 (Create/Return) | Few (callbacks limited) | 5 (make/activate/validate/passivate/destroy) | 3 (create/destroy/validate) | N/A | **5 (Factory/BeforeUse/Check/AfterUse/Release)** |
| Async-first | Sync API | Sync (JDBC) | Sync | Promise-based | Async-first | **Async-first** |
| OpenTelemetry-native metrics | Limited | JMX / Micrometer adapter | JMX | None built-in | Has telemetry | **Yes (Meter + ActivitySource)** |
| DI integration | Yes | Spring Boot autoconfig | Manual | N/A | Yes | Yes |
| Graceful drain | No | `close()` waits briefly | `close()` | `drain()` | N/A | Yes (target) |
| Layered (pool of pool) | No | No | No | No | N/A | **Yes for IChannel/IConnection** |
| Eager warm-up | No | Yes (`initialPoolSize`) | Optional | Yes (min creates eagerly) | N/A | Yes (`InitialSize`) |
| Acquire timeout | No | `connectionTimeout` | `maxWaitMillis` | `acquireTimeoutMillis` | Timeout strategy | CancellationToken (idiomatic .NET) |
| Per-item max-lifetime | No | Yes (`maxLifetime`) | Yes (`minEvictableIdleTimeMillis`+) | No | N/A | v1.x target |

## Sources

- [HikariCP — GitHub](https://github.com/brettwooldridge/HikariCP)
- [Apache commons-pool2 GenericObjectPool API](https://commons.apache.org/proper/commons-pool/apidocs/org/apache/commons/pool2/impl/GenericObjectPool.html)
- [node-pool / generic-pool — GitHub](https://github.com/coopernurse/node-pool)
- [generic-pool — npm](https://www.npmjs.com/package/generic-pool)
- [Microsoft.Extensions.ObjectPool — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/performance/objectpool?view=aspnetcore-10.0)
- [DefaultObjectPool<T> — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.objectpool.defaultobjectpool-1?view=net-10.0-pp)
- [Polly v8 documentation](https://www.pollydocs.org/)
- [Polly resilience pipelines](https://www.pollydocs.org/pipelines/)
- [.NET resilience library overview — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/resilience/)
- [Reactor Pool GracefulShutdownInstrumentedPool](https://projectreactor.io/docs/pool/snapshot/api/reactor/pool/decorators/GracefulShutdownInstrumentedPool.html)
- [RabbitMQ .NET/C# Client API Guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide)
- [RabbitMQ Channels documentation](https://www.rabbitmq.com/docs/channels)
- [RabbitMQ Connections documentation](https://www.rabbitmq.com/docs/connections)
- [rabbitmq-dotnet-client v7 multi-threading discussion](https://github.com/rabbitmq/rabbitmq-dotnet-client/discussions/1663)
- [Bulkhead Pattern — Azure Architecture Center](https://learn.microsoft.com/en-us/azure/architecture/patterns/bulkhead)
- [SQLAlchemy connection pooling docs (max lifetime / pre-ping patterns)](https://docs.sqlalchemy.org/en/20/core/pooling.html)
- [Oracle UCP stale connection handling](https://docs.oracle.com/en/database/oracle/oracle-database/21/jjucp/stale-ucp-connections.html)

---
*Feature research for: .NET adaptive object/connection pool library*
*Researched: 2026-05-02*
