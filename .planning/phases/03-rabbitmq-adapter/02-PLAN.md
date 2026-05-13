---
phase: 03-rabbitmq-adapter
plan: 02
type: execute
wave: 2
depends_on: ["03-01"]
files_modified:
  - src/Oragon.ElasticPool.RabbitMQ/Builder/ElasticChannelPoolBuilder.cs
  - src/Oragon.ElasticPool.RabbitMQ/Internals/ChannelLeasePairing.cs
  - src/Oragon.ElasticPool.RabbitMQ/Internals/ConnectionChannelTracker.cs
  - src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs
  - src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt
autonomous: true
requirements: [RMQ-02, RMQ-04]
must_haves:
  truths:
    - "Calling `services.AddElasticChannelPool(name, connectionPoolName, configurePool)` registers a working `IElasticPool<IChannel>` whose Factory acquires a connection from the inner pool by `connectionPoolName`."
    - "Each created `IChannel` is paired to its borrowed `IPoolItem<IConnection>` lease via `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` so the lease can be released when the channel is released."
    - "Channel-spread is enforced eagerly: when a candidate connection has reached `MaxChannelsPerConnection` (default 100), the Factory acquires a different connection from the pool instead of overloading the saturated one (per Q2 recommendation)."
    - "BeforeUse hook returns Unhealthy when EITHER `IChannel.IsOpen` OR the paired `IConnection.IsOpen` is false (lazy cross-pool invalidation per Q1)."
    - "Release hook closes the channel, disposes it, removes the pairing entry, and disposes the connection lease (returning the connection to its pool)."
    - "Default `CreateChannelOptions` enables publisher confirmations (PublisherConfirmationsEnabled=true, PublisherConfirmationTrackingEnabled=true) per Pitfall E."
  artifacts:
    - path: "src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs"
      provides: "AddElasticChannelPool extension method"
      exports: ["AddElasticChannelPool"]
    - path: "src/Oragon.ElasticPool.RabbitMQ/Builder/ElasticChannelPoolBuilder.cs"
      provides: "Channel pool builder with WithBounds/IdleTimeout/WithChannelOptions/WithMaxChannelsPerConnection"
      exports: ["ElasticChannelPoolBuilder"]
    - path: "src/Oragon.ElasticPool.RabbitMQ/Internals/ChannelLeasePairing.cs"
      provides: "ConditionalWeakTable<IChannel, IPoolItem<IConnection>> wrapper for pairing lookup/add/remove"
    - path: "src/Oragon.ElasticPool.RabbitMQ/Internals/ConnectionChannelTracker.cs"
      provides: "ConcurrentDictionary<IConnection, int> tracking channels-per-connection for eager spread"
  key_links:
    - from: "ElasticChannelPoolServiceCollectionExtensions.AddElasticChannelPool"
      to: "Oragon.ElasticPool.Core ServiceCollectionExtensions.AddElasticPool<IChannel>"
      via: "delegated registration"
      pattern: "AddElasticPool<IChannel>"
    - from: "Channel pool Factory hook"
      to: "connectionPool.AcquireAsync"
      via: "sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connectionPoolName)"
      pattern: "GetRequiredKeyedService<IElasticPool<IConnection>>"
    - from: "Channel pool Factory hook"
      to: "IConnection.CreateChannelAsync"
      via: "RabbitMQ.Client v7 async API"
      pattern: "CreateChannelAsync"
    - from: "Release hook"
      to: "IPoolItem<IConnection>.DisposeAsync"
      via: "ChannelLeasePairing.TryGetAndRemove"
      pattern: "ChannelLeasePairing"
---

<objective>
Layer the channel pool over the connection pool from Plan 01. Ship `services.AddElasticChannelPool(name, connectionPoolName, configurePool)`, the channel-to-connection lease pairing via `ConditionalWeakTable`, and an eager channel-spread strategy that respects `MaxChannelsPerConnection` (default 100) per RESEARCH Q2.

Purpose: Cover RMQ-02 (channel pool layered on connection pool) and the rest of RMQ-04 (sister-library naming consistency for the channel pool extension).

