# Pitfalls Research

**Domain:** .NET adaptive object pool library + RabbitMQ adapter (Oragon.AdaptivePool)
**Researched:** 2026-05-02
**Confidence:** HIGH on RabbitMQ-specific claims (verified against rabbitmq.com docs and v7 migration guide May 2026); HIGH on .NET BCL claims (verified against learn.microsoft.com); MEDIUM on cross-ecosystem pool wisdom (synthesized from HikariCP/commons-pool2 community).

## Pitfall Taxonomy

This document organizes pitfalls into ten domains drawn directly from the milestone scope:

1. Concurrency bugs (deadlocks, races, ABA, lost wake-ups)
2. Resource leaks (forgotten Return, async-disposal, exception paths)
3. Health-check pitfalls (false positives, costly checks, sweep amplification)
4. Elastic algorithm pitfalls (oscillation, thrash, slow growth, overshoot)
5. Failure policy pitfalls (cascading failure, retry storm, sticky quarantine)
6. RabbitMQ-specific pitfalls (autorecovery, channel sharing, heartbeats, channel_max, confirms+concurrency)
7. Telemetry pitfalls (cardinality, Meter lifetime, ActivitySource cost, sampling)
8. Multi-targeting pitfalls (BCL diffs, ValueTask, IAsyncDisposable, `Lock`)
9. OSS pitfalls (semver, symbols, source-link, breaking pre-release changes)
10. Cancellation token gaps (factory, health check, waiter)

Each critical pitfall has a Phase mapping. The recommended phase numbering aligns with the natural milestone breakdown:

- **P1 — Core skeleton** (interfaces, builder, Factory/Release, basic Acquire/Release, no elasticity)
- **P2 — Elasticity & health** (composite-signal grow, idle shrink, BeforeUse/Check/AfterUse hooks, failure policy)
- **P3 — Telemetry & DI** (Meter, ActivitySource, ILogger, AddAdaptivePool extension)
- **P4 — RabbitMQ adapter** (IConnection pool, layered IChannel pool, samples)
- **P5 — Hardening** (stress/concurrency tests, chaos tests, OSS quality bar, NuGet pack)

---

## Critical Pitfalls

### Pitfall 1: Lost wake-up in the waiter queue (deadlock under burst-then-drain)

**What goes wrong:**
A pending `AcquireAsync` caller never wakes after an item is returned. The caller's `Task` stays incomplete forever, eventually cancelling on the consumer's outer timeout while the item sits idle in the pool. Under burst then drain, multiple waiters can be stranded simultaneously and the pool appears "stuck" with items available.

**Why it happens:**
Classic producer/consumer race: `Return()` checks "are there any waiters?" *before* the latest waiter has finished enqueuing itself. The waiter sees "pool empty, enqueue myself", but the returner already saw "no waiters, push to free-list" — both decisions stale. A handwritten `SemaphoreSlim` + `ConcurrentQueue` combination is especially prone because the two structures are not atomically consistent.

**How to avoid:**
- Use `System.Threading.Channels.Channel<T>` (bounded or unbounded) as the waiter queue. `Channel<T>` provides lock-free, async-aware producer/consumer with documented happens-before guarantees. `WriteAsync` + `WaitToReadAsync` close the gap atomically.
- Alternatively, use **a single lock that protects both the free-list and the waiter list together**. Never split the data structures across two independent synchronization primitives.
- For the fast path (item available, no waiter), use `Interlocked` with double-check inside the lock.
- Test pattern: a "ping-pong" stress test where N threads `Acquire/Release` in tight loops with pool `MaxSize=1`. Add a watchdog that fails the test if any `AcquireAsync` exceeds 5 s.

**Warning signs:**
- Tail latency of `AcquireAsync` shows multi-second outliers under sustained concurrency.
- Test runs flake intermittently under load with timeouts that pass on retry.
- Telemetry counters show `pool.in_use < pool.size` but `pool.waiting > 0` simultaneously for >1 s.

**Phase to address:** P1 (core skeleton) — the waiter-queue design is the foundational correctness call. Wrong choice here cascades into every other phase.

---

### Pitfall 2: Item leaked when consumer throws between Acquire and Dispose

**What goes wrong:**
Consumer code does `var item = await pool.AcquireAsync(); item.Object.DoWork(); item.Dispose();` and an exception in `DoWork()` skips the `Dispose()`. Item never returns to the pool. Pool eventually saturates, every subsequent `AcquireAsync` blocks forever (or until token cancels). The leak is invisible because the GC will *eventually* finalize the item — but only when memory pressure arises, which may be days.

**Why it happens:**
- Library consumers don't always remember `using`/`await using` for pool items, especially when refactoring or in error paths.
- The pattern "manual Dispose call" is fragile compared to `using`/`await using`.
- Library APIs that return a raw resource (not a wrapper) have no place to hook return-on-dispose.

**How to avoid:**
- **Make `IPoolItem<T>` the only Acquire return type.** It implements `IAsyncDisposable` (and `IDisposable` for sync paths). `Dispose` returns to pool.
- README and XML docs MUST start every example with `await using var item = await pool.AcquireAsync(ct);` — never show manual `Dispose`.
- Implement a finalizer on the wrapper that logs a warning ("pool item leaked — was not disposed") and returns the item to the pool defensively. Do this carefully (finalizers run on a separate thread; cannot do real async work). Strategy: in finalizer, call a synchronous `ReturnFromFinalizer()` on the pool that adds back to the free-list under lock and skips async health checks.
- Consider `[MustDisposeResource]` JetBrains attribute or `[MustUseReturnValue]` for IDE warnings.
- Stress test: "leak test" that explicitly throws after Acquire and verifies pool recovers (counter `pool.leaked` increments, then GC.Collect+WaitForPendingFinalizers brings the item back).

**Warning signs:**
- `pool.in_use` counter monotonically rising and never returning to 0 even when traffic is idle.
- `pool.leaked` counter > 0 in production logs.
- Apps under memory pressure suddenly recover throughput because GC ran finalizers.

**Phase to address:** P1 (core skeleton) — wrapper design and finalizer must ship in v0.1.

---

### Pitfall 3: Exception in factory leaves counters inconsistent

**What goes wrong:**
Pool grows from 4 to 5 items: it increments `_totalSize` to 5 *optimistically*, then calls `await Factory()`, which throws. `_totalSize` stays at 5 even though only 4 real items exist. After several such failures, the pool reports "full" (`_totalSize == MaxSize`) but actually holds nothing — every Acquire times out.

**Why it happens:**
Optimistic counter increment is a common pattern for capacity reservation. If the factory call (which is async, slow, and can throw network errors) is not wrapped in try/finally that rolls back on failure, counters drift.

**How to avoid:**
- Reserve capacity (`Interlocked.Increment(_totalSize)`) BEFORE calling factory. Wrap factory call in try/catch that decrements on failure. Rethrow.
- Same pattern for `BeforeUse` failures: if validation fails on borrow, the failure-policy decision must atomically discard+decrement+grow, not leave a "ghost" reservation.
- Property-based test: invoke pool with a factory that throws on every Nth call (random N) under concurrent acquire load. Invariant: `_totalSize` always equals the count of objects the test can observe via instrumentation; at idle, `_totalSize >= MinSize`.

**Warning signs:**
- `pool.size` gauge plateaus at MaxSize while `pool.in_use` is much lower.
- Restart "fixes" mysterious pool-saturation incidents.
- Logs show many factory exceptions clustered around MaxSize boundary.

**Phase to address:** P1 (core skeleton) for the basic factory rollback; P2 (elasticity) for grow-path rollback; P5 (hardening) for fault-injection tests.

---

### Pitfall 4: ABA in lock-free free-list (item appears returned twice)

**What goes wrong:**
A naive lock-free implementation using `Interlocked.CompareExchange` on a stack-of-items can suffer ABA: thread T1 reads top=A, gets preempted; thread T2 pops A (use), T2 returns A, T2 pops A again, T2 returns B which becomes the new top, T2 returns A (now A is on top again with .next=C, where C is a now-discarded item). T1 wakes, CAS succeeds (top still equals A reference), but T1 sets top to A.next=C — a freed item.

**Why it happens:**
Developers reach for `Interlocked` to avoid lock contention without realizing CLR managed references CAN have ABA when the same object is recycled into the same slot. .NET's GC reduces but does not eliminate the risk.

