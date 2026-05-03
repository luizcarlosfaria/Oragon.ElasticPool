---
phase: 04-polish-v1-release
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Directory.Build.props
  - src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj
  - src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj
  - src/Oragon.AdaptivePool.Core/README.md
  - src/Oragon.AdaptivePool.RabbitMQ/README.md
  - README.md
  - LICENSE
  - CHANGELOG.md
  - icon.png
  - .editorconfig
autonomous: true
requirements: [OSS-02, OSS-03, OSS-04]
tags: [oss, packaging, docs, readme, license, changelog, nuget-metadata, sourcelink]

must_haves:
  truths:
    - "Both packable csproj files (Core, RabbitMQ) reference an embedded per-package README via PackageReadmeFile"
    - "Both packable csproj files reference an embedded icon via PackageIcon (icon.png at repo root, packed into the .nupkg)"
    - "Repo root contains LICENSE (MIT, full text) — license is referenced by Directory.Build.props or csproj as PackageLicenseFile OR PackageLicenseExpression=MIT (decision D-04: PackageLicenseExpression=MIT preserved, plus LICENSE file at root for GitHub UI recognition)"
    - "Repo root contains CHANGELOG.md in Keep-a-Changelog format with v1.0.0 entry listing the 30 v1 requirements"
    - "Repo root contains README.md (orchestrator overview) linking to per-package READMEs and sample"
    - "src/Oragon.AdaptivePool.Core/README.md contains 30-second quickstart + OTel exporter integration example wiring Meter+ActivitySource + comparison table vs Microsoft.Extensions.ObjectPool"
    - "src/Oragon.AdaptivePool.RabbitMQ/README.md contains layered IConnection+IChannel pool example, AutomaticRecoveryEnabled override callout, link to BurstyPublisher sample, and the connection-pool-sizing callout from Phase 3 SUMMARY heads-up #1"
    - "Per-package csproj has explicit Authors, Description, PackageTags refined for evaluator search, RepositoryUrl, and PackageProjectUrl"
    - "icon.png exists at repo root as 128×128 PNG"
    - "dotnet pack -c Release for both packable projects produces .nupkg containing README.md and icon.png in the package contents"
  artifacts:
    - path: "Directory.Build.props"
      provides: "Repo-wide package metadata defaults: PackageProjectUrl, PackageReadmeFile=README.md (relative to project), PackageIcon=icon.png, PackageRequireLicenseAcceptance=false, embedded README+icon include items"
      contains: "PackageReadmeFile"
    - path: "src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj"
      provides: "Refined Core package metadata + per-project README pack item"
      contains: "PackageReadmeFile"
    - path: "src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj"
      provides: "Refined RabbitMQ package metadata + per-project README pack item"
      contains: "PackageReadmeFile"
    - path: "src/Oragon.AdaptivePool.Core/README.md"
      provides: "Per-package README embedded in Core .nupkg — the converter-in-60-seconds README"
      min_lines: 120
    - path: "src/Oragon.AdaptivePool.RabbitMQ/README.md"
      provides: "Per-package README embedded in RabbitMQ .nupkg"
      min_lines: 80
    - path: "README.md"
      provides: "Root README — orchestrator overview linking both packages + sample, badges, comparison table, OTel example"
      min_lines: 150
    - path: "LICENSE"
      provides: "MIT license full text"
      contains: "MIT License"
    - path: "CHANGELOG.md"
      provides: "Keep-a-Changelog v1.0.0 entry"
      contains: "## [1.0.0]"
    - path: "icon.png"
      provides: "Package icon, 128×128 PNG, embedded in both .nupkg files"
  key_links:
    - from: "src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj"
      to: "src/Oragon.AdaptivePool.Core/README.md"
      via: "<None Include=\"README.md\" Pack=\"true\" PackagePath=\"\\\" />"
      pattern: "PackageReadmeFile"
    - from: "src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj"
      to: "src/Oragon.AdaptivePool.RabbitMQ/README.md"
      via: "<None Include=\"README.md\" Pack=\"true\" PackagePath=\"\\\" />"
      pattern: "PackageReadmeFile"
    - from: "Directory.Build.props"
      to: "icon.png"
      via: "<None Include=\"$(MSBuildThisFileDirectory)icon.png\" Pack=\"true\" PackagePath=\"\\\" Visible=\"false\" Condition=\"'$(IsPackable)' == 'true'\" />"
      pattern: "PackageIcon"
    - from: "README.md (root)"
      to: "samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md"
      via: "Markdown relative link"
      pattern: "samples/.*BurstyPublisher"
---

<objective>
Land all OSS-quality documentation, licensing, and NuGet packaging metadata required to ship `Oragon.AdaptivePool.Core` and `Oragon.AdaptivePool.RabbitMQ` to NuGet.org. After this plan, `dotnet pack -c Release` produces well-formed `.nupkg` files containing per-package README, embedded icon, MIT license expression, refined description/tags/authors/project URLs — and the repo presents a polished GitHub-recognizable face (root README, LICENSE, CHANGELOG).

Purpose: Address OSS-02 (README + OTel example + comparison table + sample link), OSS-03 (CHANGELOG and SemVer hygiene infra — actual MinVer wiring already shipped Phase 1), and the consumer-facing half of OSS-04 (.nupkg metadata; the .snupkg + SourceLink halves are Phase 1 carry-forward, this plan only refines metadata so consumers see Authors/Description/Icon/README in NuGet.org and IDE package managers).

Output: Updated build infra + 3 README files + LICENSE + CHANGELOG + icon.png. No code changes. No tests added (these are docs/metadata files; verification is `dotnet pack` content inspection + grep gates).
</objective>

<execution_context>
@/mnt/p/dynamic-pool/.claude/get-shit-done/workflows/execute-plan.md
@/mnt/p/dynamic-pool/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/04-polish-v1-release/04-CONTEXT.md