Output:
- Public surface: `ElasticChannelPoolBuilder`, `AddElasticChannelPool`.
- Internal: `ChannelLeasePairing` (CWT wrapper), `ConnectionChannelTracker` (eager-spread counter).
- Lazy cross-pool invalidation in `BeforeUse` (per Q1 — eager event API NOT added to Core; deferred to v2 unless Plan 03 integration tests surface a defect).
</objective>

<execution_context>
@/mnt/p/dynamic-pool/.claude/get-shit-done/workflows/execute-plan.md
@/mnt/p/dynamic-pool/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/REQUIREMENTS.md
@.planning/research/PITFALLS.md
@.planning/phases/03-rabbitmq-adapter/03-CONTEXT.md
@.planning/phases/03-rabbitmq-adapter/03-RESEARCH.md
@.planning/phases/03-rabbitmq-adapter/03-01-PLAN.md
@src/Oragon.ElasticPool.Core/Abstractions/IElasticPool.cs
@src/Oragon.ElasticPool.Core/Abstractions/IPoolItem.cs
@src/Oragon.ElasticPool.Core/Builder/ElasticPoolBuilder.cs
@src/Oragon.ElasticPool.RabbitMQ/Builder/ElasticConnectionPoolBuilder.cs
@src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticConnectionPoolServiceCollectionExtensions.cs

<interfaces>
<!-- Carry-forward facts from Plan 01 + Core. -->

From Plan 01 (this phase):
```csharp
namespace Oragon.ElasticPool.RabbitMQ.Builder;

public sealed class ElasticConnectionPoolBuilder
{
    public int MinSize { get; }
    public int MaxSize { get; }
    public int InitialSize { get; }
    public TimeSpan IdleTimeout { get; }
    public ElasticConnectionPoolBuilder WithBounds(int min, int max, int initial);
    public ElasticConnectionPoolBuilder WithIdleTimeout(TimeSpan t);
}

namespace Oragon.ElasticPool.RabbitMQ.DependencyInjection;
public static class ElasticConnectionPoolServiceCollectionExtensions
{
    public static IServiceCollection AddElasticConnectionPool(
        this IServiceCollection services, string name,
        Action<ConnectionFactory>? configureFactory,
        Action<ElasticConnectionPoolBuilder> configurePool);
}
```

From RabbitMQ.Client 7.2.1 (verified per RESEARCH.md):
```csharp
public interface IConnection : IAsyncDisposable, IDisposable
{
    bool IsOpen { get; }
    Task<IChannel> CreateChannelAsync(CreateChannelOptions? options = null, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}

public interface IChannel : IAsyncDisposable, IDisposable
{
    bool IsOpen { get; }
    Task CloseAsync(CancellationToken cancellationToken = default);
    // ... publish/consume methods (not needed in this plan)
}

public sealed class CreateChannelOptions
{
    // VERIFIED ctor signature from rabbitmq.github.io API docs:
    public CreateChannelOptions(
        bool publisherConfirmationsEnabled,
        bool publisherConfirmationTrackingEnabled,
        RateLimiter? outstandingPublisherConfirmationsRateLimiter = null,
        ushort? consumerDispatchConcurrency = 1);
}
```

From Core (already used in Plan 01):
```csharp
public interface IPoolItem<out T> : IDisposable, IAsyncDisposable
{
    T Value { get; }   // The pooled instance
}

public interface IElasticPool<T> : IDisposable, IAsyncDisposable where T : notnull
{
    int Available { get; }
    int InUse { get; }
    ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default);
}
```

From System BCL (used here):
```csharp
public sealed class ConditionalWeakTable<TKey, TValue>
    where TKey : class
    where TValue : class?
{
    public void Add(TKey key, TValue value);
    public bool TryGetValue(TKey key, out TValue value);
    public bool Remove(TKey key);
}
```
</interfaces>
</context>

<tasks>

