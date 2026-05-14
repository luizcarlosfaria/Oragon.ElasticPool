# Stack Research — Oragon.ElasticPool

**Domain:** Multi-target .NET OSS NuGet library (in-process pooling primitive + RabbitMQ adapter)
**Researched:** 2026-05-03
**Confidence:** HIGH (versions verified against current NuGet/official docs as of May 2026)

## Executive Summary

The 2026 stack for an OSS .NET pooling library is highly settled and BCL-centric. The Core
package needs **only two transitive dependencies** (Logging.Abstractions, DependencyInjection.Abstractions),
because `System.Diagnostics.Metrics.Meter`, `System.Diagnostics.ActivitySource`,
`System.Threading.Channels`, `ValueTask`, `IAsyncDisposable`, and `IMeterFactory` are all
in-box on **every** target (`net8.0`/`net9.0`/`net10.0`) — zero polyfills required. The
RabbitMQ adapter pins to `RabbitMQ.Client 7.x` (async-first, `IChannel`/`IConnection`).
Test/CI tooling consolidates around **xUnit v3 + Microsoft.Testing.Platform** (xUnit v2 is
in security-fix-only mode), **NSubstitute** (mock), **Shouldly** (assertions — Fluent
Assertions v8 changed to a $129/yr commercial license and is now disqualified for OSS),
**BenchmarkDotNet 0.15.x**, **MinVer** for tag-driven SemVer, and standard SourceLink +
deterministic builds for source debugging.

## Recommended Stack

### Core Technologies

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| .NET SDK | **10.0.x** (build host) | Build/test host for all 3 TFMs | .NET 10 SDK can target net8.0/net9.0/net10.0; net10 is LTS through Nov 2028 |
| TargetFrameworks | `net10.0;net9.0;net8.0` | Multi-target matrix | Covers current LTS (net8 until Nov 2026, net10 LTS until Nov 2028) and current STS (net9 until May 2026 — keep until EOL just for transition) |
| C# LangVersion | `latest` per TFM | Use modern syntax (collection expressions, primary ctors) | net8+ all support C# 12+; let SDK pick newest per TFM |
| Nullable | `enable` | Reference-type null safety | Mandatory for any new OSS lib in 2026; prevents an entire class of NREs at boundary |
| ImplicitUsings | `enable` | Reduce noise | Standard since net6 |
| `System.Diagnostics.Metrics.Meter` | BCL (in-box) | Counters, gauges, histograms for pool size/borrow/grow/shrink | Universal OTel-friendly metrics surface; consumes via `IMeterFactory` for testability |
| `System.Diagnostics.ActivitySource` | BCL (in-box) | Tracing on Acquire/Release/HealthCheck | OTel reads ActivitySource directly — no extra package needed |
| `System.Threading.Channels` | BCL (in-box) | Bounded waiter queue for `AcquireAsync` under pressure | Lock-free, async-aware producer/consumer; ideal for waiter queue with cancellation |
| `IAsyncDisposable` / `ValueTask` | BCL (in-box) | Async disposal of `IPoolItem<T>`, allocation-free hot paths | Both are in-box on net8+; no `System.Threading.Tasks.Extensions` needed |

### Core Package Dependencies (Oragon.ElasticPool)

**Keep this list short. Every package added here becomes a transitive burden on every consumer.**

| Package | Version | Purpose | Why minimal-cost |
|---------|---------|---------|------------------|
| `Microsoft.Extensions.Logging.Abstractions` | **10.0.x** (latest 10.0.5+) | `ILogger<T>` for state-transition logs | Only pulls `M.E.DependencyInjection.Abstractions`; no implementations |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | **10.0.x** (latest 10.0.6+) | `IServiceCollection` extension `AddElasticPool<T>(...)` | Pure interfaces; no container implementation pulled in |
| `Microsoft.SourceLink.GitHub` | **8.0.0** | Embed source-link metadata at build time | `PrivateAssets="all"` — does NOT propagate to consumers |
| `MinVer` | **6.0.0** | Tag-driven SemVer 2.0 versioning at build time | `PrivateAssets="all"` — build-only, zero runtime cost |

