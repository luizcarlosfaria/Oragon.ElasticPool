---
phase: 01-core-skeleton-fixed-size-pool
plan: 01
subsystem: scaffolding
tags: [scaffolding, repo-setup, ci, cpm, sourcelink, publicapi, mtp, xunit-v3]
requires:
  - .NET SDK 10 (with apphost packs for net8/net9/net10 cross-targeting)
provides:
  - Repo-wide build infrastructure (Directory.Build.props, Directory.Packages.props, global.json, .editorconfig, .gitignore)
  - Multi-target Core library project skeleton (net10/net9/net8) with PublicApiAnalyzers wired
  - Two test projects (Core.Tests + Core.Stress) on xUnit v3 + Microsoft.Testing.Platform
  - Solution file (.sln, classic format) referencing all three projects
  - GitHub Actions CI pipeline (build + test) on net8/net9/net10 matrix, Stress excluded
affects:
  - Plans 02 + 03 build on top of this skeleton (they own the actual pool implementation + tests)
tech-stack:
  added:
    - Microsoft.Extensions.Logging.Abstractions 10.0.6
    - Microsoft.Extensions.DependencyInjection.Abstractions 10.0.6
    - Microsoft.Extensions.Options 10.0.6
    - Microsoft.CodeAnalysis.PublicApiAnalyzers 3.3.4
    - MinVer 6.0.0
    - Microsoft.SourceLink.GitHub 8.0.0
    - xunit.v3 3.2.2
    - xunit.runner.visualstudio 3.1.5 (NOT xunit.v3.runner.visualstudio — see deviations)
    - AwesomeAssertions 9.4.0
    - NSubstitute 5.3.0
    - Microsoft.Extensions.TimeProvider.Testing 10.5.0
    - Microsoft.Extensions.Diagnostics.Testing 10.5.0
    - Microsoft.Extensions.DependencyInjection 10.0.6
    - Microsoft.Extensions.Logging.Console 10.0.6
    - coverlet.collector 6.0.4
  patterns:
    - Central Package Management with transitive pinning (CPM)
    - SourceLink + deterministic build (CI-gated via $(CI) env var)
    - PublicApiAnalyzers from day 1 with empty baselines (#nullable enable)
    - xUnit v3 self-executing test projects under Microsoft.Testing.Platform (UseMicrosoftTestingPlatformRunner=true, OutputType=Exe)
    - Stress project isolated from CI default (CI invokes Core.Tests explicitly)
key-files:
  created:
    - global.json
    - .gitignore
    - .editorconfig
    - Directory.Build.props
    - Directory.Packages.props
    - Oragon.ElasticPool.sln
    - src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj
    - src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt
    - src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt
    - tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj
    - tests/Oragon.ElasticPool.Core.Tests/PlaceholderSmokeTest.cs
    - tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj
    - tests/Oragon.ElasticPool.Core.Stress/PlaceholderStressFact.cs
    - .github/workflows/build.yml
  modified: []
decisions:
  - Used `xunit.runner.visualstudio` 3.1.5 (the unified runner adapter for xUnit v3) instead of the planner's `xunit.v3.runner.visualstudio` — that package id does not exist on NuGet.
  - Bumped Microsoft.Extensions.Logging.Abstractions/Options/DependencyInjection/Logging.Console from 10.0.5 to 10.0.6 to satisfy CPM transitive pinning (Microsoft.Extensions.Logging.Console 10.0.x pulls Logging 10.0.6 → Abstractions/Options >= 10.0.6).
  - Pinned Microsoft.Extensions.Diagnostics.Testing to 10.5.0 (planner asked for 10.0.5; the package does not have a 10.0.5 — its 10.x cadence is 10.0.0 → 10.1.0 → 10.2.0 → 10.3.0 → 10.4.0 → 10.5.0). Aligned with TimeProvider.Testing 10.5.0.
  - Added `<OutputType>Exe</OutputType>` to both test .csproj files — xUnit v3 + MTP requires the test project to be self-executing. Plan snippets omitted this property; build fails without it.
  - Used `dotnet test --project <path>` syntax in CI (instead of `dotnet test <path>`) — required by .NET 10 SDK when global.json selects MTP runner.
  - Created the solution as classic `.sln` format (passing `--format sln` to `dotnet new sln`) because the .NET 10 SDK now defaults to the new `.slnx` (XML) format, and `.slnx` is not yet universally supported by tooling. The plan and CI workflow expect a `.sln`.
metrics:
  duration: 10m37s
  completed: 2026-05-03
  tasks: 3
  files_created: 14
  commits: 3
---

# Phase 1 Plan 01: Core Skeleton — Repository Scaffolding Summary

**One-liner:** Stood up the empty Oragon.ElasticPool.sln with three multi-target (net8/net9/net10) projects, Central Package Management, SourceLink + deterministic build, PublicApiAnalyzers wired with empty baselines, xUnit v3 + Microsoft.Testing.Platform test/stress projects, and a minimal multi-TFM GitHub Actions CI workflow that runs Core.Tests only — all green from a fresh clone.

## What Was Built

### Repository configuration (Task 1 — commit 5eb02c8)

| File | Purpose |
| --- | --- |
| `global.json` | Pins .NET SDK to 10.0.100 with `latestFeature` rollForward; selects `Microsoft.Testing.Platform` as the `dotnet test` runner. |
| `.gitignore` | Standard Visual Studio / .NET ignore (bin/, obj/, .vs/, *.user, TestResults/, *.pfx, secrets.json, BenchmarkDotNet artifacts, etc.). |
| `.editorconfig` | UTF-8 + LF + 4-space indent; .NET coding conventions; nullable warnings enabled; Allman braces (`csharp_new_line_before_open_brace = all`); file-scoped namespaces. |
| `Directory.Build.props` | Repo-wide: Nullable=enable, ImplicitUsings=enable, LangVersion=latest, TreatWarningsAsErrors=true, EnforceCodeStyleInBuild=true, SourceLink (PublishRepositoryUrl/EmbedUntrackedSources), snupkg symbol packages, Deterministic=true, ContinuousIntegrationBuild + DeterministicSourcePaths gated on `$(CI) == 'true'`. Authors/Company/Copyright/RepositoryUrl set. |
| `Directory.Packages.props` | Central Package Management active (`ManagePackageVersionsCentrally=true`, `CentralPackageTransitivePinningEnabled=true`); all Phase 1 NuGet versions pinned in one place. |

### Project skeletons (Task 2 — commit 8e65d03)

| Project | Type | TFMs | Notes |
| --- | --- | --- | --- |
| `src/Oragon.ElasticPool.Core` | Library, packable | net10.0;net9.0;net8.0 | PackageReferences (no inline versions; CPM-driven): Logging.Abstractions, DI.Abstractions, Options, PublicApiAnalyzers (PrivateAssets=all), MinVer (PrivateAssets=all), SourceLink.GitHub (PrivateAssets=all). PublicAPI.Shipped.txt + PublicAPI.Unshipped.txt registered as `<AdditionalFiles>`. |
| `tests/Oragon.ElasticPool.Core.Tests` | Test exe, non-packable | net10.0;net9.0;net8.0 | xUnit v3 + MTP runner (`UseMicrosoftTestingPlatformRunner=true`, `TestingPlatformDotnetTestSupport=true`, `OutputType=Exe`). PackageReferences: xunit.v3, xunit.runner.visualstudio, AwesomeAssertions, NSubstitute, TimeProvider.Testing, DI, Logging.Console, Diagnostics.Testing, coverlet.collector. ProjectReference to Core. |
| `tests/Oragon.ElasticPool.Core.Stress` | Test exe, non-packable | net10.0;net9.0;net8.0 | Same MTP wiring as Tests; smaller dep set (no Diagnostics.Testing). ProjectReference to Core. |
| `Oragon.ElasticPool.sln` | Classic `.sln` (not `.slnx`) | — | Contains all three projects. CI invokes Core.Tests explicitly to keep Stress out of the default sweep. |

`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` each contain a single `#nullable enable` line — the canonical empty baseline expected by `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Plans 02 + 03 will populate Unshipped.txt as the public surface lands.

`PlaceholderSmokeTest.cs` (Tests) and `PlaceholderStressFact.cs` (Stress) each contain a trivial `[Fact] true.Should().BeTrue()` that proves the toolchain end-to-end (xUnit v3 + MTP + AwesomeAssertions + ProjectReference to Core all link). Both are slated for deletion by Plan 02 and Plan 03 respectively.

### CI workflow (Task 3 — commit 0b59543)

`.github/workflows/build.yml` — GitHub Actions, triggered on push to `main` and PRs targeting `main`:

- Single OS for now: `ubuntu-latest` (Windows + macOS deferred to Phase 4 OSS-01).
- `actions/setup-dotnet@v4` installs SDKs `8.0.x`, `9.0.x`, and `10.0.x`.
- TFM matrix: `net8.0`, `net9.0`, `net10.0`.
- Steps: `dotnet restore` → `dotnet build -c Release --no-restore -f ${{ matrix.tfm }}` → `dotnet test --project tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj -c Release --no-build -f ${{ matrix.tfm }}`.
- The Stress project is **never** invoked (per CONTEXT.md decision). The dedicated nightly stress job is a Phase 4 deliverable.
- `CI=true` is implicit on GitHub Actions runners → activates `ContinuousIntegrationBuild` and `DeterministicSourcePaths` from `Directory.Build.props`.

## Verification

End-to-end checks from the plan's `<verification>` block all green:

```
dotnet restore Oragon.ElasticPool.sln       → all 3 projects restored, 0 errors, 0 NU* warnings
dotnet build  Oragon.ElasticPool.sln -c Release --no-restore
                                              → 4 build outputs (Core × 3 TFMs + 2 test projects × 3 TFMs)
                                              → 0 errors; 6 SourceLink warnings (no remote configured locally;
                                                disappear in CI where actions/checkout sets origin)
dotnet test --project tests/Oragon.ElasticPool.Core.Tests   → 3 passed, 0 failed (1 placeholder × 3 TFMs)
dotnet test --project tests/Oragon.ElasticPool.Core.Stress  → 3 passed, 0 failed (1 placeholder × 3 TFMs)
test ! -e ./bin && test ! -e ./obj            → no stray top-level build artifacts
```

PublicApiAnalyzers is wired and quiet (no PublicAPI* warnings — empty baselines match an empty surface).

## Deviations from Plan

All deviations were forced by upstream NuGet reality (wrong package id / non-existent version) or by the strict requirements of the .NET 10 SDK + MTP runner combination. None introduce architectural change; all preserve plan intent.

### Auto-fixed Issues

**1. [Rule 1 — Bug] Wrong package id `xunit.v3.runner.visualstudio`**
- **Found during:** Task 2 restore.
- **Issue:** `xunit.v3.runner.visualstudio` is not published on NuGet.org (NU1101). The unified VS Test Explorer adapter for xUnit v3 is published as `xunit.runner.visualstudio` (versions 3.x). The plan's STACK research snippet propagated the wrong id.
- **Fix:** Renamed the central pin and both PackageReferences to `xunit.runner.visualstudio`; pinned to `3.1.5` (latest stable that targets xUnit v3).
- **Files modified:** `Directory.Packages.props`, `tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj`, `tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj`.
- **Commit:** 8e65d03.

**2. [Rule 1 — Bug] Non-existent version `Microsoft.Extensions.Diagnostics.Testing 10.0.5`**
- **Found during:** Task 2 restore.
- **Issue:** The 10.x cadence for `Microsoft.Extensions.Diagnostics.Testing` is 10.0.0 → 10.1.0 → 10.2.0 → 10.3.0 → 10.4.0 → 10.5.0. There is no `10.0.5`. Plan was guessed from sibling package versioning.
- **Fix:** Pinned to `10.5.0` (current latest), aligning with `Microsoft.Extensions.TimeProvider.Testing 10.5.0` (same family, same cadence).
- **Files modified:** `Directory.Packages.props`.
- **Commit:** 8e65d03.

**3. [Rule 3 — Blocking] CPM transitive pinning rejected M.E.* 10.0.5 trio**
- **Found during:** Task 2 restore (NU1109 after fix #1).
- **Issue:** With `CentralPackageTransitivePinningEnabled=true`, the Tests project pulls `Microsoft.Extensions.Logging.Console 10.0.5` → `Microsoft.Extensions.Logging 10.0.6` → which requires `Logging.Abstractions/Options >= 10.0.6`. The central pins at 10.0.5 caused a downgrade error.
- **Fix:** Bumped `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.DependencyInjection`, and `Microsoft.Extensions.Logging.Console` from 10.0.5 → 10.0.6. Kept `Microsoft.Extensions.DependencyInjection.Abstractions` at 10.0.6 (already plan-pinned).
- **Files modified:** `Directory.Packages.props`.
- **Commit:** 8e65d03.

**4. [Rule 3 — Blocking] xUnit v3 + MTP requires `<OutputType>Exe</OutputType>`**
- **Found during:** Task 2 build.
- **Issue:** `xunit.v3.core.mtp-v1.targets` errors out unless test projects declare `OutputType=Exe` (they are self-executing under MTP). Plan csproj snippets did not include this property.
- **Fix:** Added `<OutputType>Exe</OutputType>` to both test projects.
- **Files modified:** `tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj`, `tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj`.
- **Commit:** 8e65d03.

**5. [Rule 3 — Blocking] `dotnet test <project>` rejected by .NET 10 SDK with MTP runner**
- **Found during:** Task 2 verification + Task 3 CI authoring.
- **Issue:** With `"runner": "Microsoft.Testing.Platform"` in global.json, `dotnet test` on .NET 10 SDK rejects positional project paths with `Specifying a project for 'dotnet test' should be via '--project'`. The plan's verify command and proposed CI step used the legacy positional form.
- **Fix:** Switched to `dotnet test --project <path>` in both the verify runs and `.github/workflows/build.yml`.
- **Files modified:** `.github/workflows/build.yml` (committed as part of Task 3, with explanatory comment).
- **Commit:** 0b59543.

**6. [Rule 3 — Blocking] `dotnet new sln` defaulted to `.slnx` on .NET 10**
- **Found during:** Task 2 solution authoring.
- **Issue:** .NET 10 SDK's `dotnet new sln` produces `Oragon.ElasticPool.slnx` (XML format) by default. The plan, the CI workflow, and the verify commands all reference `.sln`.
- **Fix:** Recreated using `dotnet new sln --format sln` to force the classic `.sln` format.
- **Files modified:** `Oragon.ElasticPool.sln` (created).
- **Commit:** 8e65d03.

### Out-of-Scope Findings (NOT fixed)

- **SourceLink "no remote" warnings.** `Microsoft.Build.Tasks.Git` and `Microsoft.SourceLink.Common` emit two warnings per project ("Repository '/mnt/...' has no remote" and "Source control information is not available — the generated source link is empty") because the local working copy has no `origin` remote. These are MSBuild-task warnings (not C# compiler warnings) so they do not promote to errors under `TreatWarningsAsErrors=true`. They will disappear automatically in CI (where `actions/checkout@v4` configures origin) and locally as soon as a remote is added. No fix needed — adding a remote is the user's call (Rule 4 territory) and a no-op for plan correctness.

### Authentication Gates

None — all package restores worked against the public NuGet.org feed without credentials.

## Heads-up to Plan 02

- **Do NOT re-add `<PackageReadmeFile>README.md</PackageReadmeFile>` to `Oragon.ElasticPool.Core.csproj`** unless you also add a `README.md` and a matching `<None Include="README.md" Pack="true" PackagePath="\" />`. Phase 4 owns the README. The PackageReadmeFile reference was intentionally removed from the plan's snippet to avoid `dotnet pack` failure.
- **The placeholder smoke test (`tests/.../PlaceholderSmokeTest.cs`) is yours to delete** when the first real test lands.
- **Use the `--project` form for any `dotnet test` invocation** you add (script, docs, sample) — the bare positional form is broken under the MTP runner on .NET 10.
- **CPM is strict.** When you add a new dependency, pin it in `Directory.Packages.props`; reference it via `<PackageReference Include="..." />` (no `Version=` attribute) in the csproj. If transitive pinning produces an NU1109 downgrade, bump the offending central pin to satisfy the resolved transitive demand.
- **PublicApiAnalyzers is hot from day 1.** Every new public type/member must be appended to `PublicAPI.Unshipped.txt` (RS0016). Run `dotnet build` and the analyzer will tell you exactly what to add.
- **`OutputType=Exe` + `UseAppHost` (default `true`) means each test project produces a per-TFM apphost.** Don't disable apphost — xUnit v3 explicitly errors out without it. The build host (.NET 10 SDK) needs apphost packs for net8/net9/net10 installed.

## Plan 03 Heads-up

- Same notes as Plan 02 (PackageReadmeFile, `--project` form, CPM strictness, PublicApiAnalyzers).
- The Stress project's placeholder is yours to replace with the MaxSize=1 ping-pong test.
- The CI workflow does NOT and SHOULD NOT invoke the Stress project. If you need to add stress execution to CI in the future, it goes in a separate workflow (nightly schedule) per CONTEXT.md.

## Commits

| Task | Hash | Message |
| ---- | ---- | ------- |
| 1 | 5eb02c8 | feat(01-01): repo configuration (global.json, .gitignore, .editorconfig, Directory.Build.props, Directory.Packages.props) |
| 2 | 8e65d03 | feat(01-02): project skeletons (Core + Tests + Stress) with PublicAPI baselines and solution |
| 3 | 0b59543 | feat(01-03): minimal multi-TFM CI workflow (Core.Tests only — Stress excluded) |

## Self-Check: PASSED

All 14 created files exist on disk; all 3 task commits exist in `git log`. Final verification (restore + build + test core + test stress + no stray bin/obj at repo root) is green. PublicApiAnalyzers loads quietly (empty surface vs. empty baselines).