<task type="auto" tdd="true">
  <name>Task 1: ChannelLeasePairing + ConnectionChannelTracker + ElasticChannelPoolBuilder</name>
  <files>
    src/Oragon.ElasticPool.RabbitMQ/Internals/ChannelLeasePairing.cs,
    src/Oragon.ElasticPool.RabbitMQ/Internals/ConnectionChannelTracker.cs,
    src/Oragon.ElasticPool.RabbitMQ/Builder/ElasticChannelPoolBuilder.cs,
    src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt
  </files>
  <behavior>
    - Test 1 (Plan 03 Task 1 unit tests): `ChannelLeasePairing.Add(channel, lease)` followed by `TryGet(channel)` returns the lease. After `TryRemove(channel)`, `TryGet` returns false.
    - Test 2: Pairing keys are weak — when the channel is GC-collected, the lease entry is reclaimable (the GC behavior itself is non-deterministic in tests; assert via no-leak property instead — `Add` never throws on duplicate keys after a remove).
    - Test 3: `ConnectionChannelTracker.TryAcquireSlot(connection, max)` returns true and increments count when count < max; returns false (no increment) when count == max.
    - Test 4: `ConnectionChannelTracker.ReleaseSlot(connection)` decrements (never goes below 0); when count reaches 0 it removes the entry to bound dictionary growth.
    - Test 5: `ElasticChannelPoolBuilder` defaults: MinSize=0, MaxSize=32, InitialSize=0, IdleTimeout=60s, MaxChannelsPerConnection=100, ChannelOptions has PublisherConfirmationsEnabled=true && PublisherConfirmationTrackingEnabled=true.
    - Test 6: `WithMaxChannelsPerConnection(0)` throws ArgumentOutOfRangeException; `WithMaxChannelsPerConnection(2047)` is accepted.
    - Test 7: `WithChannelOptions(null!)` throws ArgumentNullException; `WithChannelOptions(custom)` replaces the default.
  </behavior>
  <action>
    1. Create `Internals/ChannelLeasePairing.cs` — internal sealed class wrapping `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>`:
       ```csharp
       internal sealed class ChannelLeasePairing
       {
           private readonly ConditionalWeakTable<IChannel, IPoolItem<IConnection>> _table = new();
           public void Add(IChannel channel, IPoolItem<IConnection> lease) { /* CWT.Add throws on duplicate; this is correct — duplicates indicate a logic bug */ _table.Add(channel, lease); }
           public bool TryGet(IChannel channel, out IPoolItem<IConnection>? lease) { return _table.TryGetValue(channel, out lease!); }
           public bool TryRemove(IChannel channel) { return _table.Remove(channel); }
       }
       ```
       Use `?` on the `out` parameter to satisfy nullable analysis (CWT's `TryGetValue` signature has a non-null reference output but we want to be explicit).

    2. Create `Internals/ConnectionChannelTracker.cs` — internal sealed class:
       ```csharp
       internal sealed class ConnectionChannelTracker
       {
           private readonly ConcurrentDictionary<IConnection, int> _counts = new();

           // Atomic try-increment-if-below-max. Returns true if a slot was acquired.
           public bool TryAcquireSlot(IConnection connection, int max)
           {
               while (true)
               {
                   var current = _counts.TryGetValue(connection, out var existing) ? existing : 0;
                   if (current >= max) return false;
                   var next = current + 1;
                   if (current == 0)
                   {
                       if (_counts.TryAdd(connection, next)) return true;
                       // race — re-read
                   }
                   else
                   {
                       if (_counts.TryUpdate(connection, next, current)) return true;
                       // race — re-read
                   }
               }
           }

           public void ReleaseSlot(IConnection connection)
           {
               while (true)
               {
                   if (!_counts.TryGetValue(connection, out var current)) return;
                   if (current <= 1)
                   {
                       // Use ICollection<KeyValuePair<,>> Remove for atomic compare-remove in older runtimes; use TryRemove(Key, Value) on net8+.
                       if (((ICollection<KeyValuePair<IConnection, int>>)_counts).Remove(new KeyValuePair<IConnection, int>(connection, current)))
                           return;
                   }
                   else
                   {
                       if (_counts.TryUpdate(connection, current - 1, current)) return;
                   }
               }
           }

           public int CountFor(IConnection connection) =>
               _counts.TryGetValue(connection, out var c) ? c : 0;
       }
       ```
       Document the atomicity invariant in XML comments. The `ICollection<KVP>.Remove` cast is the canonical "atomic remove if value matches" idiom for `ConcurrentDictionary<TKey,TValue>` on net8 — net9+ has `TryRemove(KeyValuePair<,>)` natively but the cast works on all 3 TFMs.

    3. Create `Builder/ElasticChannelPoolBuilder.cs` — public sealed class:
       ```csharp
       public sealed class ElasticChannelPoolBuilder
       {
           public int MinSize { get; private set; } = 0;
           public int MaxSize { get; private set; } = 32;
           public int InitialSize { get; private set; } = 0;
           public TimeSpan IdleTimeout { get; private set; } = TimeSpan.FromSeconds(60);
           public int MaxChannelsPerConnection { get; private set; } = 100;
           public CreateChannelOptions ChannelOptions { get; private set; } =
               new CreateChannelOptions(
                   publisherConfirmationsEnabled: true,
                   publisherConfirmationTrackingEnabled: true,
                   outstandingPublisherConfirmationsRateLimiter: null,
                   consumerDispatchConcurrency: 1);

           public ElasticChannelPoolBuilder WithBounds(int min, int max, int initial) { /* validate 0 <= min <= initial <= max */ ... return this; }
           public ElasticChannelPoolBuilder WithIdleTimeout(TimeSpan t) { /* validate > 0 */ ... return this; }
           public ElasticChannelPoolBuilder WithMaxChannelsPerConnection(int n) { if (n < 1 || n > 2047) throw new ArgumentOutOfRangeException(nameof(n), "MaxChannelsPerConnection must be in [1, 2047] — broker default channel_max is 2047."); MaxChannelsPerConnection = n; return this; }
           public ElasticChannelPoolBuilder WithChannelOptions(CreateChannelOptions options) { ChannelOptions = options ?? throw new ArgumentNullException(nameof(options)); return this; }
       }
       ```
       The 2047 ceiling validation is per Pitfall 11 — broker default `channel_max=2047`; allow up to that, but operator can lower.

    4. Update `PublicAPI.Unshipped.txt` for the new public type and methods (Builder; the two internals are NOT public).

    5. Verify: `dotnet build` exits 0; PublicApiAnalyzers happy.
  </action>
  <verify>
    <automated>
      cd /mnt/p/dynamic-pool && \
      dotnet build src/Oragon.ElasticPool.RabbitMQ/Oragon.ElasticPool.RabbitMQ.csproj -c Release && \
      grep -c "ElasticChannelPoolBuilder" src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt && \
      grep -v '^#' src/Oragon.ElasticPool.RabbitMQ/Internals/ChannelLeasePairing.cs | grep -c "ConditionalWeakTable<IChannel" && \
      grep -v '^#' src/Oragon.ElasticPool.RabbitMQ/Internals/ConnectionChannelTracker.cs | grep -c "ConcurrentDictionary<IConnection"
    </automated>
  </verify>
  <done>
    - Build clean across all 3 TFMs.
    - PublicAPI.Unshipped.txt now lists `ElasticChannelPoolBuilder` and its public members.
    - Internals are NOT exposed in PublicAPI files.
    - Grep confirms `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` is the actual data structure (per locked decision in CONTEXT.md).
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 2: AddElasticChannelPool extension with eager-spread Factory + lazy invalidation BeforeUse</name>
  <files>
    src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs,
    src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt
  </files>
  <behavior>
    - Test 1 (Plan 03 Task 1 unit tests): Registering both connection and channel pools with the same `name` and resolving `IElasticPool<IChannel>` returns a non-null pool.
    - Test 2: First `AcquireAsync` on the channel pool calls `connectionPool.AcquireAsync` exactly once and `IConnection.CreateChannelAsync` exactly once with the configured `CreateChannelOptions`.
    - Test 3: When the channel pool's BeforeUse runs and BOTH `IChannel.IsOpen=true` AND the paired `IConnection.IsOpen=true`, returns Healthy. When EITHER is false, returns Unhealthy.
    - Test 4: When 100 channels are acquired against a single substituted IConnection (default `MaxChannelsPerConnection=100`), the 101st acquire causes the Factory to call `connectionPool.AcquireAsync` AGAIN to get a different connection. (Substitute the connection pool to return two distinct IConnection mocks across two AcquireAsync calls; assert second acquire was triggered when tracker said "first connection saturated".)
    - Test 5: When the channel pool's Release fires, `IChannel.CloseAsync` is called, then `IChannel.DisposeAsync`, then `ChannelLeasePairing.TryRemove`, then `IPoolItem<IConnection>.DisposeAsync` (the connection lease is returned to the connection pool). `ConnectionChannelTracker.ReleaseSlot` is called BEFORE the lease dispose.
    - Test 6: When `IConnection.CreateChannelAsync` throws, the borrowed connection lease is disposed (returned to the pool) BEFORE the exception propagates — no connection leak.
  </behavior>
  <action>
    1. Create `DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs` — public static class. Single public method:
       ```csharp
       public static IServiceCollection AddElasticChannelPool(
           this IServiceCollection services,
           string name,
           string connectionPoolName,
           Action<ElasticChannelPoolBuilder> configurePool)
       ```

       Implementation:
       a. ArgumentNullException.ThrowIfNull on services, name, connectionPoolName, configurePool.
       b. `var chBuilder = new ElasticChannelPoolBuilder(); configurePool(chBuilder);` — capture once at registration.
       c. Capture pairing+tracker as closure state — single instance per channel-pool registration:
          ```csharp
          var pairing = new ChannelLeasePairing();
          var tracker = new ConnectionChannelTracker();
          ```
       d. Delegate to Core:
          ```csharp
          services.AddElasticPool<IChannel>(name, builder =>
          {
              builder
                  .Factory(async (sp, ct) => await CreateChannelWithSpreadAsync(sp, connectionPoolName, chBuilder, pairing, tracker, ct).ConfigureAwait(false))
                  .BeforeUse((ch, _) =>
                  {
                      if (!ch.IsOpen) return ValueTask.FromResult(PoolState.Unhealthy);
                      if (pairing.TryGet(ch, out var connLease) && connLease is not null && !connLease.Value.IsOpen)
                          return ValueTask.FromResult(PoolState.Unhealthy);
                      return ValueTask.FromResult(PoolState.Healthy);
                  })
                  .Check((ch, _) =>
                      ValueTask.FromResult(ch.IsOpen ? PoolState.Healthy : PoolState.Unhealthy))
                  .Release(async (ch, ct) =>
                  {
                      try { await ch.CloseAsync(ct).ConfigureAwait(false); } catch { /* swallow */ }
                      await ch.DisposeAsync().ConfigureAwait(false);
                      if (pairing.TryGet(ch, out var connLease) && connLease is not null)
                      {
                          // Tracker bookkeeping BEFORE returning lease to pool — avoids race where another Factory call
                          // sees the connection as still-saturated.
                          tracker.ReleaseSlot(connLease.Value);
                          pairing.TryRemove(ch);
                          await connLease.DisposeAsync().ConfigureAwait(false);
                      }
                  })
                  .WithBounds(chBuilder.MinSize, chBuilder.MaxSize, chBuilder.InitialSize)
                  .IdleTimeout(chBuilder.IdleTimeout);
          });
          return services;
          ```

       e. Implement `CreateChannelWithSpreadAsync` as a private static helper (eager-spread per Q2):
          ```csharp
          private static async ValueTask<IChannel> CreateChannelWithSpreadAsync(
              IServiceProvider sp, string connectionPoolName,
              ElasticChannelPoolBuilder chBuilder,
              ChannelLeasePairing pairing,
              ConnectionChannelTracker tracker,
              CancellationToken ct)
          {
              var connectionPool = sp.GetRequiredKeyedService<IElasticPool<IConnection>>(connectionPoolName);

              // Acquire connections from the pool until we find one with a free slot. Bounded retries
              // by `MaxSize` of the connection pool — if every connection is saturated, the pool's
              // own elasticity will grow to give us a fresh one (or fail with PoolExhaustedException
              // which we propagate).
              const int MaxAttempts = 16; // safety net; in practice 1-2 attempts suffice
              List<IPoolItem<IConnection>>? rejected = null;
              IPoolItem<IConnection>? selected = null;
              try
              {
                  for (int attempt = 0; attempt < MaxAttempts; attempt++)
                  {
                      var lease = await connectionPool.AcquireAsync(ct).ConfigureAwait(false);
                      if (tracker.TryAcquireSlot(lease.Value, chBuilder.MaxChannelsPerConnection))
                      {
                          selected = lease;
                          break;
                      }
                      // Saturated — hold onto it briefly, keep acquiring; release ALL non-selected leases at the end.
                      (rejected ??= new()).Add(lease);
                  }
                  if (selected is null)
                      throw new InvalidOperationException(
                          $"AddElasticChannelPool: could not find a connection with free channel slot in pool '{connectionPoolName}' after {MaxAttempts} attempts. " +
                          "Increase MaxChannelsPerConnection or the connection pool's MaxSize.");

                  IChannel channel;
                  try
                  {
                      channel = await selected.Value.CreateChannelAsync(chBuilder.ChannelOptions, ct).ConfigureAwait(false);
                  }
                  catch
                  {
                      tracker.ReleaseSlot(selected.Value);
                      throw;
                  }

                  pairing.Add(channel, selected);
                  selected = null; // ownership transferred to pairing
                  return channel;
              }
              finally
              {
                  // Return any rejected (saturated) connections to the pool.
                  if (rejected is not null)
                      foreach (var r in rejected)
                          try { await r.DisposeAsync().ConfigureAwait(false); } catch { /* don't mask primary exception */ }
                  // If we threw before transferring ownership of `selected`, return it too.
                  if (selected is not null)
                      try { await selected.DisposeAsync().ConfigureAwait(false); } catch { /* don't mask */ }
              }
          }
          ```

       f. Add XML doc on the public method:
          - RMQ-02 + RMQ-04 conventions.
          - Document `MaxChannelsPerConnection` semantics and the eager-spread strategy.
          - Document the LAZY cross-pool invalidation (Q1) — `BeforeUse` re-checks both `IChannel.IsOpen` and the paired `IConnection.IsOpen`. NOTE that the eager callback path is a deferred v2 feature and that integration tests in Plan 03 will validate whether lazy is sufficient.

    2. Update `PublicAPI.Unshipped.txt` for the new public extension method.

    3. Verify: full solution build clean; Core tests still pass.
  </action>
  <verify>
    <automated>
      cd /mnt/p/dynamic-pool && \
      dotnet build Oragon.ElasticPool.sln -c Release && \
      grep -v '^#' src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs | grep -c "AddElasticPool<IChannel>" && \
      grep -v '^#' src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs | grep -c "TryAcquireSlot" && \
      grep -v '^#' src/Oragon.ElasticPool.RabbitMQ/DependencyInjection/ElasticChannelPoolServiceCollectionExtensions.cs | grep -c "ReleaseSlot" && \
      grep -c "AddElasticChannelPool" src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt && \
      dotnet test tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj -c Release --no-build
    </automated>
  </verify>
  <done>
    - Build clean.
    - All Phase 1+2 Core tests still pass (no regression — we added internal-visibility-only paths, plus a new project not yet under test).
    - The Factory implementation explicitly demonstrates eager-spread: `tracker.TryAcquireSlot(lease.Value, chBuilder.MaxChannelsPerConnection)` is invoked PER ACQUIRE before deciding to use the connection.
    - `BeforeUse` performs the lazy weak-table re-probe (one `pairing.TryGet` call followed by `connLease.Value.IsOpen` check).
    - PublicAPI.Unshipped.txt now lists both extension methods (`AddElasticConnectionPool` from Plan 01, `AddElasticChannelPool` here).
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Channel pool consumer → adapter | Adapter is in-process; channel handed to consumer is a real `IChannel` from RabbitMQ.Client |
| Adapter → connection pool (Plan 01) | Internal — same process, same author |
| Channel ↔ connection pairing via CWT | Uses GC weak refs; if a consumer leaks a channel without disposing, the pairing entry is reclaimable but the underlying connection lease may be held until finalization |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-03-07 | Denial of Service | Saturated single connection causes channel pool to spin in `CreateChannelWithSpreadAsync` waiting for new connection | mitigate | `MaxAttempts=16` safety net + bubble `PoolExhaustedException` from connection pool when its MaxSize is reached |
| T-03-08 | Tampering | Concurrent `tracker.TryAcquireSlot`/`ReleaseSlot` race producing negative count or count > max | mitigate | Atomic CAS via `ConcurrentDictionary.TryUpdate` + lower-bound guard in ReleaseSlot |
| T-03-09 | Information Disclosure | Per Pitfall 10, sharing one channel across publishing threads can interleave frames — but adapter only HANDS OUT channels via pool acquire | accept | Out of adapter's control; documented in XML doc + sample (Plan 03) demonstrates per-acquire pattern |
| T-03-10 | Tampering | `ChannelLeasePairing.Add` throws on duplicate keys (CWT contract) — could happen if a channel is paired twice in a buggy Factory | mitigate | Factory is the SINGLE add site; assigned ownership transfer pattern (`selected = null` after Add) prevents double-add |
| T-03-11 | Repudiation | When `BeforeUse` returns Unhealthy due to dead underlying connection, the consumer sees a Core failure-policy event but no adapter-specific log | accept | Core's existing `pool.health.failures` counter + LoggerMessage 1010 (CheckUnhealthy) suffices for v1; consumer can correlate via `pool.name` tag matching channel pool name |
| T-03-12 | Elevation of Privilege | A malicious `configureFactory` closure (in connection pool) returning a non-`ConnectionFactory` `IConnectionFactory` could bypass `ForceAutomaticRecoveryDisabled` | accept | Documented limitation in Plan 01 — the override only fires for the concrete `ConnectionFactory` type since `AutomaticRecoveryEnabled` is not on `IConnectionFactory` |
</threat_model>

<verification>
- `dotnet build Oragon.ElasticPool.sln -c Release` exits 0 across net8/9/10.
- `dotnet test tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj -c Release` reports 141/141 (Phase 1+2 baseline) passing on every TFM — NO REGRESSION.
- Source greps confirm:
  - `AddElasticPool<IChannel>` appears (delegation to Core).
  - `tracker.TryAcquireSlot(...)` is the gate for connection selection.
  - `pairing.TryGet(ch, ...)` is used in BOTH `BeforeUse` and `Release`.
- PublicApiAnalyzers (RS0016/RS0017) clean.
- IF a Core API gap surfaces (e.g., the Factory delegate cannot capture `pairing`/`tracker` because they leak across pool restart, or the lease returned by Core is not safely captured in CWT due to Core internals): STOP and return PLANNING BLOCKED to the orchestrator. Per phase success criterion #5, the gap must be filed and Core refactored before continuing to Plan 03.
</verification>

<success_criteria>
1. `services.AddElasticChannelPool(name, connectionPoolName, configurePool)` registers a working `IElasticPool<IChannel>`.
2. `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` is the pairing data structure (verified by grep).
3. Default `CreateChannelOptions` has both publisher-confirmation flags ON (Pitfall E mitigation).
4. Eager spread: when a connection has reached `MaxChannelsPerConnection` channels, the Factory acquires a different connection (verified by `TryAcquireSlot`-driven retry loop).
5. Lazy cross-pool invalidation: `BeforeUse` returns Unhealthy when EITHER `IChannel.IsOpen` OR paired `IConnection.IsOpen` is false.
6. Release order: CloseAsync → DisposeAsync (channel) → ReleaseSlot → TryRemove → DisposeAsync (lease).
7. No regression in Phase 1+2 Core tests.
</success_criteria>

<output>
After completion, create `.planning/phases/03-rabbitmq-adapter/03-02-SUMMARY.md` documenting:
- Files created with line counts.
- The eager-spread algorithm summary (what `TryAcquireSlot` does, retry-loop bound).
- The lazy invalidation summary (where `BeforeUse` re-probes).
- Whether any Core API gap surfaced during implementation (per phase success criterion #5 — STOP and report if yes).
- Heads-up to Plan 03: integration tests need to specifically validate (a) `MaxChannelsPerConnection` spread under broker `channel_max=10`, and (b) lazy invalidation latency when a connection dies under live channels (this is the Q1 "ship lazy, validate empirically" mandate).
</output>