**Floating versions strategy:** Pin to `10.0.*` (or use a version range like `[10.0.0,)` resolved at restore). The `M.E.*` packages follow .NET version cadence — staying on 10.x is the universal lowest-common-denominator since .NET 10 SDK can resolve them for net8/net9/net10 targets. Avoid pinning to a specific patch (`10.0.5`) — that forces consumers into version battles.

> **Critical:** Do NOT add `Microsoft.Extensions.Logging`, `Microsoft.Extensions.DependencyInjection` (the implementation), `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, or `Microsoft.Extensions.Configuration` to Core. These are application-layer concerns.

### RabbitMQ Adapter Dependencies (Oragon.ElasticPool.RabbitMQ)

| Package | Version | Purpose | Why |
|---------|---------|---------|-----|
| `Oragon.ElasticPool` | (matching) | Project reference / NuGet | The pool primitive |
| `RabbitMQ.Client` | **7.2.1** (or `[7.0.0,8.0.0)`) | `IConnection`/`IChannel` to be pooled | v7.x is async-first; `IModel` was renamed `IChannel`; `BasicProperties` is now a value type you `new` — old samples will mislead |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.x | `services.AddElasticConnectionPool(...)` extensions | Same minimalist DI surface as Core |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.x | Adapter-level logging | Same as Core |

**RabbitMQ.Client v7 specifics that affect adapter design:**
1. `IModel` → **`IChannel`** (rename throughout); old code samples are wrong.
2. **All public methods are async-first** with `Async` suffix (`CreateChannelAsync`, `BasicPublishAsync`, `CloseAsync`).
3. **Channel options moved**: `ConfirmSelect()` is gone — set `CreateChannelOptions.PublisherConfirmationsEnabled = true` when calling `CreateChannelAsync(options)`.
4. **BasicProperties**: instantiate directly (`new BasicProperties { ... }`); `CreateBasicProperties()` is removed.
5. **Health probe**: use `IConnection.IsOpen` and `IChannel.IsOpen` (sync property); `CloseReason` for diagnostic context.
6. **Memory ownership**: `ReadOnlyMemory<byte>` body in delivered messages is library-owned — irrelevant to publisher pooling but worth noting in samples.
7. **Targets**: RabbitMQ.Client 7.x supports .NET Standard 2.0 + .NET 4.6.2+, so all our TFMs work.

### Test Project Dependencies

| Package | Version | Purpose | Why |
|---------|---------|---------|-----|
| `Microsoft.NET.Test.Sdk` | **17.x** (latest 17.12+) | Test host SDK | Required even with MTP for tooling integration |
| `xunit.v3` | **3.2.2+** | Test framework | v3 is GA since July 2025; v2 is security-fix-only mode |
| `xunit.v3.runner.visualstudio` | matching `xunit.v3` | VS Test Explorer runner | For local debugging in IDEs |
| `xunit.v3.runner.console` (optional) | matching | CLI runner | xUnit v3 test projects are also self-executable — runner is optional |
| `Microsoft.Testing.Platform` | bundled | New cross-framework runner | Opt-in via `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`; native in .NET 10 SDK |
| `Shouldly` | **4.3+** | Fluent assertions (BSD-3-Clause, free) | Drop-in replacement strategy for FluentAssertions v8 license trap |
| `NSubstitute` | **5.3+** | Mocking | Lightweight, idiomatic substitution-based API; preferred over Moq for new projects |
| `Microsoft.Extensions.Logging.Console` | 10.0.x | Log-to-console in tests for diagnostics | Test-only; never in Core/Adapter |
| `Microsoft.Extensions.DependencyInjection` | 10.0.x | Real `ServiceCollection` for integration tests | Test-only |
| `Testcontainers.RabbitMq` | **4.x** (latest 4.6+) | Spin up real RabbitMQ in adapter integration tests | Standard for integration testing infrastructure deps |
| `coverlet.collector` | **6.0.x** | Code coverage in CI | Plays nicely with `dotnet test --collect:"XPlat Code Coverage"` |

### Benchmark Project Dependencies

| Package | Version | Purpose | Why |
|---------|---------|---------|-----|
| `BenchmarkDotNet` | **0.15.8+** | Microbenchmarks | 0.15+ adds .NET 10 support, WakeLock; industry standard, .NET Foundation project |
| `BenchmarkDotNet.Diagnostics.Windows` (optional) | 0.15.8+ | ETW profiling | Only if Windows-specific perf data needed |

Benchmark project should target only `net10.0` (or all three if comparing perf across runtimes — useful to demonstrate stability).

### Development Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| `dotnet-format` (in-box since .NET 6) | Style enforcement | Run via `dotnet format --verify-no-changes` in CI |
| `dotnet-validate` (validate-NuGet-package) | Package linting | Use `Meziantou.Analyzer` + `dotnet validate package local` post-pack |
| `Meziantou.Analyzer` (optional, dev-only) | Static analysis (perf, async, threading) | Opinionated; great for libs that promise thread-safety |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | Track public API surface explicitly | Prevents accidental ABI breaks; `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` files |
| `Roslynator.Analyzers` (optional) | Code quality | Lighter than Meziantou; pick one |
| `editorconfig` | Style consistency across IDEs | Required artifact |

### CI / Packaging Stack

| Tool | Purpose | Notes |
|------|---------|-------|
| **GitHub Actions** | CI/CD | `actions/checkout@v4`, `actions/setup-dotnet@v4` with multi-version install of `8.0.x`, `9.0.x`, `10.0.x` |
| Matrix strategy | Per-TFM testing | `strategy.matrix.tfm: [net8.0, net9.0, net10.0]` then `dotnet test -f ${{ matrix.tfm }}` |
| **MinVer** | Tag-driven SemVer | Push tag `v1.0.0` → packages built as `1.0.0`; commits between tags get `1.0.1-alpha.0.5+abc1234` |
| **NuGet.org** | Package publishing | `dotnet nuget push *.nupkg --api-key $NUGET_API_KEY` after CI green |
| **GitHub Releases** | Release notes | Auto-generated from PR titles or via `release-drafter` |
| **Dependabot** | Dependency updates | `.github/dependabot.yml` for nuget + actions |

### Source Link / Deterministic Build Configuration

In `Directory.Build.props` (root) — applies to all projects:

```xml
<PropertyGroup>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
  <EmbedUntrackedSources>true</EmbedUntrackedSources>
  <IncludeSymbols>true</IncludeSymbols>
  <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  <Deterministic>true</Deterministic>
  <DeterministicSourcePaths Condition="'$(CI)' == 'true'">true</DeterministicSourcePaths>