**How to avoid:**
- **Don't roll your own lock-free free-list.** Use `ConcurrentBag<T>`, `ConcurrentQueue<T>`, or `Channel<T>` — all BCL-tested at scale and immune to ABA in their public contract.
- If profiling shows lock contention is real (it usually isn't), use `Channel<T>` with `SingleReader=false` — built for this exact use case.
- Stress test: 1M iterations of concurrent Acquire/Release with `MaxSize=2` and 32 threads; assert no item is observed by two threads at the same time (each item carries an Interlocked-incremented "in-use generation" counter).

**Warning signs:**
- Test failures where two threads claim the same `IPoolItem<T>.Object` instance.
- Sporadic NREs in consumer code on `item.Object.X` after pool returns the same object twice.
- Channel publish errors clustered around pool grow/shrink events.

**Phase to address:** P1 (core skeleton) — pick the right BCL primitive from day one, before any "optimization" tempts a custom CAS loop.

---

### Pitfall 5: Health-check sweep amplifies load when downstream is unhealthy

**What goes wrong:**
The Check hook calls `IsOpen` (cheap) but in some adapters might do a real probe (e.g., a `noop` AMQP frame, an HTTP GET to a `/health` endpoint). When RabbitMQ is briefly unhealthy, every sweep pass rediscovers every item is broken; failure policy discards and immediately re-creates. The new connections all fail. Sweep runs again 30 s later and the pattern repeats — but now with multiplied retry traffic from the consuming app on top. The pool becomes a *load amplifier* during outages.

**Why it happens:**
- Health checks are written assuming the dependency is mostly healthy.
- Sweep cadence + factory retry + consumer retry compose multiplicatively.
- "Heal aggressively" feels right in isolation; in a partial-outage scenario it's the wrong default.

**How to avoid:**
- **Sweep must back off when failure rate is high.** Track rolling failure rate; when above threshold (e.g., 50% of last 10 sweeps failed), dial back sweep frequency exponentially (30 s → 60 s → 120 s, capped at 5 min).
- **Cap concurrent factory creation.** A `SemaphoreSlim(maxConcurrentCreations)` around `Factory()` prevents a sweep from spawning 100 simultaneous TCP-connect attempts.
- **Distinguish "broken item" from "downstream unavailable".** When factory fails (not just health-check), backoff the entire pool's grow attempts, not just sweep.
- Document the recipe: pair Adaptive Pool with a Polly circuit breaker around the consumer's `AcquireAsync` call so the application itself stops asking when downstream is dead.

**Warning signs:**
- During a RabbitMQ outage, the application's outbound connection rate to RabbitMQ goes UP rather than down.
- `pool.failures` counter shows synchronized spikes at sweep-interval cadence.
- Recovery from outage is slower than expected — the pool is fighting itself.

**Phase to address:** P2 (elasticity & health) — sweep design must include backoff from day one; P5 (hardening) for chaos tests.

Source: This is a real production pattern documented in HikariCP issue tracker ("connection storm during DB outage") and in [Apache commons-pool2 docs](https://commons.apache.org/proper/commons-pool/) on `testWhileIdle` cadence.

---

### Pitfall 6: Health check inside Acquire blocks the hot path

**What goes wrong:**
Putting a synchronous or even brief async health-check (e.g., a 50 ms server round-trip) on the BeforeUse hook adds 50 ms to every single Acquire. At 1000 acq/s, that's 50 thread-seconds of blocking per second — pool throughput collapses, app threads pile up.

**Why it happens:**
"Of course we should validate before handing out an item — what if it's broken?" Developers reach for the strictest check by default. They don't measure cost vs. failure rate.

**How to avoid:**
- **Default BeforeUse to cheap, in-process checks only.** For RabbitMQ: read `IsOpen` boolean (zero-cost). NEVER do a server round-trip on the hot path.
- **Real round-trip checks belong in background sweep**, where latency doesn't matter.
- **Document the cost contract**: BeforeUse must complete in < 1 ms p99 or you're using it wrong. Add an XML doc on the `BeforeUse` hook with this requirement explicit.
- **Measure**: ship a benchmark that shows no-op pool acquire latency vs. with-BeforeUse-IsOpen acquire latency. Should be near-equal.

**Warning signs:**
- Acquire p99 latency rises sharply when BeforeUse is enabled.
- CPU usage doesn't rise in proportion — threads are blocked on I/O for the health check.
- Application throughput drops when "we added more health checking".

**Phase to address:** P2 (elasticity & health) — design the hook contract with this constraint baked into XML docs and the sample.

---

### Pitfall 7: Elastic algorithm oscillation (grow/shrink thrash)

**What goes wrong:**
Pool grows to 100 under burst; burst ends; idle timeout is 30 s; pool shrinks aggressively to MinSize=10 in 60 s. Next minute, another burst arrives — pool must grow again from 10 to 100. The grow/shrink cycle repeats every 60 s, exactly at the period of the workload's "slow" rhythm. Each grow costs N TCP handshakes; each shrink wastes ready connections. CPU and broker load thrash in resonance.

**Why it happens:**
- Single-axis decisions (only "idle for X" → shrink) miss the broader pattern (workload is bursty, not transitioning to permanent idle).
- No hysteresis: grow threshold and shrink threshold are not separated, so the system oscillates around a single trip point.
- IdleTimeout is set too aggressively because "we want to save resources" — but the resources we're "saving" cost more to recreate than to retain.

**How to avoid:**
- **Hysteresis in shrink:** require N consecutive sweep windows of low utilization before shrinking (e.g., 5 windows × 30 s = 2.5 minute calm before shrink begins).
- **Shrink in increments, not jumps:** retire one idle item per sweep tick, not all at once.
- **Defer shrink below "high water mark" (HWM):** track p95 of `pool.in_use` over a 10-minute window. Don't shrink below max(MinSize, p95-HWM × 0.7).
- **Expose tuning knobs**: `IdleTimeout`, `ShrinkBatchSize`, `ShrinkBackoffWindows`, `HighWaterMarkWindow`. Default sensibly but allow override.
- Test: simulate a sinusoidal load (period = 60 s, amplitude = 5x base). Assert pool size stays within [base, peak] and does NOT oscillate by more than 20% of peak between bursts.

**Warning signs:**
- `pool.size` gauge shows sawtooth pattern in production dashboards.
- Broker dashboards show repeated connection-open/close cycles at regular intervals.
- App p99 latency has periodic spikes correlating with grow events.

**Phase to address:** P2 (elasticity & health) — composite-signal grow + hysteretic shrink is the headline differentiator; specify and test thoroughly.

---

### Pitfall 8: Slow growth fails the burst (10x burst, pool grows by 1 per second)

**What goes wrong:**
Pool starts at 10, sees burst to 200 concurrent acquires. Grow logic says "wait > 100 ms → grow by 1". 190 acquires pile up in the waiter queue, getting freed at 1/sec. Burst lasts 10 s, the burst ends before the pool ever caught up. From the consumer's perspective, the pool was useless during the burst.

**Why it happens:**
- Conservative grow-by-one prevents overshoot but cripples burst response.
- Single-signal trigger ("anyone waiting?") fires too late when the queue depth signal would have allowed earlier action.
- No "burst mode" — pool treats every wait the same regardless of queue depth.

**How to avoid:**
- **Composite-signal grow** (this is exactly the differentiator from PROJECT.md): combine waiter-queue length, sustained utilization %, and individual wait time. Examples:
  - `waiters_in_queue >= 5` → grow by `min(waiters_in_queue, MaxSize - currentSize, MaxBurstGrow)`
  - `utilization > 80% for last 10 s` → grow by ceiling(0.2 × currentSize)
  - `acquire_wait > 200 ms` → grow by 1
- **Cap concurrent factory invocations** (Pitfall 5) — don't grow by 100 in one tick or you DDoS the broker.
- **Pre-emptive grow on first wait**: if any wait happens AND we're below MaxSize, start growing immediately, don't wait for the threshold to hit.
- Test: "spike test" — pool starts at 10, suddenly receives 200 acquire requests. Assert p99 acquire latency under 500 ms (or whatever target) and pool reaches steady state within 1 s.

**Warning signs:**
- App-side timeouts spike during traffic bursts.
- `pool.acquire.duration` histogram shows long tail during bursts.
- `pool.size` rises slowly compared to `pool.waiting` during burst onset.

**Phase to address:** P2 (elasticity & health) — design compositely from the start.

---

### Pitfall 9: RabbitMQ automatic recovery vs pool's "discard broken item" — double-management

**What goes wrong:**
RabbitMQ.Client v7 has `AutomaticRecoveryEnabled = true` by default. The library detects a closed connection (heartbeat missed, TCP reset) and *transparently reopens* the connection in-place — same `IConnection` reference, but with a fresh socket underneath. Meanwhile, our pool's health check sees `IsOpen == false` for a brief window during recovery and discards the connection. The library's recovery completes seconds later on a connection nobody is holding — wasted work AND we just rebuilt the pool unnecessarily. Worse: if our `Release` hook calls `CloseAsync` on a connection currently being recovered, behavior is undefined.

**Why it happens:**
Two layers of recovery (RabbitMQ.Client's automatic recovery + our pool's failure-policy) both trying to "fix" the same connection without coordination.

**How to avoid:**
- **Decision: pick one layer.** For pool-managed connections, **disable RabbitMQ.Client automatic recovery** (`AutomaticRecoveryEnabled = false`) and let the pool's failure policy handle replacement. Rationale: pool already does discard+replace, and pool grows/shrinks based on real demand — letting the client also try to recover creates unpredictable lifecycle.
- **Document this clearly**: the adapter's `AddAdaptiveConnectionPool` MUST configure `ConnectionFactory.AutomaticRecoveryEnabled = false` by default, with an XML doc explaining why.
- If a user really wants automatic recovery, document the contract: BeforeUse hook should treat the brief `IsOpen == false` window as transient (e.g., wait up to 100 ms before declaring broken). But this is an advanced opt-in.
- For `IChannel`: channels are NEVER auto-recovered separately from connections; if the underlying connection is recovered, channels on it must be re-created. Pool's layered design handles this naturally: channel pool acquires fresh channels from connection pool.

**Warning signs:**
- RabbitMQ broker logs show paired close/open events from the same client at exactly the heartbeat interval.
- `IConnection` references in app memory have stale endpoint info after a network blip.
- Channel publishes throw `AlreadyClosedException` even though the pool just handed the channel out as healthy.

**Phase to address:** P4 (RabbitMQ adapter) — encode the configuration default in the `AddAdaptiveConnectionPool` extension; explain in XML doc and README.

Source: [RabbitMQ .NET Client API Guide — Recovery section](https://www.rabbitmq.com/client-libraries/dotnet-api-guide#recovery) (HIGH confidence, May 2026).

---

### Pitfall 10: Sharing a pooled IChannel for concurrent publishing

**What goes wrong:**
Consumer code acquires an `IChannel` from the pool, then uses it from multiple threads to publish concurrently — perhaps via a fan-out pattern. Frames from the parallel publishes interleave at the AMQP protocol level, the broker rejects the malformed frame stream, the connection drops with a hard protocol error. Worse: pool returns the channel as "healthy" right before the connection drops, so multiple consumers experience cascading failures simultaneously.

**Why it happens:**
- The RabbitMQ.Client v7 docs describe IChannel as "thread-safe to call" in some places, leading developers to think shared concurrent publishing is fine. The official guide explicitly contradicts this for publishers: it's a "hard requirement" not to share channels for concurrent publishing.
- Pool semantics make sharing easy: "I have a pooled channel, I'll inject it into 5 services and we'll all publish."
- Sample code from older blog posts shows shared `IModel` (pre-v7) with no warning.

**How to avoid:**
- **Pool channels per logical publish operation**, not per service or per app. Each publish call: `await using var ch = await channelPool.AcquireAsync(); await ch.Object.BasicPublishAsync(...);`
- **Pool size scales with publish concurrency** automatically — that's the whole point of the adaptive design.
- **Sample MUST demonstrate the correct pattern.** Show 100 parallel publishes acquiring 100 channels (the pool grows to ~100), not one channel shared across 100 publishes.
- **README anti-pattern section** warning explicitly against shared-channel publishing.
- **Bonus safeguard**: optionally, the channel adapter can wrap `IChannel` in a thin proxy that throws on second concurrent call from different thread (debug builds only — opt-in).

**Warning signs:**
- Connection drops with "unexpected frame" or "FRAMING_ERROR" in broker logs.
- Publish exceptions mention "channel was closed" right after a successful publish on the same channel.
- The pool size stays at 1 even under heavy publish load (the user thinks "one channel is enough").

**Phase to address:** P4 (RabbitMQ adapter) — pattern enforcement via documentation, sample, and README. P5 (hardening) for adding the optional thread-confined proxy.

Source: [RabbitMQ .NET Client API Guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) — quote: *"sharing a channel (an IChannel instance) for concurrent publishing will lead to incorrect frame interleaving at the protocol level. Channel instances must not be shared by threads that publish on them."* (HIGH confidence, May 2026).

---

### Pitfall 11: Channel-per-connection ceiling exceeded (channel_max=2047 default)

**What goes wrong:**
At extreme burst, the pool grows the channel pool to e.g. 3000 channels, all backed by 1 connection from the connection pool. Channel #2048 fails with `NOT_ALLOWED - number of channels opened (2047) has reached the negotiated channel_max (2047)`. Failure policy discards channel; tries again on the same connection → fails again → discard storm.

**Why it happens:**
- Default `channel_max` per RabbitMQ documentation is **2047** — verified from [rabbitmq.com/docs/configure](https://www.rabbitmq.com/docs/configure).
- Layered pool design: channel pool grows by acquiring connections from inner connection pool. If inner pool gives back the SAME connection on every acquire (because there's only 1 connection in the inner pool), all channels concentrate on it.
- Developer expects "the pool grows infinitely" — but is unaware of the per-connection limit.

**How to avoid:**
- **Adapter should expose a "channels per connection" target**, e.g., 100 (well under 2047 to leave headroom). Channel pool's factory hook tries to spread channels across multiple inner-pool connections.
- **Channel pool's grow algorithm should also grow the inner connection pool** when the average channels-per-connection exceeds the target.
- **Document the math in README**: "If you expect P concurrent publishes, plan for P channels = ceil(P / target_channels_per_connection) connections."
- **Surface the broker's negotiated channel_max value via metrics** (`pool.rabbitmq.channel_max` gauge) so users know their actual limit.
- **Test**: integration test using Testcontainers RabbitMQ with `channel_max=10` (forced low). Assert the pool spreads channels across multiple connections, not all on one.

**Warning signs:**
- Broker logs show `NOT_ALLOWED - number of channels opened ... has reached the negotiated channel_max`.
- `pool.failures` counter spikes for the channel pool but not the connection pool.
- All failures cluster on a single connection.

**Phase to address:** P4 (RabbitMQ adapter) — channel-per-connection spreading is part of the layered-pool design.

Sources: [RabbitMQ docs configure](https://www.rabbitmq.com/docs/configure) (default 2047, HIGH); [RabbitMQ Channels docs](https://www.rabbitmq.com/docs/channels) (recommend single-digit channels per connection for typical apps; pool when concurrency demands more, HIGH).

---

### Pitfall 12: Heartbeat misconfiguration — connection killed during long publish wait

**What goes wrong:**
Default heartbeat in RabbitMQ.Client is 60 seconds. App configures `RequestedHeartbeat = TimeSpan.FromMinutes(10)` thinking "less network chatter = better". Network is silent for 10 minutes (low traffic). Network firewall (which has a 5-minute idle timeout) silently drops the TCP connection. RabbitMQ.Client doesn't notice (heartbeats happen at 10-min cadence), still says `IsOpen == true`. Pool hands out the dead connection. Next publish times out at the application level after 30 seconds — at which point the pool may discard the channel but the underlying TCP/AMQP problem is masked.

**Why it happens:**
- Heartbeat tradeoff: shorter = more network chatter but faster dead-peer detection; longer = quieter but blind to silent disconnects.
- Cloud/k8s/firewall environments commonly have idle TCP timeouts (5–15 min) — heartbeat MUST be shorter than the smallest such timeout.
- Defaults are application-friendly but users often "tune" them in the wrong direction.

**How to avoid:**
- **Adapter default: keep `RequestedHeartbeat = TimeSpan.FromSeconds(60)`** (RabbitMQ.Client default) and **document why shortening or lengthening is risky**.
- **Validation in `AddAdaptiveConnectionPool`**: if the user sets `RequestedHeartbeat > TimeSpan.FromMinutes(2)`, log a WARNING ("heartbeat exceeds typical NAT timeout — silent connection drops likely").
- **Sample includes heartbeat tuning section** with cloud-platform-specific recommendations (AWS NLB ~350 s, Azure Load Balancer 4 min default, etc.).
- **Health sweep can compensate partially**: if BeforeUse calls `IsOpen` and trusts it, the sweep should ALSO try a no-op AMQP call periodically (every few minutes) to flush silent deaths — but NOT on the hot Acquire path (Pitfall 6).

**Warning signs:**
- Sporadic "connection forcibly closed by remote host" exceptions clustering at multiples of the heartbeat or NAT timeout.
- App publishes work fine after a "warm-up" then start failing after long idle periods.
- `IsOpen == true` returns even after the connection is provably dead.

**Phase to address:** P4 (RabbitMQ adapter) — config defaults + validation; P5 (hardening) for documentation.

---

### Pitfall 13: Publisher confirms with concurrent publishes on the same channel

**What goes wrong:**
Channel created with `CreateChannelOptions { PublisherConfirmationsEnabled = true }`. Two threads call `BasicPublishAsync` simultaneously on the same channel. Confirms come back from the broker with sequence numbers 1, 2, 3, ... — but the client library's per-channel correlation maps those to the *originating publish task* using sequence numbers assigned at publish time. Concurrent publishes can race on sequence-number assignment (depends on internal locking in the v7 client). Worst case: confirm for publish A is delivered to publish B's task; A appears unconfirmed and B appears confirmed though the broker actually accepted both/neither.

**Why it happens:**
- Confirms model assumes ordered, single-threaded publisher per channel.
- Concurrent publishing on a single channel is already forbidden (Pitfall 10), but confirms add ANOTHER reason it's wrong even if frame interleaving were magically OK.

**How to avoid:**
- **Same fix as Pitfall 10**: pool channels per publish operation. Confirms work correctly because each channel has exactly one in-flight publisher.
- **Document the publisher-confirms requirement in adapter README**: "If publisher confirms are enabled, treat every channel as exclusive to one publish op at a time. Pool size must equal target concurrency."
- **Default `CreateChannelOptions` for the channel pool**: `PublisherConfirmationsEnabled = true, PublisherConfirmationTrackingEnabled = true` — opinionated default since durability is usually wanted, but make it overridable.
- **Sample explicitly demonstrates publish-with-confirm** awaiting the publish task, then disposing the channel back to the pool.

**Warning signs:**
- Confirms latency spikes correlate with concurrency spikes.
- Some publishes "appear to succeed but the message isn't on the queue" (the smoking gun of confirm misalignment).
- Channel confirms timeouts under load that don't appear under low concurrency.

**Phase to address:** P4 (RabbitMQ adapter) — defaults + sample + README.

Source: [RabbitMQ .NET Client API Guide — Recovery section](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) — *"When a connection is in the recovering state, any publishes attempted on its channels will be rejected with an exception. The client currently does not perform any internal buffering of such outgoing messages."* (HIGH confidence May 2026).

---

### Pitfall 14: Cancellation token forgotten in factory or health check

**What goes wrong:**
User calls `await pool.AcquireAsync(ct)` with a 5-second cancellation token. Pool needs to grow → calls `await Factory()` (no ct propagation) → factory does TCP-connect with no cancellation, takes 30 seconds. User's `ct` is canceled at 5 s, but the pool still holds the slot reservation and the user's task eventually throws... at the 5 s mark from the user's perspective, but the factory keeps running for another 25 s, creating an orphan connection that's never used.

**Why it happens:**
- Hook contracts that don't include `CancellationToken` parameter let users write factories that forget it.
- "Trust the user to plumb ct" fails — they will forget.
- Even when ct is plumbed, .NET I/O APIs that ignore ct exist (legacy `Socket.Connect` etc.).

**How to avoid:**
- **All hook signatures MUST take `CancellationToken`**: `Factory(IServiceProvider sp, CancellationToken ct)`, `BeforeUse(T item, CancellationToken ct)`, `Check(T item, CancellationToken ct)`, etc. Optional cancellation is not optional.
- **Pool internally creates a linked CTS** combining the user's `AcquireAsync` ct + pool-shutdown ct + a per-factory timeout (default 30 s, configurable). Pass the linked token to the factory.
- **For health check during sweep**: use the pool's shutdown ct + a health-check timeout (e.g., 5 s). Don't run unbounded health probes.
- **Test**: factory that ignores ct intentionally; verify pool's linked timeout still fires and the orphaned factory result is disposed if it eventually completes.

**Warning signs:**
- "Mysterious" connections in broker logs that nothing is using — they're orphan factories from canceled grows.
- App-side cancellation timeouts pass but server load shows lingering work.
- `pool.acquire.duration` histogram shows tail well beyond the cancellation timeout.

**Phase to address:** P1 (core skeleton) — hook signatures are foundational; can't add ct later without breaking change.

---

### Pitfall 15: High-cardinality metric tags blow up memory

**What goes wrong:**
Pool metrics include a tag `pool.name` (good — distinguishes multiple pools) and a tag `item.id` (terrible — every borrowed item creates a new time-series). After a day of running with 10k items churned through, the OTel collector has 10k unique series for `pool.acquire.duration`. Memory blows up; Prometheus scrape times explode; bills increase.

**Why it happens:**
- "More tags = more diagnostics" intuition.
- Adding tags is cheap at write-side; cost is borne by collector/storage.
- `Activity.SetTag` and `Counter.Add` accept arbitrary tag dictionaries — no compile-time guard against high cardinality.

**How to avoid:**
- **Tag whitelist**: pool metrics MUST tag only on `pool.name` (configurable string identifier). NEVER tag on item identity, request id, user id, etc.
- **For traces (ActivitySource)**: per-acquire tags are fine because each Activity is independent — but expensive tags (e.g., serialized objects) are wasteful.
- **Document the metric vocabulary in README**: list every metric, every tag, allowed values. This is part of the library's public contract.
- **`PublicAPI.Shipped.txt`**: include metric names + tag names as part of the tracked public surface.

**Warning signs:**
- Prometheus / Application Insights / Aspire dashboard shows millions of unique series for one metric.
- OTel collector memory footprint correlates with pool throughput.
- Metric scrape times increase over uptime.

**Phase to address:** P3 (telemetry & DI) — design tag schema upfront and document it.

---

### Pitfall 16: Meter and ActivitySource lifetime mismatch with pool lifetime

**What goes wrong:**
Pool is created with `new Meter("Oragon.AdaptivePool")` directly. Multiple `IAdaptivePool<T>` instances in the same app each create their own Meter with the same name. OTel registers them all, last-writer-wins for instruments — counters from one pool get overwritten by the other. Or: pool is disposed but the Meter/ActivitySource isn't, leaking measurement registrations.

**Why it happens:**
- Direct `new Meter(...)` is the obvious API but isn't the recommended one in .NET 8+.
- `IMeterFactory` exists specifically to manage Meter lifetime tied to DI scope — but it's easy to miss.

**How to avoid:**
- **Use `IMeterFactory` from DI** (`services.AddMetrics()` registers it; both .NET 8/9/10 have it in-box). The pool's constructor takes `IMeterFactory` and calls `factory.Create("Oragon.AdaptivePool")` — factory handles lifetime + dispose.
- **Single ActivitySource per assembly**: declare `internal static readonly ActivitySource Source = new("Oragon.AdaptivePool");` once at the assembly level. Don't dispose it (ActivitySource lifetime = process lifetime is fine).
- **For Meter, pool dispose disposes the meter** if the pool created it.
- **Test**: create two pools of same type, verify metrics are tagged with `pool.name` and counts don't collide.

**Warning signs:**
- "Duplicate meter" warnings in OTel exporter logs.
- Metric counts that are off by a factor of (number of pools).
- `IDisposable` not implemented on Meter chain.

**Phase to address:** P3 (telemetry & DI) — get DI integration right from start.

Source: [.NET Observability with OpenTelemetry — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel) (HIGH).

---

### Pitfall 17: ActivitySource always-on adds latency to hot path

**What goes wrong:**
Pool creates an `Activity` for every Acquire/Release call. Even when no listener is attached, `ActivitySource.StartActivity` does some work (tag dictionary allocation, name string interning). At 100k acq/s, this is measurable.

**Why it happens:**
- "Trace everything" instinct; assumption that ActivitySource is free when no listener.
- Reality: `StartActivity` returns null when no listener, BUT — only after invoking the sampler, and tag allocation depends on how it's structured.

**How to avoid:**
- **Check `ActivitySource.HasListeners()` before constructing tag dictionaries.** Pattern:
  ```csharp
  if (Source.HasListeners()) {
      using var act = Source.StartActivity("Acquire");
      act?.SetTag("pool.name", _name);
  }
  ```
- For very hot paths (Acquire/Release), consider sampling: only start activities for slow acquires (e.g., wait > 10 ms) or every Nth acquire. Use a ratio sampler.
- **Benchmark with and without listener**: include `BenchmarkDotNet` benchmarks that measure both states; should be near-zero overhead with no listener.

**Warning signs:**
- BenchmarkDotNet shows >10ns overhead on Acquire even when no OTel pipeline is configured.
- Profiler shows time in `ActivitySource.StartActivity`.
- "Why did adding observability slow us down 5%" tickets.

**Phase to address:** P3 (telemetry & DI) — instrument carefully; benchmark.

---

### Pitfall 18: Multi-targeting trap — `System.Threading.Lock` on net9+ but not net8

**What goes wrong:**
Developer reads about the new `System.Threading.Lock` in .NET 9 (a more efficient monitor wrapper) and uses it for the pool's internal lock. Build fails on net8 target. Or: developer uses `#if NET9_0_OR_GREATER` to switch between `System.Threading.Lock` (net9+) and `object` (net8), but the lock semantics differ subtly (e.g., `Lock.EnterScope()` returns a different disposable type).

**Why it happens:**
- Stack research notes `System.Threading.Lock` as the only multi-target API requiring `#if`.
- Mismatched lock objects cause inconsistent compile errors, lots of `#if` clutter, or subtle behavior differences.

**How to avoid:**
- **Pick one path: stick with `object` lock targets across all TFMs.** Negligible perf delta; massive simplification. Document this as an explicit Stack decision.
- If you really want `System.Threading.Lock` on net9+, encapsulate behind an internal `PoolLock` abstraction with two implementations selected by `#if`. Don't sprinkle `#if` throughout the codebase.
- **Use `System.Threading.Channels.Channel<T>`** for waiter coordination instead of raw locks (it's lock-free internally and uniform across TFMs).

**Warning signs:**
- `#if NET9_0_OR_GREATER` blocks proliferating throughout codebase.
- CI failing on net8 only (or net10 only) for compile errors.
- Different lock reentry behavior between TFMs in tests.

**Phase to address:** P1 (core skeleton) — locking strategy is a one-time decision.

---

### Pitfall 19: ValueTask vs Task ambiguity in public API

**What goes wrong:**
`AcquireAsync` returns `ValueTask<IPoolItem<T>>`. User awaits it twice (`var task = pool.AcquireAsync(); var a = await task; var b = await task;`) — `ValueTask` does not support multiple awaits. Or: user does `Task.WhenAll(pool.AcquireAsync(), pool.AcquireAsync())` — `WhenAll` calls `.AsTask()` internally, which boxes ValueTask, defeating the perf benefit, but it works. Worse: user calls `.AsTask()` then awaits the original ValueTask — undefined behavior.

**Why it happens:**
- ValueTask is a perf optimization; consumers don't always know the rules.
- Sample code that mixes ValueTask and Task encourages misuse.

**How to avoid:**
- **`AcquireAsync` returns `ValueTask<IPoolItem<T>>`** (perf win for the common case where item is immediately available — completes synchronously).
- **XML docs explicitly state**: "Await the returned ValueTask exactly once. To use with `Task.WhenAll`, call `.AsTask()` first."
- **Disposal returns `ValueTask`** for the same reason.
- **Tests**: use `ValueTaskExtensions` from BCL where applicable; avoid showing `Task.WhenAll(pool.AcquireAsync(), ...)` in samples.
- **Avoid `ConfigureAwait(false)` in samples** unless explaining why; library code itself uses `ConfigureAwait(ConfigureAwaitOptions.None)` internally.

**Warning signs:**
- `InvalidOperationException: ValueTask was awaited multiple times`.
- Mysterious test flakes when sample code uses `WhenAll` with ValueTask.

**Phase to address:** P1 (core skeleton) for API decision; P3 for sample quality.

---

### Pitfall 20: Pool's IAsyncDisposable not actually called by DI

**What goes wrong:**
Pool implements `IAsyncDisposable.DisposeAsync` to gracefully drain. User registers via `services.AddSingleton<IAdaptivePool<T>>(...)`. App stops; `ServiceProvider.Dispose()` is called (sync). Microsoft.Extensions.DI's container DOES call `DisposeAsync` for singletons IF you call `await serviceProvider.DisposeAsync()` instead of `Dispose()` — but many app patterns (especially older or hand-rolled hosts) only call `Dispose()`. Result: `DisposeAsync` is never invoked, drain never runs, in-flight publishes are silently dropped.

**Why it happens:**
- Sync vs async dispose disparity in DI.
- Generic Host (`Microsoft.Extensions.Hosting`) DOES call `DisposeAsync`; bare `ServiceCollection` may not.

**How to avoid:**
- **Implement BOTH `IDisposable` AND `IAsyncDisposable`.** The sync `Dispose` should do "best-effort drain with hard timeout" (e.g., wait 1 s for in-flight, then force-dispose). The async `DisposeAsync` does graceful drain.
- **Document the deployment requirement**: "For graceful shutdown, ensure your host calls `ServiceProvider.DisposeAsync()`. The standard .NET Generic Host does this automatically."
- **Provide an explicit `DrainAsync(TimeSpan timeout)` API** that consumers can invoke from their own shutdown hook (k8s SIGTERM handler, etc.) without relying on dispose timing.
- **Test**: mock a host that only calls `Dispose()` and verify the pool still cleans up (no orphan connections).

**Warning signs:**
- After app shutdown, broker logs show abruptly dropped connections rather than clean closes.
- Last few publishes during shutdown silently disappear.
- Resource leaks in test runs that compose-and-dispose service providers repeatedly.

**Phase to address:** P1 (core) for dual implementation; P4 (adapter) for clean RabbitMQ close.

---

### Pitfall 21: Quarantine policy that never recovers

**What goes wrong:**
Quarantine failure policy puts a broken item aside with exponential backoff for retry. Backoff keeps doubling: 1 s, 2 s, 4 s, ..., 1024 s, 2048 s. After 17 minutes of failures, the item won't be retried for 34 minutes. If something has fundamentally changed (e.g., wrong credentials replaced by correct ones), the quarantined item never returns to service. Eventually the pool is mostly quarantined and effectively at MinSize=0.

**Why it happens:**
- Pure exponential backoff has no ceiling.
- Quarantine state has no "external trigger to retry" mechanism.
- Confusion between "this individual item is bad" and "this pool's dependency is bad".

**How to avoid:**
- **Cap exponential backoff at a sensible ceiling** (e.g., 60 s). Allow override.
- **Quarantine is for the ITEM, not the POOL.** A quarantined item's slot should immediately count as "needs replacement" — the pool's grow logic should create a fresh item to fill the gap, not wait for quarantine to recover.
- **Default failure policy for v1 should be DiscardAndReplace** (already in PROJECT.md). Quarantine is opt-in, advanced. Don't ship it as default.
- **Provide `IItemFailurePolicy<T>` extensibility** so users can write smart policies (e.g., "discard for connection errors, quarantine for auth errors") — but ship the simple one.
- **Test**: feed bad items repeatedly; verify pool maintains MinSize via discard+replace, doesn't accumulate quarantined dead weight.

**Warning signs:**
- `pool.size` decreases over time despite MinSize being set.
- `pool.quarantined` counter grows unboundedly.
- Pool throughput degrades but no obvious incident.

**Phase to address:** P2 (elasticity & health) — design the failure-policy interface and ship the safe default; P5 (hardening) for stress test on the recovery path.

---

### Pitfall 22: Cascading failure — pool exhaustion masks dependency failure

**What goes wrong:**
RabbitMQ is partially down. Factory takes 25 s per attempt before timing out. App still gets traffic. Acquire calls pile up in the waiter queue. Pool grows up to MaxSize. At MaxSize, every Acquire blocks the full timeout. App threads exhaust. App appears "totally down" — but the actual broker is just slow, not dead. The pool has amplified a partial outage into a full one.

**Why it happens:**
- Pool's job is to provide items; it doesn't reason about dependency health.
- No bulkhead between pool exhaustion and consumer threading.
- No fast-fail mode when factory failures cluster.

**How to avoid:**
- **Aggregate failure tracking**: if factory has failed N times in last M seconds, enter "degraded" mode where new Acquires fast-fail with `PoolDegradedException` (within 100 ms) instead of waiting full timeout.
- **Document Polly composition**: pair Adaptive Pool with a Polly circuit breaker. Show the recipe in README.
- **Surface degraded mode in metrics**: `pool.degraded` boolean gauge.
- **Test**: simulate broker slow-failing for 30 s; verify Acquire times out fast (under 1 s) once degraded mode kicks in, instead of all consumers waiting 25 s.

**Warning signs:**
- App-wide thread pool exhaustion correlated with downstream slowness.
- `pool.acquire.duration` p99 = full timeout for all calls (not just the slow ones).
- App log shows all calls failing with the same timeout exception.

**Phase to address:** P2 (elasticity & health) for degraded mode; P5 for chaos test.

---

### Pitfall 23: SemVer violation on pre-release / hidden breaking changes

**What goes wrong:**
v0.5.0 ships with `IAdaptivePool<T>.AcquireAsync(CancellationToken)`. v0.6.0-alpha refactors to `AcquireAsync(AcquireOptions, CancellationToken)`. Users on `[0.5.0,)` floating range get the alpha; their code breaks. SemVer says "anything before 1.0 has no compatibility guarantees", but in practice users assume 0.x.x is roughly stable and pin loose.

**Why it happens:**
- Pre-1.0 semver is technically unrestricted; in practice, users still expect minor-bump-=-no-break.
- Pre-releases (`-alpha`, `-beta`) get pulled by `Include="*-*"` patterns or `<RestoreAdditionalProjectSources>` quirks.
- Breaking changes in pre-release aren't loud enough.

**How to avoid:**
- **Once you have any user, treat 0.x as if it were 1.x for SemVer.** Bump to 0.(N+1).0 only for breaking, 0.N.(M+1) for additive.
- **Use `Microsoft.CodeAnalysis.PublicApiAnalyzers`**: requires `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt`. CI will fail any PR that adds/changes public API without updating these files. Forces conscious approval of every public surface change.
- **Use `MinVer` and tag releases**. Pre-releases get `-alpha.N` suffix. Document in README that pre-releases may break.
- **Maintain `CHANGELOG.md`** with explicit "Breaking" / "Added" / "Fixed" sections.
- **Don't let breaking changes silently land** in pre-releases. Even alphas should bump major or pre-release name.

**Warning signs:**
- Users file issues like "code stopped compiling after I updated to 0.X".
- `PublicApiAnalyzers` reports unshipped surface changes that weren't in the PR description.
- CHANGELOG is empty or out of date.

**Phase to address:** P5 (hardening) — set up `PublicApiAnalyzers` and CHANGELOG discipline before first NuGet push.

---

### Pitfall 24: Symbol packages misconfigured — debugging steps fail

**What goes wrong:**
NuGet pack runs without `<IncludeSymbols>true</IncludeSymbols>` + `<SymbolPackageFormat>snupkg</SymbolPackageFormat>`. Or symbols are embedded in the main `.nupkg` (deprecated). Users hit a bug, try to step into the library, debugger says "no symbols" or downloads a stale version. Without source link, even with symbols they see the IL view, not the original C#.

**Why it happens:**
- Symbol publishing has multiple modes (embedded, snupkg, MyGet symbol server) — easy to misconfigure.
- SourceLink requires `ContinuousIntegrationBuild=true` only in CI for path normalization; missing this breaks step-through.
- Local builds work fine; only consumers debugging the published package see the failure.

**How to avoid:**
- **Standard config in `Directory.Build.props`** (already documented in STACK.md):
  ```xml
  <PropertyGroup>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
  ```
- **CI pushes both `.nupkg` AND `.snupkg`** to NuGet.org.
- **Use `Microsoft.SourceLink.GitHub`** package as a `PrivateAssets="all"` reference.
- **Verify**: after publish, install package locally in a test app, set a breakpoint in pool code, step in. Should see source.
- **Pre-flight check**: use `Meziantou.Validation` or `dotnet validate package local` to lint the `.nupkg` for symbol/source-link compliance before push.

**Warning signs:**
- Users file issues like "cannot step into AdaptivePool source".
- NuGet Gallery package page does not show "Source repository" link.
- Symbol package upload fails silently in CI.

**Phase to address:** P5 (hardening) — set up packaging before first publish.

Source: [Producing Packages with Source Link — devblogs.microsoft.com](https://devblogs.microsoft.com/dotnet/producing-packages-with-source-link/) (HIGH).

---

## Technical Debt Patterns

Shortcuts that seem reasonable but create long-term problems.

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Hand-rolled `SemaphoreSlim` + `ConcurrentQueue` waiter coordination | Fewer "magic" abstractions; "just use BCL primitives" | Lost wake-ups (Pitfall 1), ABA risk, unbounded debugging time | Never (use `Channel<T>`) |
| Skip the `IPoolItem<T>` wrapper, return `T` directly | Simpler API | Inevitable leaks (Pitfall 2), no place to hook return-on-dispose | Never |
| Use `Task<T>` instead of `ValueTask<T>` for `AcquireAsync` | No multi-await footgun | Allocation per Acquire on hot path | Until perf benchmarks prove the cost matters (probably never for connection pools, but matters for cheap-object pools) |
| Single-signal grow ("if waiter, grow") | Easy to implement | Slow under burst (Pitfall 8) | MVP demo; not for production claim of "adaptive" |
| Single failure policy hardcoded | Less interface surface | Limited to RabbitMQ-style use cases; can't extend to HTTP/DB later | If RabbitMQ is the only use case forever (it's not — PROJECT.md says future adapters) |
| No `IMeterFactory`, just `new Meter(...)` | Simpler constructor | Meter-collision bugs across pool instances (Pitfall 16) | Never — `IMeterFactory` is in-box |
| Skip CHANGELOG, rely on git log | One less file | Users can't tell what changed; Pitfall 23 | Never for OSS |
| Ship without `PublicApiAnalyzers` | Faster initial setup | Accidental ABI breaks become routine | Only pre-v0.1; mandatory by v0.5 |
| Default `AutomaticRecoveryEnabled = true` in adapter | Matches RabbitMQ.Client default | Double-management with pool's failure policy (Pitfall 9) | Never — adapter must override default |
| Don't expose channel-pool's connection-spread target | One less knob | Channel_max=2047 hit at scale (Pitfall 11) | Never |

---

## Integration Gotchas

Common mistakes when connecting to external services.

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| **RabbitMQ.Client v7 IConnection** | Leave `AutomaticRecoveryEnabled = true` (default) when pool manages lifecycle | Set `false` in adapter default; let pool's failure policy own replacement |
| **RabbitMQ.Client v7 IChannel** | Share pooled channel across threads for parallel publishing | Acquire one channel per concurrent publish operation; pool grows accordingly |
| **RabbitMQ.Client v7 BasicProperties** | Reuse old code: `channel.CreateBasicProperties()` | `new BasicProperties { ... }` directly (CreateBasicProperties removed in v7) |
| **RabbitMQ.Client v7 publisher confirms** | Call `ConfirmSelect()` after channel creation | Set `CreateChannelOptions { PublisherConfirmationsEnabled = true }` when calling `CreateChannelAsync(options)` |
| **RabbitMQ heartbeat** | Set heartbeat to 10 minutes "to reduce network noise" | Keep default 60 s; longer than NAT idle timeout = silent drop (Pitfall 12) |
| **RabbitMQ channel_max** | Assume "unlimited" channels per connection | Default is 2047; design for hundreds, spread across multiple connections |
| **Microsoft.Extensions.DI** | Rely on `Dispose` to drain | Implement both `IDisposable` AND `IAsyncDisposable`; document `DisposeAsync` requirement |
| **OpenTelemetry .NET** | `new Meter(...)` directly in pool ctor | Inject `IMeterFactory`, call `factory.Create("Oragon.AdaptivePool")` |
| **Polly v8** | Wrap pool internals with Polly | Document composition: `Polly.Pipeline → AcquireAsync` from consumer side |
| **`Microsoft.Extensions.ObjectPool`** | Build on top as base class | Build directly on BCL primitives; M.E.OP is the wrong abstraction (fixed size, no health) |
| **Testcontainers.RabbitMq v4** | Use static port mapping in tests | Let Testcontainers assign random ports; integrate via the `IContainer` API |

---

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Synchronous health check in `BeforeUse` (Pitfall 6) | Acquire latency = health-check latency on every call | BeforeUse must be < 1 ms; round-trip checks in background sweep only | First sustained traffic; immediately at ~100 acq/s |
| Unbounded grow without concurrent-create cap (Pitfall 5) | Burst causes connection storm, TCP-connect failures, broker rejection | `SemaphoreSlim` capping concurrent factory invocations | First real burst; ~100 simultaneous Acquires when pool starts cold |
| `ActivitySource` always-on without listener check (Pitfall 17) | 5–10% CPU overhead on hot path with no observability gain | `if (Source.HasListeners()) { ... }` guard | High-throughput pools; >10k acq/s |
| High-cardinality metric tags (Pitfall 15) | OTel collector OOM; Prometheus scrape timeouts | Tag schema audit; only static-cardinality tags on metrics | First production deploy; 24h after rollout |
| `ValueTask` boxed in `Task.WhenAll` (Pitfall 19) | Allocation per Acquire instead of zero-alloc fast path | Document `.AsTask()` usage; samples avoid `WhenAll(AcquireAsync())` | High-frequency acquire loops |
| Eager warm-up at app startup (no parallel cap) | App startup latency = N × factory time; readiness probes fail | Warm-up uses same concurrent-factory-cap as runtime grow | Container restart in production; rolling deploy storms |
| Sweep that scans all items synchronously | Sweep tick stalls Acquire when N items × M ms check | Sweep uses Task.WhenAll with cap; or scans incrementally | Pool size > 100 items |
| Allocations on hot path (per-Acquire dictionaries, lambdas) | GC pressure proportional to throughput | BenchmarkDotNet for hot-path allocations; use cached delegates, structs | >50k acq/s sustained |

---

## "Looks Done But Isn't" Checklist

Things that appear complete but are missing critical pieces.

- [ ] **Pool implementation:** Often missing finalizer for leak recovery — verify GC.WaitForPendingFinalizers reclaims leaked items in test (Pitfall 2).
- [ ] **AcquireAsync:** Often missing the linked CancellationTokenSource that combines user ct + pool-shutdown ct + per-factory timeout — verify factory timeout fires even if user ct doesn't (Pitfall 14).
- [ ] **Failure policy:** Often missing the "factory failure backoff" path — verify outage triggers degraded mode within N failures, not after timeout (Pitfall 22).
- [ ] **Sweep:** Often missing failure-rate-aware backoff — verify sweep frequency drops when most items fail (Pitfall 5).
- [ ] **Shrink:** Often missing hysteresis — verify pool doesn't oscillate under sinusoidal load (Pitfall 7).
- [ ] **Grow:** Often missing concurrent-factory cap — verify burst doesn't trigger 100 simultaneous TCP connects (Pitfall 5).
- [ ] **IPoolItem<T>:** Often missing both `IDisposable` AND `IAsyncDisposable` — verify sync `using` and async `await using` both work, and finalizer fallback exists (Pitfall 2, 20).
- [ ] **Meter:** Often using `new Meter(...)` instead of `IMeterFactory.Create(...)` — verify multi-pool tests show isolated metrics (Pitfall 16).
- [ ] **ActivitySource:** Often missing `HasListeners()` guard — benchmark Acquire with and without listener (Pitfall 17).
- [ ] **Telemetry tags:** Often includes per-item or per-call tags — audit tag schema; only `pool.name` should appear on metric tags (Pitfall 15).
- [ ] **DI registration:** Often missing graceful drain wiring — verify host shutdown actually drains in-flight (Pitfall 20).
- [ ] **RabbitMQ adapter:** Often leaves `AutomaticRecoveryEnabled = true` — verify the extension method overrides this default (Pitfall 9).
- [ ] **RabbitMQ channel pool:** Often pins all channels to one connection — verify channels spread across multiple connections (Pitfall 11).
- [ ] **RabbitMQ heartbeat:** Often left at user-overridable with no validation — verify warning log when set unreasonably (Pitfall 12).
- [ ] **Sample code:** Often shows shared channel publishing — verify sample explicitly Acquires per-publish (Pitfall 10).
- [ ] **NuGet package:** Often missing `.snupkg` symbol package — verify NuGet Gallery shows "Source repository" link after publish (Pitfall 24).
- [ ] **PublicAPI tracking:** Often skipped — verify `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` exist and CI rejects unintended changes (Pitfall 23).
- [ ] **CHANGELOG:** Often empty after first PR — enforce changelog entry in PR template (Pitfall 23).
- [ ] **Multi-target build:** Often passes on net10 only because dev box doesn't have older SDKs — verify CI matrix actually runs all 3 TFMs (Pitfall 18).
- [ ] **Stress tests:** Often "exists" but only with single-thread; verify burst-then-drain ping-pong pattern under N=32 threads (Pitfall 1).

---

## Recovery Strategies

When pitfalls occur despite prevention, how to recover.

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Lost wake-up (deadlock) | HIGH | 1) Add temporary watchdog logging that emits stack of every >10s Acquire wait. 2) Force restart pool. 3) Refactor waiter coordination to `Channel<T>` if not already. |
| Item leaks (Pitfall 2) | MEDIUM | 1) `pool.leaked` counter alarm. 2) Force-drain via `pool.DrainAsync(0)` or restart. 3) Audit consumer code for missing `using`/`await using`. |
| Counter drift (Pitfall 3) | LOW (if detected early) | 1) Restart restores state. 2) Add Interlocked counter invariant test. |
| ABA (Pitfall 4) | HIGH | 1) Hard restart. 2) Replace lock-free CAS with `Channel<T>`. 3) Add "in-use generation" assertions. |
| Sweep amplification (Pitfall 5) | MEDIUM | 1) Increase sweep interval as hotfix config. 2) Add backoff logic. 3) Pair with circuit breaker on consumer side. |
| Costly BeforeUse (Pitfall 6) | LOW | 1) Disable BeforeUse temporarily via config. 2) Move expensive checks to background sweep. |
| Oscillation (Pitfall 7) | LOW | 1) Increase IdleTimeout config 2-3x. 2) Add hysteresis to shrink algorithm. |
| Slow burst growth (Pitfall 8) | LOW | 1) Decrease grow threshold; increase grow batch size. 2) Implement composite-signal triggers. |
| Auto-recovery vs pool conflict (Pitfall 9) | MEDIUM | 1) Set `AutomaticRecoveryEnabled = false` in ConnectionFactory. 2) Restart app. |
| Channel sharing (Pitfall 10) | MEDIUM | 1) Audit consumer code; refactor to per-publish channel acquire. 2) Add debug-build proxy that throws on concurrent use. |
| Channel_max exhaustion (Pitfall 11) | MEDIUM | 1) Increase connection pool MaxSize. 2) Lower channels-per-connection target. 3) Increase broker `channel_max` (last resort). |
| Heartbeat misconfigured (Pitfall 12) | LOW | 1) Restart with default heartbeat. 2) Document network's NAT timeout. |
| Confirm misalignment (Pitfall 13) | HIGH (data integrity) | 1) Audit publishes; force one-channel-per-publish discipline. 2) Verify message arrival via consumer-side dedup if possible. |
| Cancellation gaps (Pitfall 14) | LOW | 1) Add per-factory timeout to ConnectionFactory or pool config. 2) Plumb ct through hooks. |
| High-cardinality metrics (Pitfall 15) | MEDIUM | 1) Drop high-cardinality tags via OTel relabeling rule (collector side). 2) Fix tag schema in next release. |
| Meter lifetime (Pitfall 16) | LOW | 1) Switch to `IMeterFactory`. 2) Restart drops bad meter registrations. |
| ActivitySource cost (Pitfall 17) | LOW | 1) Add `HasListeners()` guards. 2) Sample if necessary. |
| `System.Threading.Lock` build break (Pitfall 18) | LOW | 1) Replace with `object` lock. 2) Remove `#if`. |
| ValueTask misuse (Pitfall 19) | LOW | 1) Update samples. 2) Document multi-await rule clearly. |
| Dispose not called (Pitfall 20) | MEDIUM | 1) Add `DrainAsync` API; document explicit calling. 2) Implement `IDisposable` fallback. |
| Quarantine never recovers (Pitfall 21) | MEDIUM | 1) Cap backoff. 2) Switch to discard+replace as default. |
| Cascading failure (Pitfall 22) | MEDIUM | 1) Restart app to clear waiters. 2) Add degraded-mode fast-fail. 3) Pair with Polly circuit breaker. |
| SemVer break (Pitfall 23) | HIGH (consumer trust) | 1) Yank affected version on NuGet. 2) Publish patch with API restored. 3) Adopt `PublicApiAnalyzers`. |
| Symbols missing (Pitfall 24) | LOW | 1) Reconfigure pack settings. 2) Republish package + `.snupkg`. |

