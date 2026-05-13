---
phase: 04-polish-v1-release
plan: 01
subsystem: oss-release-prep
tags: [oss, packaging, docs, readme, license, changelog, nuget-metadata]
requires:
  - "Phase 1: Directory.Build.props (MinVer + SourceLink)"
  - "Phase 1: per-csproj IsPackable=true, PackageId, PackageLicenseExpression=MIT"
  - "Phase 3: samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher (link target)"
  - "Phase 3: AutomaticRecoveryEnabled override + EventId 2001 (callout target)"
provides:
  - "LICENSE (MIT, repo root) — GitHub-UI license recognition"
  - "CHANGELOG.md (Keep-a-Changelog 1.1.0; v1.0.0 entry; date placeholder 2026-05-XX for Plan 02)"
  - "icon.png (128x128 RGB PNG; embedded in both .nupkg)"
  - "README.md (root orchestrator)"
  - "src/Oragon.ElasticPool.Core/README.md (embedded in Core .nupkg; 30s quickstart, M.E.OP comparison table, OTel snippet, instrument inventory)"
  - "src/Oragon.ElasticPool.RabbitMQ/README.md (embedded in RabbitMQ .nupkg; layered example + 3 callouts: channel-per-publisher, connection sizing, AutomaticRecoveryEnabled override)"
  - "Directory.Build.props: PackageProjectUrl, PackageReadmeFile, PackageIcon, PackageLicenseExpression=MIT, PackageRequireLicenseAcceptance=false, Authors=Luiz Carlos Faria + repo-wide icon.png pack ItemGroup"
  - "Per-csproj refinement: Description (expanded), PackageTags (refined for evaluator search), <None Include='README.md' Pack='true'>; per-csproj PackageLicenseExpression stripped (DRY)"
affects:
  - "Directory.Build.props (+15 lines net; PackageLicenseExpression centralised)"
  - "src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj (+4 / -1 lines)"
  - "src/Oragon.ElasticPool.RabbitMQ/Oragon.ElasticPool.RabbitMQ.csproj (+4 / -1 lines)"
tech-stack:
  added: []
  patterns:
    - "Repo-wide PackageReadmeFile=README.md + per-csproj <None Include='README.md' Pack='true'> resolves to per-project README inside .nupkg"
    - "Repo-wide <ItemGroup Condition=IsPackable=true> in Directory.Build.props for icon.png — applies only to packable projects (Core, RabbitMQ); test/sample projects unaffected"
    - "Stdlib zlib/struct PNG generation as ImageMagick fallback (PIL not available, convert not installed)"
key-files:
  created:
    - "LICENSE (21 lines, MIT canonical text)"
    - "CHANGELOG.md (35 lines, Keep-a-Changelog 1.1.0)"
    - "icon.png (258 bytes, 128x128 RGB navy placeholder)"
    - "README.md (139 lines, root orchestrator)"
    - "src/Oragon.ElasticPool.Core/README.md (170 lines)"
    - "src/Oragon.ElasticPool.RabbitMQ/README.md (134 lines)"
  modified:
    - "Directory.Build.props (centralised package metadata)"
    - "src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj (Description / PackageTags refined; README pack item; PackageLicenseExpression removed)"
    - "src/Oragon.ElasticPool.RabbitMQ/Oragon.ElasticPool.RabbitMQ.csproj (Description / PackageTags refined; README pack item; PackageLicenseExpression removed)"