# Phase 1 — repo scaffolding, build infra, MinVer + SourceLink already wired.
@.planning/phases/01-core-skeleton-fixed-size-pool/01-SUMMARY.md

# Phase 3 — final state of RabbitMQ adapter; SUMMARY heads-up #1 (connection-pool sizing
# callout) MUST appear in the RabbitMQ README per goal-backward truth.
@.planning/phases/03-rabbitmq-adapter/03-SUMMARY.md

# Sample README (RabbitMQ adapter) — link target from package READMEs.
@samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md

# Existing build infra (will be modified, not replaced).
@Directory.Build.props
@Directory.Packages.props
@src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj
@src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj

<interfaces>
<!-- Public API surface that README quickstarts must match. Extracted from PublicAPI.Unshipped.txt. -->
<!-- Use these exact symbol names + signatures in code samples — analyzer will reject README drift. -->

From src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt — Core entry points:

```csharp
// DI extension (the README quickstart's primary entry point)
namespace Oragon.AdaptivePool.Core.DependencyInjection;
public static class AdaptivePoolServiceCollectionExtensions
{
    public static IServiceCollection AddAdaptivePool<T>(
        this IServiceCollection services,
        string name,
        Action<AdaptiveObjectPoolBuilder<T>> configure) where T : class;
}

// Pool consumer surface
namespace Oragon.AdaptivePool.Core.Abstractions;
public interface IAdaptivePool<T>
{
    IPoolItem<T> Acquire();
    ValueTask<IPoolItem<T>> AcquireAsync(CancellationToken cancellationToken = default);
    Task ReadyAsync();
    int MinSize { get; }
    int MaxSize { get; }
    int InUse { get; }
    int Available { get; }
}

public interface IPoolItem<T> : IDisposable, IAsyncDisposable
{
    T Value { get; }
}

// Telemetry names (use these literal strings in OTel example)
// Meter:          "Oragon.AdaptivePool"
// ActivitySource: "Oragon.AdaptivePool"
```

From src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt — RabbitMQ entry points:

```csharp
namespace Oragon.AdaptivePool.RabbitMQ.DependencyInjection;
public static class AdaptiveConnectionPoolServiceCollectionExtensions
{
    public static IServiceCollection AddAdaptiveConnectionPool(
        this IServiceCollection services,
        string name,
        Action<ConnectionFactory>? configureFactory,
        Action<AdaptiveConnectionPoolBuilder> configurePool);
}

public static class AdaptiveChannelPoolServiceCollectionExtensions
{
    public static IServiceCollection AddAdaptiveChannelPool(
        this IServiceCollection services,
        string name,
        string connectionPoolName,
        Action<AdaptiveChannelPoolBuilder> configurePool);
}
```

If a snippet you write does not compile against the above, fix the snippet (NOT the API).
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Repo-wide OSS files (LICENSE, CHANGELOG, icon, root README) + Directory.Build.props metadata defaults</name>
  <files>
    LICENSE,
    CHANGELOG.md,
    icon.png,
    README.md,
    Directory.Build.props
  </files>
  <action>
Create four repo-root OSS files and refine `Directory.Build.props` so per-package metadata has sensible defaults that the per-csproj task only has to override when needed.

**1. LICENSE** (repo root) — full MIT license text, copyright year 2026, holder "Luiz Carlos Faria (luizcarlosfaria@gmail.com) and contributors". Use the canonical MIT text from https://opensource.org/licenses/MIT verbatim. Per CONTEXT.md decision: `PackageLicenseExpression=MIT` (already in both csproj from Phase 1) is the NuGet machine-readable signal; LICENSE at root is the GitHub-UI signal. Both must agree.