---

## Pitfall-to-Phase Mapping

How roadmap phases should address these pitfalls.

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| 1. Lost wake-up | P1 (core) | Stress test: N threads, MaxSize=1, ping-pong, watchdog asserts no Acquire >5 s |
| 2. Item leak | P1 (core) | Test: throw between Acquire and Dispose, GC.WaitForPendingFinalizers, item recovered |
| 3. Counter drift on factory exception | P1 (core) | Test: factory throws on every Nth call; counters match observable item count |
| 4. ABA in lock-free queue | P1 (core) | Decision: use `Channel<T>` not custom CAS. Stress test asserts no item observed by 2 threads |
| 5. Sweep amplification | P2 (elasticity & health) | Chaos test: simulate downstream outage; sweep frequency drops, factory cap holds |
| 6. Costly BeforeUse on hot path | P2 (elasticity & health) | Benchmark: Acquire latency with vs without BeforeUse, p99 delta < 1 ms |
| 7. Grow/shrink oscillation | P2 (elasticity & health) | Sinusoidal-load test, pool size doesn't oscillate >20% |
| 8. Slow burst growth | P2 (elasticity & health) | Spike test: 200 simultaneous Acquires from pool=10, p99 < 500 ms, steady-state in 1 s |
| 9. RabbitMQ auto-recovery conflict | P4 (RabbitMQ adapter) | `AddAdaptiveConnectionPool` sets `AutomaticRecoveryEnabled = false` by default; integration test verifies |
| 10. Shared channel publishing | P4 (RabbitMQ adapter) | Sample demonstrates per-publish Acquire; README anti-pattern section |
| 11. channel_max exhaustion | P4 (RabbitMQ adapter) | Integration test with `channel_max=10` on Testcontainers; channels spread across connections |
| 12. Heartbeat misconfig | P4 (RabbitMQ adapter) | Default heartbeat 60 s; warning log when user sets > 2 min |
| 13. Confirms + concurrent publish | P4 (RabbitMQ adapter) | Sample shows confirm-with-await per channel; anti-pattern documented |
| 14. Cancellation token gaps | P1 (core) | All hook signatures take `CancellationToken`; linked-CTS plumbing; test factory ignoring ct still cancels |
| 15. High-cardinality metric tags | P3 (telemetry & DI) | Tag schema audit; integration test counts unique series per metric (≤ 1 per pool name) |
| 16. Meter lifetime | P3 (telemetry & DI) | `IMeterFactory` used; multi-pool test shows isolated metrics |
| 17. ActivitySource always-on cost | P3 (telemetry & DI) | Benchmark with/without listener; HasListeners() guard verified |
| 18. `System.Threading.Lock` multi-target | P1 (core) | Use `object` lock; CI runs all 3 TFMs |
| 19. ValueTask multi-await | P1 (core) | XML doc + samples; tests use single await |
| 20. IAsyncDisposable not called | P1 (core) for design; P4 for RMQ specifics | Test: Dispose-only path; pool still cleans up; explicit DrainAsync API works |
| 21. Quarantine never recovers | P2 (elasticity & health) | Default policy is DiscardAndReplace; quarantine has capped backoff; test bad-item flood doesn't shrink pool |
| 22. Cascading failure | P2 (elasticity & health) | Chaos test: slow factory, degraded mode kicks in, Acquire fast-fails |
| 23. SemVer/breaking change | P5 (hardening) | `PublicApiAnalyzers` + CHANGELOG enforcement in CI |
| 24. Symbols/source-link broken | P5 (hardening) | Post-publish smoke test: install package in test app, step into source successfully |