decisions:
  - "icon.png generated via stdlib zlib/struct PNG (ImageMagick `convert` unavailable in WSL; Python PIL not installed). 128x128 solid navy 0x1E3A5F RGB; 258 bytes; placeholder per CONTEXT.md D-04 — replaceable pre-tag without affecting pipeline"
  - "PackageLicenseExpression=MIT relocated from per-csproj to Directory.Build.props (DRY). Both Core and RabbitMQ csproj had it from Phase 1; now removed there and inherited"
  - "Authors changed from 'Oragon' to 'Luiz Carlos Faria' per CONTEXT.md decision (more accurate for OSS attribution); Company kept as 'Oragon'; Copyright updated to '2026 Luiz Carlos Faria and contributors'"
  - "RabbitMQ README adds an explicit 'channel-per-publisher discipline' callout above the connection-pool sizing callout — covers Phase 3 Pitfall 10 (IChannel not thread-safe; per-iteration acquire pattern)"
  - "READMEs use absolute https://github.com/oragon/Oragon.ElasticPool/... links to Sample/LICENSE; per-csproj README ships in .nupkg where relative paths can't resolve. Root README uses relative links (samples/..., src/..., CHANGELOG.md, LICENSE) for in-repo navigation on GitHub"
metrics:
  duration: "~30 minutes"
  completed: "2026-05-03"
  tasks: 2
  commits: 2
  files_created: 6
  files_modified: 3
  build_status: "0 errors, 0 warnings (Release; 9 projects)"
  package_status: "2 .nupkg + 2 .snupkg produced; both .nupkg contain README.md + icon.png + multi-TFM lib/{net8.0,net9.0,net10.0}/*.dll"
---

# Phase 4 Plan 01: OSS Documentation, Licensing & NuGet Metadata Polish Summary

Landed all OSS-quality documentation, licensing, and NuGet packaging metadata required to ship `Oragon.ElasticPool.Core` and `Oragon.ElasticPool.RabbitMQ` to NuGet.org. After this plan, `dotnet pack -c Release` produces well-formed `.nupkg` files containing per-package README, embedded icon, MIT license expression, refined description/tags/authors/project URLs — and the repo presents a polished GitHub-recognizable face (root README, LICENSE, CHANGELOG). No code changes; metadata + docs only.

## Tasks Executed

| Task | Name                                                                          | Commit    |
| ---- | ----------------------------------------------------------------------------- | --------- |
| 1    | Repo-wide OSS files (LICENSE, CHANGELOG, icon, root README) + Directory.Build.props metadata defaults | `37e0886` |
| 2    | Per-package READMEs (Core + RabbitMQ) + per-csproj metadata refinement        | `d085db8` |

## Verification Results

### Build (Release)

```
dotnet build Oragon.ElasticPool.sln -c Release
ok dotnet build: 9 projects, 0 errors, 0 warnings (00:00:12.41)
```

### Pack (Release) — both packable projects

```
dotnet pack src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj -c Release
ok Successfully created package '/tmp/pack-core/Oragon.ElasticPool.Core.0.0.0-alpha.0.79.nupkg' (98.5 KB)
ok Successfully created package '/tmp/pack-core/Oragon.ElasticPool.Core.0.0.0-alpha.0.79.snupkg' (52.7 KB)

dotnet pack src/Oragon.ElasticPool.RabbitMQ/Oragon.ElasticPool.RabbitMQ.csproj -c Release
ok Successfully created package '/tmp/pack-rmq/Oragon.ElasticPool.RabbitMQ.0.0.0-alpha.0.79.nupkg' (43.0 KB)
ok Successfully created package '/tmp/pack-rmq/Oragon.ElasticPool.RabbitMQ.0.0.0-alpha.0.79.snupkg' (36.2 KB)
```

### Package contents (unzip -l)

**Core .nupkg** contains:
- `lib/net8.0/Oragon.ElasticPool.Core.dll` (75776 bytes)
- `lib/net9.0/Oragon.ElasticPool.Core.dll` (75776 bytes)
- `lib/net10.0/Oragon.ElasticPool.Core.dll` (75776 bytes)
- `icon.png` (258 bytes)
- `README.md` (8263 bytes — the per-package one)