</PropertyGroup>
```

Rationale: `ContinuousIntegrationBuild=true` normalizes file paths in CI (different per-machine paths break source-link). `EmbedUntrackedSources=true` embeds generated sources (e.g., from source generators) so debuggers can step into them. `snupkg` is the modern symbol format accepted by NuGet.org.

## Installation (Project Setup)

```xml
<!-- Directory.Packages.props (root) -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.6" />
    <PackageVersion Include="RabbitMQ.Client" Version="7.2.1" />
    <PackageVersion Include="MinVer" Version="6.0.0" />
    <PackageVersion Include="Microsoft.SourceLink.GitHub" Version="8.0.0" />

    <!-- Test -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.v3.runner.visualstudio" Version="3.2.2" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
    <PackageVersion Include="Testcontainers.RabbitMq" Version="4.6.0" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Console" Version="10.0.5" />

    <!-- Benchmarks -->
    <PackageVersion Include="BenchmarkDotNet" Version="0.15.8" />
  </ItemGroup>
</Project>
```

```xml
<!-- src/Oragon.ElasticPool/Oragon.ElasticPool.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>true</IsPackable>
    <PackageId>Oragon.ElasticPool</PackageId>
    <Description>Generic, elastic in-process object pool for .NET with health auto-healing and built-in observability.</Description>
    <PackageTags>pool;objectpool;adaptive;elastic;async;observability;opentelemetry</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="MinVer" PrivateAssets="all" />
    <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

## Multi-Target API Compatibility Matrix