### Phase Risk Profile

| Phase | Pitfalls Addressed | Risk Profile |
|-------|--------------------|--------------|
| P1 — Core skeleton | 1, 2, 3, 4, 14, 18, 19, 20 (design) | HIGH — foundational decisions; most pitfalls are unrecoverable from later. The waiter-coordination primitive choice (`Channel<T>`), wrapper design with finalizer, and hook signatures with `CancellationToken` cannot be retrofitted without breaking changes. |
| P2 — Elasticity & health | 5, 6, 7, 8, 21, 22 | HIGH — composite-signal grow + hysteretic shrink + failure-policy interface together are the marquee differentiator. Get this wrong and the library is just a slow `Microsoft.Extensions.ObjectPool`. |
| P3 — Telemetry & DI | 15, 16, 17 | MEDIUM — observability quality affects adoption. Errors here are recoverable (fix in next minor) but cardinality leaks have ops cost. |
| P4 — RabbitMQ adapter | 9, 10, 11, 12, 13, 20 (RMQ specifics) | MEDIUM-HIGH — RabbitMQ-specific defaults (`AutomaticRecoveryEnabled = false`, channel-per-connection target, heartbeat preservation) directly affect production behavior. Sample/README enforces channel-discipline patterns. |
| P5 — Hardening & OSS | 23, 24, plus chaos tests for 1, 5, 22 | MEDIUM — OSS quality bar is reputation-defining; chaos tests catch regressions in earlier phases. |