**RabbitMQ .nupkg** contains:
- `lib/net8.0/Oragon.ElasticPool.RabbitMQ.dll` (31232 bytes)
- `lib/net9.0/Oragon.ElasticPool.RabbitMQ.dll` (31232 bytes)
- `lib/net10.0/Oragon.ElasticPool.RabbitMQ.dll` (31232 bytes)
- `icon.png` (258 bytes)
- `README.md` (5389 bytes — the per-package one)

### nuspec metadata (verbatim from packed .nupkg)

**Core**:
```xml
<authors>Luiz Carlos Faria</authors>
<license type="expression">MIT</license>
<licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
<icon>icon.png</icon>
<readme>README.md</readme>
<projectUrl>https://github.com/oragon/Oragon.ElasticPool</projectUrl>
<description>Generic, elastic, self-healing in-process object pool for .NET 8/9/10. Composite-signal grow under pressure, hysteretic shrink, 5-stage lifecycle hooks (Factory/BeforeUse/Check/AfterUse/Release), pluggable failure policy, built-in OpenTelemetry (Meter + ActivitySource). Async-first, DI-first.</description>
<copyright>Copyright © 2026 Luiz Carlos Faria and contributors</copyright>
<tags>pool objectpool adaptive elastic self-healing async async-first dependency-injection opentelemetry observability resilience dotnet</tags>
```

**RabbitMQ**:
```xml
<authors>Luiz Carlos Faria</authors>
<license type="expression">MIT</license>
<licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
<icon>icon.png</icon>
<readme>README.md</readme>
<projectUrl>https://github.com/oragon/Oragon.ElasticPool</projectUrl>
<description>RabbitMQ.Client v7+ adapter for Oragon.ElasticPool — layered IConnection + IChannel pools with IsOpen-based health checks, AutomaticRecoveryEnabled override, and built-in OpenTelemetry. Async-first.</description>
<copyright>Copyright © 2026 Luiz Carlos Faria and contributors</copyright>
<tags>rabbitmq rabbitmq-client pool objectpool adaptive elastic connection-pool channel-pool async-first opentelemetry dotnet</tags>
```

### Grep gates (Task 1 + Task 2)

All gates green:

- LICENSE contains "MIT License" ✓
- CHANGELOG.md contains `## [1.0.0]` ✓
- icon.png is a `PNG image data, 128 x 128, 8-bit/color RGB, non-interlaced` ✓
- README.md (root) starts with `# Oragon.ElasticPool` and references `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher` ✓
- Directory.Build.props contains PackageReadmeFile=README.md, PackageIcon=icon.png, PackageLicenseExpression>MIT, and the icon.png repo-wide pack ItemGroup ✓
- Core README contains `^# Oragon.ElasticPool.Core`, `AddElasticPool<`, `Why not \`Microsoft.Extensions.ObjectPool`, `AddMeter("Oragon.ElasticPool")`, `AddSource("Oragon.ElasticPool")`, sample link ✓
- RabbitMQ README contains `^# Oragon.ElasticPool.RabbitMQ`, `AddElasticConnectionPool`, `AddElasticChannelPool`, `AutomaticRecoveryEnabled`, the connection-pool sizing callout ("connection pool's `MaxSize`"), sample link ✓
- Both csproj contain `<None Include="README.md" Pack="true"`; neither contains `PackageLicenseExpression` (centralised); Core tags include `self-healing`; RabbitMQ tags include `connection-pool` ✓

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 — Blocking] Stale obj/ Windows fallback path broke `dotnet pack` for the RabbitMQ project**

- **Found during:** Task 2 verification (first `dotnet pack` invocation against the RabbitMQ csproj)
- **Issue:** `error MSB4018: Unable to find fallback package folder 'C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages'` for `TargetFramework=net9.0`. The cached `obj/.../project.assets.json` from a prior Windows-side restore had Windows-only fallbackFolders baked in; under WSL/Linux the path is unreachable so `ResolvePackageAssets` threw.
- **Fix:** `rm -rf src/Oragon.ElasticPool.{Core,RabbitMQ}/obj src/Oragon.ElasticPool.{Core,RabbitMQ}/bin` and `dotnet restore --no-cache --force` regenerated clean assets without the Windows fallback. Subsequent pack invocations succeeded.
- **Files modified:** None (cache files only; not git-tracked)
- **Commit:** N/A (no source change required); validated empirically before Task 2 commit `d085db8`
- **Note for Plan 02:** CI runners start from clean obj/, so this is a local-only artifact and does not need any csproj/nuget.config change. No risk of recurrence in CI.

