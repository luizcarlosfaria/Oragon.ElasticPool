# Phase 4 Deferred Items

## README Quickstart Drift (discovered Plan 04-02 Task 4)

**Issue:** Both `src/Oragon.AdaptivePool.Core/README.md` and `README.md` (root) quickstarts
use property-setter syntax (`pool.MinSize = 1; pool.InitialSize = 2; pool.MaxSize = 16;`)
that doesn't exist on `AdaptivePoolBuilder<T>`. The actual public API (per the now-frozen
`PublicAPI.Shipped.txt`) requires the fluent method `pool.WithBounds(minSize, maxSize, initialSize)`.

Same drift for `pool.IdleTimeout = TimeSpan.FromMinutes(2);` -> should be
`pool.IdleTimeout(TimeSpan.FromMinutes(2))` (method call, not property).

**Empirically validated:** A consumer following the README quickstart verbatim hits
`CS1061 'AdaptivePoolBuilder<MyClient>' does not contain a definition for 'MinSize'`.
The corrected code (`WithBounds(...)` + method-call `IdleTimeout(...)`) compiles and runs.

**Why deferred:** Out of scope for Plan 04-02 (which freezes the API surface and adds
release machinery). The README is also missing a `services.AddOptions()` call AND uses
`GetRequiredService<IAdaptivePool<T>>()` instead of the actual `GetRequiredKeyedService`
because `AddAdaptivePool` registers a keyed singleton.

**Action item for maintainer (PRE-RELEASE):** Before pushing `v1.0.0-rc.1`, fix the README
quickstarts in:
- `README.md` (root)
- `src/Oragon.AdaptivePool.Core/README.md`
- (verify) `src/Oragon.AdaptivePool.RabbitMQ/README.md`

Replace the property-setter snippets with the validated working pattern from
`/tmp/consumer-smoke/PoolDemo/Program.cs` (Plan 04-02 Task 4).

**Risk:** This is a doc bug, not a code bug. The frozen PublicAPI surface is correct;
consumers just need a corrected README to copy-paste from. Failing to fix this before
publish means every evaluator following the README will hit a compile error in the first
60 seconds — directly contradicting OSS-02 ("README converts evaluators in 60s").