---

## Sources

- [RabbitMQ Channels documentation](https://www.rabbitmq.com/docs/channels) — single-digit channels per connection guidance, channel-per-operation antipattern, channel leak signs (HIGH, May 2026 fetch)
- [RabbitMQ Configuration](https://www.rabbitmq.com/docs/configure) — channel_max default 2047, "0 = unlimited" warning (HIGH, May 2026 fetch)
- [RabbitMQ .NET Client API Guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) — IChannel concurrent-publish hard requirement, automatic recovery initial-failure caveat, publishes-during-recovery rejected (HIGH, May 2026 fetch)
- [RabbitMQ .NET Client v7 Migration Guide](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/main/v7-MIGRATION.md) — IModel→IChannel rename, async API, BasicProperties (HIGH, May 2026 fetch)
- [.NET Observability with OpenTelemetry — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel) — `IMeterFactory` is the sanctioned lifetime owner (HIGH)
- [Producing Packages with Source Link — devblogs.microsoft.com](https://devblogs.microsoft.com/dotnet/producing-packages-with-source-link/) — `ContinuousIntegrationBuild`, `PublishRepositoryUrl`, snupkg (HIGH)
- [HikariCP — GitHub](https://github.com/brettwooldridge/HikariCP) — connection-storm and oscillation patterns synthesized from issue tracker (MEDIUM)
- [Apache commons-pool2 GenericObjectPool](https://commons.apache.org/proper/commons-pool/) — testWhileIdle cadence, failure-policy semantics (MEDIUM)
- [Reactor Pool GracefulShutdownInstrumentedPool](https://projectreactor.io/docs/pool/snapshot/api/reactor/pool/decorators/GracefulShutdownInstrumentedPool.html) — graceful drain pattern (MEDIUM)
- [SQLAlchemy connection pooling docs (max lifetime / pre-ping patterns)](https://docs.sqlalchemy.org/en/20/core/pooling.html) — pre-ping vs idle-recycle tradeoff parallel (MEDIUM)
- [Microsoft.Extensions.ObjectPool — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/performance/objectpool) — base case for what NOT to inherit (HIGH)
- Internal experience: composite-signal grow / hysteretic shrink algorithm shape — synthesized from cross-ecosystem pool research (MEDIUM)

---
*Pitfalls research for: .NET adaptive object pool library + RabbitMQ adapter (Oragon.AdaptivePool)*
*Researched: 2026-05-02*