### Out-of-Scope Discoveries

None. All grep gates and verification commands passed without surfacing pre-existing issues.

## Carry-Forward Notes for Plan 02

1. **CHANGELOG date placeholder:** `## [1.0.0] - 2026-05-XX` — Plan 02 (release) replaces `XX` with the real publish day before tagging `v1.0.0`.
2. **GitHub remote URL assumption:** Both root README badges and per-package README absolute links assume `oragon/Oragon.ElasticPool` as the canonical GitHub remote. Plan 02 must verify (or update) this when configuring `release.yml`'s `dotnet nuget push` step. If the remote slug differs, update: Directory.Build.props `RepositoryUrl`/`PackageProjectUrl`, root `README.md` badge URLs, and the absolute links inside both per-package READMEs.
3. **icon.png placeholder:** The 258-byte solid-navy PNG is intentionally placeholder per CONTEXT.md D-04. Pre-tag (or in a future Plan) it can be replaced with a designed icon — pipeline already references it by relative path so no csproj change needed.
4. **OpenTelemetry dependency in code samples:** README quickstarts show `using OpenTelemetry; using OpenTelemetry.Metrics; using OpenTelemetry.Trace;` but the library does NOT take a hard dependency on OpenTelemetry — only on `System.Diagnostics.Metrics.Meter` and `System.Diagnostics.ActivitySource` (BCL). The OTel snippet only fires if the consumer adds `OpenTelemetry`/`OpenTelemetry.Exporter.Console` packages. This is correct and intentional, but worth re-verifying before publish that the README copy doesn't imply otherwise.
5. **PublicAPI freeze (Plan 02):** Per CONTEXT.md "specifics", before tagging `v1.0.0`, copy `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt` and empty Unshipped. Not done here; Plan 02 territory.
6. **CI badge live-ness:** The build/NuGet/Downloads badges in the root README are placeholders that will go live only after Plan 02 ships `release.yml` and the first publish runs.

## Self-Check: PASSED

Files claimed:
- `LICENSE` ✓ FOUND
- `CHANGELOG.md` ✓ FOUND
- `icon.png` ✓ FOUND (PNG 128x128)
- `README.md` ✓ FOUND
- `src/Oragon.ElasticPool.Core/README.md` ✓ FOUND
- `src/Oragon.ElasticPool.RabbitMQ/README.md` ✓ FOUND
- `Directory.Build.props` ✓ MODIFIED (Task 1)
- `src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj` ✓ MODIFIED (Task 2)
- `src/Oragon.ElasticPool.RabbitMQ/Oragon.ElasticPool.RabbitMQ.csproj` ✓ MODIFIED (Task 2)

Commits claimed:
- `37e0886` (Task 1) ✓ FOUND in `git log`
- `d085db8` (Task 2) ✓ FOUND in `git log`

Verification artefacts (out-of-tree, ephemeral):
- `/tmp/pack-core/Oragon.ElasticPool.Core.0.0.0-alpha.0.79.nupkg` ✓ produced
- `/tmp/pack-core/Oragon.ElasticPool.Core.0.0.0-alpha.0.79.snupkg` ✓ produced
- `/tmp/pack-rmq/Oragon.ElasticPool.RabbitMQ.0.0.0-alpha.0.79.nupkg` ✓ produced
- `/tmp/pack-rmq/Oragon.ElasticPool.RabbitMQ.0.0.0-alpha.0.79.snupkg` ✓ produced