| API | net8.0 | net9.0 | net10.0 | Needs `#if`? |
|-----|--------|--------|---------|--------------|
| `IMeterFactory` | yes | yes | yes | no |
| `Meter` / `Counter<T>` / `Histogram<T>` / `ObservableGauge<T>` | yes | yes | yes | no |
| `ActivitySource` / `Activity` | yes | yes | yes | no |
| `ILogger<T>` (via Abstractions) | yes | yes | yes | no |
| `LoggerMessageAttribute` (source-gen logging) | yes | yes | yes | no |
| `ValueTask` / `IAsyncDisposable` | yes | yes | yes | no |
| `System.Threading.Channels` | yes | yes | yes | no |
| `CancellationToken.CreateLinkedTokenSource` | yes | yes | yes | no |
| `Lock` (System.Threading.Lock — net9 monitor wrapper) | NO | yes | yes | **YES** — `#if NET9_0_OR_GREATER` (use `object` lock target on net8) |
| `TimeProvider` | yes (in-box net8+) | yes | yes | no |
| `PriorityQueue<TElement,TPriority>.Remove` | yes | yes | yes | no |
| Collection expressions (`[..items]`) | yes (C# 12) | yes | yes | no — language feature, not BCL |
| `ConfigureAwaitOptions` | yes | yes | yes | no |
| `Random.Shared` | yes | yes | yes | no |

**Verdict: zero polyfills required.** Only one likely `#if` guard: if you adopt `System.Threading.Lock` (net9+) for slightly nicer monitor syntax, guard it. Everything else is uniform across the matrix.

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|-------------------------|
| **xUnit v3** | NUnit 4.x | Team already on NUnit; data-driven tests are heavy and you want `[TestCaseSource]` ergonomics |
| **xUnit v3** | MSTest v3 | Required by enterprise tooling (Azure DevOps test categories); MTP-native since .NET 10 |
| **Shouldly** | Awesome Assertions | You really want FluentAssertions API and the BSD-3 of Shouldly bothers your team — but Shouldly is more actively maintained |
| **Shouldly** | Built-in `Assert.*` | You want zero assertion deps; less ergonomic but fully sufficient for a library this size |
| **NSubstitute** | Moq 4.18 | Existing team familiarity; note Moq 4.20+ added a controversial telemetry feature (SponsorLink) — if you accept Moq, pin `<4.20.0` |
| **MinVer** | Nerdbank.GitVersioning | You need version stamping without git tags (e.g., branch-based versioning) and accept a `version.json` config file |
| **MinVer** | GitVersion | You want GitFlow-aware versioning with config — overkill for a library; MinVer is the lightweight default |
| **GitHub Actions** | Azure Pipelines | Repo lives in Azure DevOps; otherwise GHA is OSS standard |
| **Central Package Mgmt** | Per-project versions | You only have 2 projects and don't see future growth — but CPM costs nothing to adopt early |
| **Testcontainers** | Local RabbitMQ install / docker-compose | CI only has docker-compose primitive; Testcontainers is just a wrapper, no friction |
| **`System.Threading.Channels` waiter queue** | `SemaphoreSlim` + `ConcurrentQueue` | You want absolute minimum dependencies and trust your hand-rolled coordination — Channels is in-box anyway, take the win |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| **FluentAssertions v8+** | License changed Jan 2025 to Xceed Community License; commercial use $129.95/dev/year. OSS contributors and downstream commercial consumers can't use it freely. | **Shouldly** (BSD-3, free, actively maintained) or **Awesome Assertions** (Apache-2 fork of FA v7) |
| **FluentAssertions v7.x (pinned)** | Still Apache-2 but only critical fixes; no future evolution. Pinning works for legacy projects but not greenfield. | **Shouldly** |
| **Moq 4.20+** | SponsorLink controversy — sent emails to dev addresses on build; community trust damaged. | **NSubstitute** (cleanest API) or **Moq <4.20** if you must |
| **`Microsoft.Extensions.Logging`** in Core | Pulls in DI implementation, Options, DiagnosticSource — explodes transitive surface | `Microsoft.Extensions.Logging.Abstractions` only |
| **`Microsoft.Extensions.DependencyInjection`** (impl) in Core | Same — drags in the container; libraries should code to abstractions | `Microsoft.Extensions.DependencyInjection.Abstractions` only |
| **`Microsoft.Extensions.Hosting`** in Core | Application-layer concern (lifetime, IHostedService); a pool primitive doesn't own a host | If you want sweep timer wired to host lifetime, document the pattern in README; let consumer wire it |
| **`Microsoft.Extensions.ObjectPool`** as a base | This is exactly what you're replacing — it's fixed-size with no health hooks; building on it is wrong abstraction | Build directly on BCL primitives (`Channel<T>`, `ConcurrentBag<T>`, `Lock`) |
| **`Polly`** in Core | Polly is great but it's a resilience library, not a pool — pulling it in conflates concerns and adds 100KB+ of dependency surface | Document how consumers can compose Polly retries around `AcquireAsync` in samples |
| **`Polyfill` (SimonCropp)** | All targets are net8+; nothing to polyfill. Source-only package adds repo noise for zero gain. | Just rely on BCL |
| **`xunit` (v2)** | Security fix mode only since v3 GA in July 2025; new attribute systems and `Assert.*` improvements only in v3 | `xunit.v3` |
| **`Microsoft.NET.Test.Sdk` < 17.10** | Older versions don't fully integrate with MTP runner | 17.12+ |
| **`Newtonsoft.Json`** anywhere | Even if needed for adapter telemetry, prefer `System.Text.Json` (in-box, faster, no transitive cost) | `System.Text.Json` (in-box on all TFMs) |
| **`AutoFixture`** (heavy use) | Magic test data; OK lightly but for a pool library, deterministic test inputs are worth more than terse fixtures | Hand-rolled test builders |
| **`net6.0` / `net7.0` / `netstandard2.0` targeting** | Both EOL; `netstandard2.0` would force polyfills for `IAsyncDisposable`, `Channel<T>`, `ActivitySource` | Drop them; require net8+ |
| **`AssemblyVersion` hand-edits** | Drift between `AssemblyVersion`, `FileVersion`, `PackageVersion` is a recurring bug | MinVer manages all three from one git tag |

## Stack Patterns by Variant

**If consumer is OTel-instrumented:**
- Library is automatically picked up — `Meter` name `"Oragon.ElasticPool"` and ActivitySource name `"Oragon.ElasticPool"` are conventions; consumer adds `.AddMeter("Oragon.ElasticPool")` / `.AddSource("Oragon.ElasticPool")` to their OTel pipeline.

**If consumer is using Aspire (.NET Aspire dashboard):**
- Standard OTel exposure works out of the box. No special handling needed beyond standard names.

**If consumer is on net8.0 only:**
- Same package works; we ship binaries for net8.0/net9.0/net10.0; NuGet picks the best match.

**If consumer is on net48 / netstandard2.0:**
- **Not supported.** Document this as a non-goal in README. Backporting requires polyfills for `IAsyncDisposable`, `Channel<T>`, `ActivitySource`, `IMeterFactory` — large attack surface for a tiny user population.

**If RabbitMQ.Client v8 ships:**
- Treat as a separate adapter package version (e.g., `Oragon.ElasticPool.RabbitMQ` 2.x for v8). Don't try to multi-target the client. v7 → v8 is unlikely to be drastic given v7 just stabilized.

## Version Compatibility

| Package A | Compatible With | Notes |
|-----------|-----------------|-------|
| `Microsoft.Extensions.*@10.0.x` | net8.0, net9.0, net10.0 | Microsoft Extensions 10.x targets net8/net9/net10/netstandard2.0 — always works on our matrix |
| `RabbitMQ.Client@7.2.1` | net8.0+, net4.6.2+, netstandard2.0 | Works on all our TFMs |
| `xunit.v3@3.2.2` | net8.0+, net4.7.2+ | Test project must target net8+ — fine since our tests target the same matrix as the library |
| `BenchmarkDotNet@0.15.8` | net8.0+ | net10 added in 0.15.0 |
| `Testcontainers.RabbitMq@4.x` | net8.0+ | Standard since v4 |
| `MinVer@6.0.0` | Any SDK; build-time only | Reads tags via libgit2 — works in any CI |
| `Microsoft.SourceLink.GitHub@8.0.0` | Any SDK | Works with both `dotnet pack` and msbuild |

## Repository Structure (recommendation)

```
/
  Directory.Build.props          # SourceLink, deterministic, common metadata
  Directory.Packages.props        # Central package versions
  global.json                     # Pin SDK roll-forward (latestFeature on 10.0)
  .editorconfig
  .github/
    workflows/ci.yml              # Matrix: ubuntu+windows × net8/net9/net10
    workflows/release.yml         # Triggered on tag push; calls dotnet pack + push
    dependabot.yml
  src/
    Oragon.ElasticPool/
    Oragon.ElasticPool.RabbitMQ/
  tests/
    Oragon.ElasticPool.Tests/
    Oragon.ElasticPool.IntegrationTests/   # stress, concurrency
    Oragon.ElasticPool.RabbitMQ.Tests/
    Oragon.ElasticPool.RabbitMQ.IntegrationTests/  # uses Testcontainers
  bench/
    Oragon.ElasticPool.Benchmarks/
  samples/
    PublisherSample/              # the motivating "few/hour to 100k simultaneous" demo
  README.md
  LICENSE                         # MIT
  CHANGELOG.md
```

## Sources

- [NuGet: RabbitMQ.Client 7.2.1](https://www.nuget.org/packages/rabbitmq.client/) — version verified, target frameworks confirmed (HIGH)
- [RabbitMQ v7 Migration Guide (GitHub)](https://github.com/rabbitmq/rabbitmq-dotnet-client/blob/main/v7-MIGRATION.md) — IModel→IChannel, async API, BasicProperties (HIGH)
- [.NET 10 Announcement (devblogs)](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/) — LTS through Nov 2028 (HIGH)
- [.NET 10 release notes (dotnet/core)](https://github.com/dotnet/core/blob/main/release-notes/10.0/README.md) — official release info (HIGH)
- [NuGet: Microsoft.Extensions.Logging.Abstractions 10.0.5](https://www.nuget.org/packages/microsoft.extensions.logging.abstractions/) — minimal transitive deps confirmed (HIGH)
- [NuGet: Microsoft.Extensions.DependencyInjection.Abstractions 10.0.6](https://www.nuget.org/packages/microsoft.extensions.dependencyinjection.abstractions/) (HIGH)
- [NuGet: xunit.v3 3.2.2](https://www.nuget.org/packages/xunit.v3) — GA status confirmed (HIGH)
- [xUnit v3 Migration Guide](https://xunit.net/docs/getting-started/v3/migration) — v2 in security-fix mode (HIGH)
- [xUnit + MTP doc](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform) — `UseMicrosoftTestingPlatformRunner` opt-in, native in .NET 10 SDK (HIGH)
- [NuGet: BenchmarkDotNet 0.15.8](https://www.nuget.org/packages/benchmarkdotnet/) — .NET 10 support added in 0.15.0 (HIGH)
- [NuGet: MinVer 6.0.0](https://www.nuget.org/packages/minver) — current major (HIGH)
- [Producing Packages with Source Link (devblogs)](https://devblogs.microsoft.com/dotnet/producing-packages-with-source-link/) — `ContinuousIntegrationBuild`, `PublishRepositoryUrl` (HIGH)
- [NuGet symbol packages snupkg (Microsoft Learn)](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg) — modern symbol format (HIGH)
- [Central Package Management (Microsoft Learn)](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management) — `ManagePackageVersionsCentrally`, transitive pinning (HIGH)
- [High-performance logging (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/high-performance-logging) — `[LoggerMessage]` source generator (HIGH)
- [.NET Observability with OpenTelemetry (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel) — `IMeterFactory`, `ActivitySource` are BCL-native (HIGH)
- [Fluent Assertions v8 license change (InfoQ)](https://www.infoq.com/news/2025/01/fluent-assertions-v8-license/) — disqualifies for OSS (HIGH)
- [Shouldly NuGet](https://www.nuget.org/packages/Shouldly) — BSD-3, alternative (HIGH)
- [actions/setup-dotnet](https://github.com/actions/setup-dotnet) — multi-version SDK install pattern (HIGH)
- [Polyfill (SimonCropp)](https://github.com/SimonCropp/Polyfill) — checked; not needed for our matrix (HIGH — confirmed unnecessary)

---
*Stack research for: .NET OSS pooling library + RabbitMQ adapter*
*Researched: 2026-05-03*