**2. CHANGELOG.md** (repo root) — Keep-a-Changelog 1.1.0 format (https://keepachangelog.com/en/1.1.0/). Structure:

```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-05-XX

### Added
- **Core (`Oragon.AdaptivePool.Core`)** — generic in-process object pool with:
  - `IAdaptivePool<T>` with sync `Acquire()` + `AcquireAsync(CancellationToken)` (API-01)
  - `IPoolItem<T>` disposable wrapper with double-dispose detection (API-02)
  - Fluent builder `AdaptiveObjectPoolFactory.Build<T>(...)` (API-03)
  - 5 lifecycle hooks: `Factory`, `BeforeUse`, `Check`, `AfterUse`, `Release` (HOOK-01..05)
  - Configurable `MinSize`/`MaxSize`/`InitialSize` with eager warm-up (BOUND-01, BOUND-02)
  - Composite-signal grow (waiters + utilization + p95 wait) and hysteretic shrink (ELASTIC-01, ELASTIC-02)
  - Pluggable `IItemFailurePolicy<T>` with `DiscardAndReplace` default (FAIL-01, FAIL-02)
  - Built-in `Meter` + `ActivitySource` (both named `"Oragon.AdaptivePool"`) (TELEM-01, TELEM-02)
  - Source-generated `[LoggerMessage]` logging (TELEM-03)
  - DI extension `AddAdaptivePool<T>(name, configure)` (DI-01)
  - End-to-end `CancellationToken` propagation (QUAL-01)
  - `IAsyncDisposable` with drain semantics (QUAL-02)
  - Stress-validated thread safety (QUAL-03)

- **RabbitMQ adapter (`Oragon.AdaptivePool.RabbitMQ`)** — for RabbitMQ.Client v7+:
  - `AddAdaptiveConnectionPool(name, configure)` with `IsOpen`-based health (RMQ-01)
  - `AddAdaptiveChannelPool(name, connectionPoolName, configure)` layered over connection pool (RMQ-02)
  - Bursty publisher sample (RMQ-03)
  - Conventions consistent with `Oragon.RabbitMQ` sister library (RMQ-04)

- **Quality / OSS** — multi-TFM (net8/9/10) CI matrix (OSS-01), README + OTel example + comparison table + sample link (OSS-02), MinVer-driven SemVer with `.snupkg` + SourceLink (OSS-03, OSS-04), `PublicApiAnalyzers` baselined (OSS-05).

[Unreleased]: https://github.com/oragon/Oragon.AdaptivePool/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/oragon/Oragon.AdaptivePool/releases/tag/v1.0.0
```

Use today's placeholder date `2026-05-XX` — Plan 02 (or release-time) replaces XX with the real day.

**3. icon.png** (repo root) — 128×128 PNG placeholder. Generate via ImageMagick (typically pre-installed on Ubuntu CI / WSL):

```bash
# From repo root
convert -size 128x128 xc:'#1E3A5F' \
  -fill white -gravity center -font DejaVu-Sans-Bold -pointsize 60 \
  -annotate +0+0 'AP' \
  -bordercolor '#FFC857' -border 4 \
  icon.png
# Verify: file icon.png  →  PNG image data, 136 x 136, 8-bit/color RGB
# Trim border back to 128×128 if convert added 4px:
mogrify -crop 128x128+0+0 icon.png  || true
file icon.png
identify icon.png   # must report exactly 128x128 (resize if not)
```

If `convert` is not available, fall back to a minimal 128×128 solid-color PNG via Python:

```bash
python3 -c "
from struct import pack
import zlib, sys
W=H=128
raw = b''.join(b'\\x00' + b'\\x1E\\x3A\\x5F'*W for _ in range(H))
def chunk(t,d):
    return pack('>I',len(d))+t+d+pack('>I',zlib.crc32(t+d)&0xffffffff)
png = b'\\x89PNG\\r\\n\\x1a\\n'
png += chunk(b'IHDR', pack('>IIBBBBB',W,H,8,2,0,0,0))
png += chunk(b'IDAT', zlib.compress(raw,9))
png += chunk(b'IEND', b'')
sys.stdout.buffer.write(png)
" > icon.png
identify icon.png 2>/dev/null || file icon.png
```

The icon is intentionally placeholder per CONTEXT.md "Claude's Discretion: criar placeholder simples agora ou shipar v1.0 sem icon?" — answer: ship a placeholder; can be replaced pre-tag without affecting pipeline.

**4. README.md** (repo root) — orchestrator overview. Structure (use exactly these section names + order):

```markdown
# Oragon.AdaptivePool

> Generic, elastic, self-healing object pool for .NET — with built-in OpenTelemetry.

[![build](https://github.com/oragon/Oragon.AdaptivePool/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/oragon/Oragon.AdaptivePool/actions/workflows/build.yml)
[![NuGet Core](https://img.shields.io/nuget/v/Oragon.AdaptivePool.Core.svg?label=Core)](https://www.nuget.org/packages/Oragon.AdaptivePool.Core)
[![NuGet RabbitMQ](https://img.shields.io/nuget/v/Oragon.AdaptivePool.RabbitMQ.svg?label=RabbitMQ)](https://www.nuget.org/packages/Oragon.AdaptivePool.RabbitMQ)
[![Downloads](https://img.shields.io/nuget/dt/Oragon.AdaptivePool.Core.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## What this is

A pool that simultaneously delivers **elasticity** (grows under pressure, shrinks
when idle), **auto-healing** (broken items detected via lifecycle hooks and
replaced), and **fluent DX** (async-first, DI-first, builder pattern). Multi-target
`net10.0` / `net9.0` / `net8.0`. First adapter ships for RabbitMQ.Client v7+.

## Packages

| Package | NuGet | Purpose |
|---------|-------|---------|
| [`Oragon.AdaptivePool.Core`](src/Oragon.AdaptivePool.Core/README.md) | [![nuget](https://img.shields.io/nuget/v/Oragon.AdaptivePool.Core.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.Core) | Generic pool engine, hooks, telemetry, DI |
| [`Oragon.AdaptivePool.RabbitMQ`](src/Oragon.AdaptivePool.RabbitMQ/README.md) | [![nuget](https://img.shields.io/nuget/v/Oragon.AdaptivePool.RabbitMQ.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.RabbitMQ) | `IConnection` + layered `IChannel` pools for RabbitMQ.Client v7+ |

## 30-second quickstart

[same minimal snippet as Core README quickstart — see Task 2; copy-paste from there for consistency]

## Why not `Microsoft.Extensions.ObjectPool`?

[same comparison table as Core README — see Task 2]

## OpenTelemetry in 5 lines

[same OTel snippet as Core README — see Task 2]

## RabbitMQ bursty publisher

See the runnable sample at [`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher`](samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md):

```bash
dotnet run --project samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher
```

## Documentation

- Core API + telemetry: [`src/Oragon.AdaptivePool.Core/README.md`](src/Oragon.AdaptivePool.Core/README.md)
- RabbitMQ adapter (layered IConnection+IChannel): [`src/Oragon.AdaptivePool.RabbitMQ/README.md`](src/Oragon.AdaptivePool.RabbitMQ/README.md)
- Sample bursty publisher: [`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md`](samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/README.md)
- Changelog: [`CHANGELOG.md`](CHANGELOG.md)

## Versioning

[SemVer 2.0](https://semver.org/spec/v2.0.0.html). Breaking changes only on major
version bumps. Public surface enforced via
[`Microsoft.CodeAnalysis.PublicApiAnalyzers`](https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md).

Versions are produced by [MinVer](https://github.com/adamralph/minver) from git
tags: pushing `v1.0.0` produces `1.0.0.nupkg`; commits between tags receive
prerelease versions like `1.0.1-alpha.0.5+abc1234`.

## Contributing

Issues and PRs welcome at https://github.com/oragon/Oragon.AdaptivePool. Run
`dotnet test` before submitting; CI requires green on the
`ubuntu-latest × {net8.0, net9.0, net10.0}` matrix.

## License

[MIT](LICENSE) © 2026 Luiz Carlos Faria and contributors.

## Acknowledgments

Sister library [`Oragon.RabbitMQ`](https://github.com/oragon/Oragon.RabbitMQ)
(consumer side) shares conventions and naming.
```

**5. Directory.Build.props** — additions (preserve all existing PropertyGroup entries):

```xml
<Project>
  <PropertyGroup>
    <!-- existing properties unchanged: Nullable, ImplicitUsings, LangVersion,
         TreatWarningsAsErrors, EnforceCodeStyleInBuild, PublishRepositoryUrl,
         EmbedUntrackedSources, IncludeSymbols, SymbolPackageFormat,
         ContinuousIntegrationBuild, Deterministic, DeterministicSourcePaths,
         Authors, Company, Copyright, RepositoryType, RepositoryUrl —
         keep verbatim. -->

    <!-- Phase 4 additions: package metadata defaults applied repo-wide.
         Per-csproj overrides PackageId/Description/PackageTags. -->
    <PackageProjectUrl>https://github.com/oragon/Oragon.AdaptivePool</PackageProjectUrl>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageIcon>icon.png</PackageIcon>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <Authors>Luiz Carlos Faria</Authors>
    <Copyright>Copyright © 2026 Luiz Carlos Faria and contributors</Copyright>
  </PropertyGroup>

  <!-- Phase 4: pack the repo-root icon into every packable project automatically.
       The per-project README is added per-csproj (different file per package). -->
  <ItemGroup Condition="'$(IsPackable)' == 'true'">
    <None Include="$(MSBuildThisFileDirectory)icon.png"
          Pack="true"
          PackagePath="\"
          Visible="false" />
  </ItemGroup>
</Project>
```

**Decision rationale (per D-04 / CONTEXT.md):**
- `PackageLicenseExpression=MIT` — machine-readable for NuGet.org. Already on per-csproj from Phase 1 (will be removed in favor of repo-wide default in this task to DRY).
- `LICENSE` at repo root — GitHub-UI recognition (the green license badge).
- Both must agree (MIT). The `Authors` value from Phase 1 was `Oragon` — replace with `Luiz Carlos Faria` per CONTEXT.md decision (more accurate for OSS attribution).

**Note:** Per-csproj `PackageLicenseExpression=MIT` was set in Phase 1 and is now redundant once moved to Directory.Build.props. Task 2 strips it from each csproj.

**.editorconfig touch (optional):** Add `[*.md]` block with `trim_trailing_whitespace = false` (Markdown line breaks). Read existing `.editorconfig` first; only append if missing the markdown rule.
  </action>
  <verify>
    <automated>
test -f LICENSE \
  && grep -q "MIT License" LICENSE \
  && test -f CHANGELOG.md \
  && grep -q '^## \[1.0.0\]' CHANGELOG.md \
  && test -f icon.png \
  && file icon.png | grep -q "PNG image" \
  && test -f README.md \
  && grep -q '^# Oragon.AdaptivePool' README.md \
  && grep -q 'samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher' README.md \
  && grep -q 'PackageReadmeFile>README.md' Directory.Build.props \
  && grep -q 'PackageIcon>icon.png' Directory.Build.props \
  && grep -q 'PackageLicenseExpression>MIT' Directory.Build.props \
  && grep -q '<None Include="\$(MSBuildThisFileDirectory)icon.png"' Directory.Build.props \
  && python3 -c "from PIL import Image; im = Image.open('icon.png'); assert im.size == (128,128), im.size" 2>/dev/null \
  || (identify icon.png 2>/dev/null | grep -q '128x128') \
  && echo OK
    </automated>
  </verify>
  <done>
    LICENSE (MIT), CHANGELOG.md (Keep-a-Changelog v1.0.0 entry), icon.png (128×128 PNG),
    README.md (root, with badges + comparison + sample link), Directory.Build.props
    (repo-wide PackageReadmeFile/PackageIcon/PackageLicenseExpression/Authors and
    icon-pack ItemGroup) all created/updated. `dotnet build` from repo root still
    succeeds (no breakage to existing build).
  </done>
</task>

<task type="auto">
  <name>Task 2: Per-package READMEs (Core + RabbitMQ) + per-csproj metadata refinement</name>
  <files>
    src/Oragon.AdaptivePool.Core/README.md,
    src/Oragon.AdaptivePool.RabbitMQ/README.md,
    src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj,
    src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj
  </files>
  <action>
Create the two per-package READMEs that ship inside the `.nupkg` files (visible on NuGet.org and in IDE NuGet package managers), and refine each csproj to:
1. Reference the per-project README via `<None Include="README.md" Pack="true" PackagePath="\" />`.
2. Strip the now-redundant `PackageLicenseExpression` (moved to Directory.Build.props in Task 1).
3. Refine `<Description>` and `<PackageTags>` for evaluator-search SEO without lying about features.

---

**1. `src/Oragon.AdaptivePool.Core/README.md`** — the converter-in-60-seconds artifact. Required structure (use these section anchors verbatim; the goal-backward checker greps them):

```markdown
# Oragon.AdaptivePool.Core

[![NuGet](https://img.shields.io/nuget/v/Oragon.AdaptivePool.Core.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/oragon/Oragon.AdaptivePool/blob/main/LICENSE)

> Generic, elastic, self-healing object pool for .NET 8 / 9 / 10 — with built-in OpenTelemetry.

`Oragon.AdaptivePool.Core` is a generic in-process object pool for expensive-to-create
resources (clients, connections, handlers). It **grows** under sustained pressure,
**shrinks** when idle, and **heals itself** by detecting and replacing broken items
through pluggable lifecycle hooks. Async-first, DI-first, observable.

For RabbitMQ `IConnection` + `IChannel` pooling, install the companion package
[`Oragon.AdaptivePool.RabbitMQ`](https://www.nuget.org/packages/Oragon.AdaptivePool.RabbitMQ).

## Install

```bash
dotnet add package Oragon.AdaptivePool.Core
```

## 30-second quickstart

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddAdaptivePool<MyExpensiveClient>("default", pool =>
{
    pool.MinSize    = 1;
    pool.InitialSize = 2;
    pool.MaxSize    = 16;
    pool.IdleTimeout = TimeSpan.FromMinutes(2);

    pool.Factory((sp, ct) => ValueTask.FromResult(new MyExpensiveClient()));
    pool.BeforeUse((c, ct) => ValueTask.FromResult(c.IsHealthy ? PoolState.Healthy : PoolState.Unhealthy));
    pool.Release  ((c, ct) => { c.Dispose(); return ValueTask.CompletedTask; });
});

using var host = builder.Build();
var pool = host.Services.GetRequiredService<IAdaptivePool<MyExpensiveClient>>();

await using var lease = await pool.AcquireAsync();
lease.Value.DoWork();
// On Dispose, the item returns to the pool — or is discarded if BeforeUse said Unhealthy.

public sealed class MyExpensiveClient : IDisposable
{
    public bool IsHealthy => true;
    public void DoWork() { /* ... */ }
    public void Dispose() { /* ... */ }
}
```

## Why not `Microsoft.Extensions.ObjectPool`?

| Feature                            | `Microsoft.Extensions.ObjectPool` | `Oragon.AdaptivePool.Core` |
|------------------------------------|-----------------------------------|----------------------------|
| `Min` / `Max` bounds               | ❌ (only `MaximumRetained`)        | ✅                          |
| Elastic grow under pressure        | ❌                                 | ✅ (composite signal: waiters + utilization + p95 wait) |
| Auto-shrink when idle              | ❌                                 | ✅ (hysteretic, IdleTimeout-driven) |
| Lifecycle hooks (5 stages)         | ❌                                 | ✅ (`Factory`, `BeforeUse`, `Check`, `AfterUse`, `Release`) |
| Health check on borrow             | ❌                                 | ✅ (`BeforeUse`)            |
| Background health sweep            | ❌                                 | ✅ (`Check` + `PeriodicTimer` + exponential backoff) |
| Pluggable failure policy           | ❌                                 | ✅ (`IItemFailurePolicy<T>`) |
| Async-first API                    | ❌ (`Get()` is sync, blocks)       | ✅ (`AcquireAsync` returns `ValueTask`) |
| Built-in OpenTelemetry             | Limited                           | ✅ (`Meter` + `ActivitySource` + source-gen `ILogger`) |
| Layered pools (e.g., channel→conn) | N/A                               | ✅ (see `Oragon.AdaptivePool.RabbitMQ`) |

`Microsoft.Extensions.ObjectPool` is great for cheap, stateless, allocation-only
pooling (e.g., `StringBuilder`). `Oragon.AdaptivePool.Core` is for expensive,
stateful, lifecycle-sensitive resources where elasticity and health matter.

## Three pillars

1. **Elasticity** — composite-signal grow (waiters + sustained utilization % + p95
   acquire-wait), hysteretic shrink to `MinSize` only after consecutive low-utilization
   sweep windows past a cooldown since the last grow. No thrashing.
2. **Auto-healing** — five lifecycle hooks at every stage: `Factory` (create),
   `BeforeUse` (validate on borrow), `Check` (background sweep), `AfterUse` (validate
   on return; opt-in), `Release` (cleanup/dispose). Pluggable `IItemFailurePolicy<T>`
   decides discard vs. quarantine vs. custom.
3. **Fluent DX** — async-first, DI-first, builder pattern. `services.AddAdaptivePool<T>(name, configure)`.

## OpenTelemetry in 5 lines

`Oragon.AdaptivePool` exposes a `Meter` and `ActivitySource` both named
`"Oragon.AdaptivePool"`. Wire them to any OTel exporter:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services
    .AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Oragon.AdaptivePool").AddConsoleExporter())
    .WithTracing(t => t.AddSource("Oragon.AdaptivePool").AddConsoleExporter());
// In production, swap AddConsoleExporter() for AddOtlpExporter() (Aspire / OTel collector / etc.).
```

The pool emits these metrics out of the box:

| Instrument                    | Type     | Purpose                              |
|-------------------------------|----------|--------------------------------------|
| `pool.size`                   | Gauge    | Total items the pool currently owns  |
| `pool.available`              | Gauge    | Items idle and ready to acquire      |
| `pool.in_use`                 | Gauge    | Items currently leased               |
| `pool.waiting`                | Gauge    | Waiters queued on `AcquireAsync`     |
| `pool.acquire.count`          | Counter  | Total acquires                       |
| `pool.acquire.duration`       | Histogram| Time spent in `AcquireAsync`         |
| `pool.factory.failures`       | Counter  | `Factory` exceptions                 |
| `pool.grow.count`             | Counter  | Times the pool grew                  |
| `pool.shrink.count`           | Counter  | Times the pool shrank                |
| `pool.health.failures`        | Counter  | `BeforeUse` / `Check` Unhealthy returns |

All instruments carry a single `pool.name` tag (the name passed to `AddAdaptivePool`).
Cardinality stays bounded.

ActivitySource spans: `Acquire`, `Release`, `HealthCheck`, `Grow`, `Shrink`. Guarded
by `HasListeners()` so you pay nothing if nobody is listening.

## Lifecycle hooks

```csharp
builder.Services.AddAdaptivePool<MyClient>("default", pool =>
{
    pool.Factory  ((sp, ct) => /* create new T */);                         // required
    pool.BeforeUse((c, ct) => /* return Healthy / Unhealthy */);            // optional
    pool.Check    ((c, ct) => /* background sweep — return Healthy / Unhealthy */); // optional
    pool.AfterUse ((c, ct) => /* validate on return; v1 default no-op */);  // optional
    pool.Release  ((c, ct) => /* dispose / close — runs on eviction */);    // optional
});
```

Returning `PoolState.Unhealthy` from any hook invokes the configured
`IItemFailurePolicy<T>`. The default `DiscardAndReplaceFailurePolicy<T>` discards
the item and replaces it if the pool is below `MinSize`.

## Bursty workload sample

See [`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher`](https://github.com/oragon/Oragon.AdaptivePool/tree/main/samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher)
for an end-to-end demo: a publisher that goes from "few/hour" to "100k simultaneous"
and back to idle, watching the pool grow, shrink, and self-heal — with metrics
visible in any OTel collector (Aspire Dashboard, Grafana, Console exporter).

## Multi-targeting

Targets `net10.0`, `net9.0`, `net8.0`. No exclusive .NET 10 APIs without
`#if NET10_0_OR_GREATER` guards.

## Versioning & API stability

[SemVer 2.0](https://semver.org/spec/v2.0.0.html). Public surface enforced via
[`Microsoft.CodeAnalysis.PublicApiAnalyzers`](https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md):
breaking changes require an explicit `PublicAPI.Unshipped.txt` update or the build
fails.

## Contributing

https://github.com/oragon/Oragon.AdaptivePool

## License

[MIT](https://github.com/oragon/Oragon.AdaptivePool/blob/main/LICENSE)
```

---

**2. `src/Oragon.AdaptivePool.RabbitMQ/README.md`** — RabbitMQ-focused. Required structure:

```markdown
# Oragon.AdaptivePool.RabbitMQ

[![NuGet](https://img.shields.io/nuget/v/Oragon.AdaptivePool.RabbitMQ.svg)](https://www.nuget.org/packages/Oragon.AdaptivePool.RabbitMQ)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/oragon/Oragon.AdaptivePool/blob/main/LICENSE)

> RabbitMQ.Client v7+ adapter for [`Oragon.AdaptivePool.Core`](https://www.nuget.org/packages/Oragon.AdaptivePool.Core).
> Layered, lifecycle-managed pools for `IConnection` and `IChannel`. Async-first.

## Install

```bash
dotnet add package Oragon.AdaptivePool.RabbitMQ
```

(`Oragon.AdaptivePool.Core` is pulled transitively.)

## What it gives you

- `services.AddAdaptiveConnectionPool(name, configureFactory, configurePool)` — pool of `IConnection` with `IsOpen`-based health checks.
- `services.AddAdaptiveChannelPool(name, connectionPoolName, configurePool)` — pool of `IChannel` **layered** on the connection pool: each channel acquire borrows one connection lease for its lifetime.
- Conventions consistent with [`Oragon.RabbitMQ`](https://github.com/oragon/Oragon.RabbitMQ) (sister consumer-side library).

## Quickstart — bursty publisher

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.RabbitMQ.DependencyInjection;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);

// Connection pool
builder.Services.AddAdaptiveConnectionPool(
    name: "default",
    configureFactory: f =>
    {
        f.Uri = new Uri("amqp://guest:guest@localhost:5672/");
        // AutomaticRecoveryEnabled is overridden to FALSE by the pool — see callout below.
    },
    configurePool: pool =>
    {
        pool.MinSize     = 1;
        pool.MaxSize     = 32;
        pool.InitialSize = 2;
        pool.IdleTimeout = TimeSpan.FromMinutes(2);
    });

// Channel pool layered on top
builder.Services.AddAdaptiveChannelPool(
    name: "default",
    connectionPoolName: "default",
    configurePool: pool =>
    {
        pool.MinSize     = 0;
        pool.MaxSize     = 256;
        pool.InitialSize = 0;
        pool.IdleTimeout = TimeSpan.FromSeconds(30);
    });

using var host = builder.Build();
var channels = host.Services.GetRequiredService<IAdaptivePool<IChannel>>();

// Publish under any load shape — pool grows/shrinks/heals automatically.
await using var lease = await channels.AcquireAsync();
var props = new BasicProperties();
await lease.Value.BasicPublishAsync(exchange: "", routingKey: "demo", mandatory: false,
                                    basicProperties: props, body: "hello"u8.ToArray());
```

## ⚠ Sizing the connection pool

Because each in-flight channel acquire holds one `IPoolItem<IConnection>` lease,
**the connection pool's `MaxSize` must be ≥ the peak number of simultaneously
in-flight channel acquires** in your workload — not just `ceil(channels / channel_max)`.

If you see waiters parked indefinitely on `chPool.AcquireAsync`, raise the
**connection pool's** `MaxSize` first.

(See Phase 3 SUMMARY for the empirical analysis behind this rule.)

## ⚠ AutomaticRecoveryEnabled override

`Oragon.AdaptivePool.RabbitMQ` forces `ConnectionFactory.AutomaticRecoveryEnabled = false`
to avoid two recovery loops (RabbitMQ.Client's vs. the pool's). The pool owns the
lifecycle: `BeforeUse` and `Check` hooks consult `IConnection.IsOpen` and
`Release` calls `CloseAsync()`. If you configured `AutomaticRecoveryEnabled = true`
on the factory, you'll see a single warning log on first acquire (EventId 2001):

> `AutomaticRecoveryEnabled was true on the configured ConnectionFactory for pool 'X'; Oragon.AdaptivePool overrides this to false (the pool owns lifecycle).`

This is intentional. To suppress, set `AutomaticRecoveryEnabled = false` yourself.

## Telemetry

Inherits the Core `Meter` and `ActivitySource` (both named `"Oragon.AdaptivePool"`).
The connection and channel pools are independently named (`pool.name` tag) so you
can chart them separately. See [the Core README](https://www.nuget.org/packages/Oragon.AdaptivePool.Core)
for the full instrument inventory.

## Sample: end-to-end bursty publisher

[`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher`](https://github.com/oragon/Oragon.AdaptivePool/tree/main/samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher)
ships a runnable demo: cycles between idle and 100k-simultaneous publish,
demonstrates pool grow/shrink/heal under real load against a Testcontainers
RabbitMQ broker.

```bash
RABBITMQ_URI=amqp://guest:guest@localhost:5672/ \
  dotnet run --project samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher
```

## Compatibility

- `RabbitMQ.Client` 7.x (async-first API; `IChannel` replaces `IModel`).
- Multi-target `net10.0` / `net9.0` / `net8.0`.

## License

[MIT](https://github.com/oragon/Oragon.AdaptivePool/blob/main/LICENSE)
```

---

**3. `src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj`** — modifications:

Strip `<PackageLicenseExpression>MIT</PackageLicenseExpression>` (now in Directory.Build.props).
Refine `<Description>` (current is one line; expand slightly):

```xml
<Description>Generic, elastic, self-healing in-process object pool for .NET 8/9/10. Composite-signal grow under pressure, hysteretic shrink, 5-stage lifecycle hooks (Factory/BeforeUse/Check/AfterUse/Release), pluggable failure policy, built-in OpenTelemetry (Meter + ActivitySource). Async-first, DI-first.</Description>
```

Refine `<PackageTags>` (semicolon-separated, lowercase): `pool;objectpool;adaptive;elastic;self-healing;async;async-first;dependency-injection;opentelemetry;observability;resilience;dotnet`.

Add per-project README pack item:

```xml
<ItemGroup>
  <None Include="README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

Keep all existing PackageReferences and InternalsVisibleTo / AdditionalFiles ItemGroups unchanged.

---

**4. `src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj`** — modifications:

Strip `<PackageLicenseExpression>MIT</PackageLicenseExpression>`.
Refine `<Description>`:

```xml
<Description>RabbitMQ.Client v7+ adapter for Oragon.AdaptivePool — layered IConnection + IChannel pools with IsOpen-based health checks, AutomaticRecoveryEnabled override, and built-in OpenTelemetry. Async-first.</Description>
```

Refine `<PackageTags>`:

```xml
<PackageTags>rabbitmq;rabbitmq-client;pool;objectpool;adaptive;elastic;connection-pool;channel-pool;async-first;opentelemetry;dotnet</PackageTags>
```

Add per-project README pack item:

```xml
<ItemGroup>
  <None Include="README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

Keep all existing PackageReferences, ProjectReferences, AdditionalFiles, and
InternalsVisibleTo ItemGroups unchanged.

**Compilation safety:** the `<None Include="README.md" />` element does NOT participate
in compilation; it's pure pack metadata. No risk of breaking the build.

---

**Required link integrity in READMEs:** Every Markdown link to
`samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher/...` must point at a
real file (it exists from Phase 3). Do not invent `docs/...` paths — none exist
yet, and we are not creating them in this plan.
  </action>
  <verify>
    <automated>
test -f src/Oragon.AdaptivePool.Core/README.md \
  && grep -q '^# Oragon.AdaptivePool.Core' src/Oragon.AdaptivePool.Core/README.md \
  && grep -q 'AddAdaptivePool<' src/Oragon.AdaptivePool.Core/README.md \
  && grep -q 'Why not `Microsoft.Extensions.ObjectPool' src/Oragon.AdaptivePool.Core/README.md \
  && grep -q 'AddMeter("Oragon.AdaptivePool")' src/Oragon.AdaptivePool.Core/README.md \
  && grep -q 'AddSource("Oragon.AdaptivePool")' src/Oragon.AdaptivePool.Core/README.md \
  && grep -q 'samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher' src/Oragon.AdaptivePool.Core/README.md \
  && test -f src/Oragon.AdaptivePool.RabbitMQ/README.md \
  && grep -q '^# Oragon.AdaptivePool.RabbitMQ' src/Oragon.AdaptivePool.RabbitMQ/README.md \
  && grep -q 'AddAdaptiveConnectionPool' src/Oragon.AdaptivePool.RabbitMQ/README.md \
  && grep -q 'AddAdaptiveChannelPool' src/Oragon.AdaptivePool.RabbitMQ/README.md \
  && grep -q 'AutomaticRecoveryEnabled' src/Oragon.AdaptivePool.RabbitMQ/README.md \
  && grep -q 'connection pool.s `MaxSize`' src/Oragon.AdaptivePool.RabbitMQ/README.md \
  && grep -q 'samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher' src/Oragon.AdaptivePool.RabbitMQ/README.md \
  && grep -q '<None Include="README.md" Pack="true"' src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj \
  && grep -q '<None Include="README.md" Pack="true"' src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj \
  && ! grep -q 'PackageLicenseExpression' src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj \
  && ! grep -q 'PackageLicenseExpression' src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj \
  && grep -q 'self-healing' src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj \
  && grep -q 'connection-pool' src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj \
  && dotnet build Oragon.AdaptivePool.sln -c Release --no-restore 2>&1 | tee /tmp/build.log \
  && grep -E '(Build succeeded|Compilação com êxito)' /tmp/build.log \
  && dotnet pack src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj -c Release --no-build -o /tmp/pack-core 2>&1 | tail -20 \
  && ls /tmp/pack-core/Oragon.AdaptivePool.Core.*.nupkg | head -1 | xargs -I{} unzip -l {} | grep -E 'README\.md|icon\.png' \
  && dotnet pack src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj -c Release --no-build -o /tmp/pack-rmq 2>&1 | tail -20 \
  && ls /tmp/pack-rmq/Oragon.AdaptivePool.RabbitMQ.*.nupkg | head -1 | xargs -I{} unzip -l {} | grep -E 'README\.md|icon\.png' \
  && echo OK
    </automated>
  </verify>
  <done>
    Both per-package READMEs exist with required sections (quickstart, comparison
    table for Core, layered example + sizing callout for RabbitMQ, OTel example
    referencing literal Meter/ActivitySource name "Oragon.AdaptivePool", sample
    link). Both csproj files reference per-project README via Pack=true; license
    expression moved to Directory.Build.props. `dotnet build` is green. `dotnet
    pack` produces .nupkg files containing both README.md and icon.png.
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Package consumer ← NuGet.org | Consumers download .nupkg (signed by NuGet.org's signing infrastructure once published). README/icon are part of the package payload — no untrusted user input crosses here. |
| README → external links (badges, GitHub, NuGet.org) | README markdown renders on NuGet.org; NuGet.org sanitizes markdown server-side. |
| Repo maintainers ← contributors (via PRs) | Phase 4 itself doesn't change PR ingestion; out of scope. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-04-01 | Information disclosure | README code samples | mitigate | Code samples use `amqp://guest:guest@localhost:5672/` (well-known dev default) — no real secrets. RABBITMQ_URI environment variable in sample doc is documented but NOT echoed in code. |
| T-04-02 | Spoofing | Package metadata (Authors, Copyright) | mitigate | Authors/Copyright values are written by the maintainer locally; NuGet.org signs packages on publish (handled by `dotnet nuget push` in Plan 02). PackageRequireLicenseAcceptance=false matches MIT semantics. |
| T-04-03 | Tampering | icon.png embedded in package | accept | Icon is a placeholder generated locally; if tampered with locally before pack, it remains 128×128 PNG with no execution semantic. NuGet.org content signing (Plan 02 trigger) detects post-publish tampering. |
| T-04-04 | Information disclosure | LICENSE / CHANGELOG | accept | Both files are public OSS artifacts by design; no PII beyond authorship attribution and contributors' commit history (which is already public via git). |
| T-04-05 | Repudiation | CHANGELOG attribution | accept | Manual changelog maintenance + git history provides the authoritative record. Out of scope for v1.0 to add release-please-style automation (CONTEXT.md deferred). |
</threat_model>

<verification>
## Plan-level acceptance

After both tasks complete:

1. `dotnet build Oragon.AdaptivePool.sln -c Release` — green (no new warnings introduced; the 12 carry-forward SourceLink "no remote" warnings from Phase 3 are pre-existing and accepted).
2. `dotnet pack src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj -c Release` produces `Oragon.AdaptivePool.Core.0.0.0-alpha.X+xxx.nupkg` (MinVer auto-version pre-tag) containing:
   - `README.md` (the per-package one, NOT the root)
   - `icon.png`
   - assembly DLLs for net8.0/net9.0/net10.0
3. `dotnet pack src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj -c Release` ditto for RabbitMQ.
4. `unzip -p {pkg} '*.nuspec' | grep -E '(authors|description|tags|projectUrl|icon|readme|license)'` shows all metadata fields populated as Directory.Build.props + per-csproj defines them.
5. Root `README.md`, `LICENSE`, `CHANGELOG.md`, `icon.png` all exist on disk and are git-tracked.
6. `git status` after the plan runs shows ONLY the files listed in `files_modified` as changed.

## Post-conditions

- OSS-02 (README + OTel + comparison + sample link) — **satisfied** at the per-package
  README level. CI badges in root README will go live once Plan 02 ships and the repo
  has a public GitHub workflow run.
- OSS-04 (NuGet metadata polish) — **satisfied** at the metadata level. The .snupkg
  + SourceLink halves were already wired Phase 1; Plan 02 verifies them in CI.
- OSS-03 (CHANGELOG infrastructure) — **satisfied**. MinVer wiring is Phase 1 carry-forward.
</verification>

<success_criteria>
1. `test -f LICENSE && test -f CHANGELOG.md && test -f README.md && test -f icon.png` all pass at repo root.
2. `test -f src/Oragon.AdaptivePool.Core/README.md && test -f src/Oragon.AdaptivePool.RabbitMQ/README.md` pass.
3. `dotnet pack` for both packable projects produces .nupkg containing the per-package README.md + icon.png.
4. `unzip -p {pkg} '*.nuspec'` shows: `<authors>Luiz Carlos Faria</authors>`, `<license type="expression">MIT</license>`, `<readme>README.md</readme>`, `<icon>icon.png</icon>`, `<projectUrl>https://github.com/oragon/Oragon.AdaptivePool</projectUrl>`, refined `<description>` and `<tags>`.
5. Both per-package READMEs grep-pass for: `AddAdaptivePool<` (Core only) / `AddAdaptiveChannelPool` (RabbitMQ only); `AddMeter("Oragon.AdaptivePool")` (Core only); `samples/Oragon.AdaptivePool.RabbitMQ.Sample.BurstyPublisher` (both).
6. RabbitMQ README contains the connection-pool sizing callout (Phase 3 heads-up #1) and the AutomaticRecoveryEnabled override callout (matches EventId 2001 message format).
7. `dotnet build Oragon.AdaptivePool.sln -c Release` is green.
</success_criteria>

<output>
After completion, create `.planning/phases/04-polish-v1-release/04-01-SUMMARY.md` documenting:
- Files created/modified (all 9 files in `files_modified`)
- Per-package README sections — confirm OTel snippet matches the literal Meter/ActivitySource name `"Oragon.AdaptivePool"`
- icon.png generation method used (ImageMagick `convert` or Python PIL fallback)
- Any deviations (e.g., if icon generation tool unavailable, document fallback)
- Output of `dotnet pack` showing .nupkg sizes and content listing
- Confirmation that `<PackageLicenseExpression>` was successfully relocated to Directory.Build.props with no per-csproj duplication
- Carry-forward notes for Plan 02 (in particular: badges in root README assume `oragon/Oragon.AdaptivePool` is the GitHub remote — Plan 02 must verify that assumption holds when configuring `release.yml`'s `dotnet nuget push` step)
</output>
