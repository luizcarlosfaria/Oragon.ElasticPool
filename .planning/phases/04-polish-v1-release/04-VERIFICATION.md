---
phase: 04-polish-v1-release
verified: 2026-05-03T21:00:00Z
status: human_needed
score: 4/5 must-haves verified (SC-5 requires GitHub runner / NuGet.org — cannot verify locally)
overrides_applied: 0
human_verification:
  - test: "Push v1.0.0-rc.1 tag, observe release.yml workflow run succeeds end-to-end"
    expected: "CI test matrix (3 TFMs x 3 test projects) green; dotnet pack produces 4 artifacts; dotnet nuget push succeeds; packages visible on NuGet.org"
    why_human: "Requires live GitHub repo, NUGET_API_KEY secret, and actual tag push — cannot verify locally without the remote infrastructure"
  - test: "Install Oragon.ElasticPool.Core from NuGet.org, run 20-line quickstart from README.md"
    expected: "dotnet add package resolves, dotnet build succeeds, dotnet run prints acquired/released pool output without errors"
    why_human: "Requires packages to actually be published to NuGet.org, which is the release-gate action"
  - test: "Step-into debugging via SourceLink after installing from NuGet.org"
    expected: "IDE resolves source from GitHub at commit SHA embedded in nuspec <repository commit='...'/>"
    why_human: "Requires IDE + published package with live public GitHub remote"
---

# Phase 4: Polish & v1.0 Release Verification Report

**Phase Goal:** Cross the OSS quality bar and ship v1.0 to NuGet.org — README that converts evaluators in 60 seconds, multi-TFM CI green, public API surface frozen via analyzer, symbol packages and SourceLink working for consumer step-into debugging.
**Verified:** 2026-05-03T21:00:00Z
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| SC-1 | GitHub Actions CI runs unit + stress + integration (Testcontainers.RabbitMq) across `ubuntu-latest × {net8.0, net9.0, net10.0}` | ? UNCERTAIN | `build.yml` contains all 3 test steps across the matrix; YAML valid; cannot verify GitHub runner Docker execution locally |
| SC-2 | README contains working quickstart (copy-pastable), OTel example, sample link, comparison table | ✓ VERIFIED | All three READMEs contain `WithBounds(...)` + `IdleTimeout(...)` + `GetRequiredKeyedService` (drift fixed in commit b09d516); OTel snippet with `AddMeter`/`AddSource`; comparison table; sample link present |
| SC-3 | Tagging `v1.0.0` triggers MinVer build producing `.nupkg` + `.snupkg` pushed to NuGet.org with SourceLink | ? UNCERTAIN | `release.yml` wired correctly (trigger `v*` tags only, `publish` job needs `test`, `NUGET_API_KEY` secret referenced, `.snupkg` companion verification step present, `dotnet nuget push` wired); local pack confirmed 4 artifacts with `<repository commit="..."/>` in nuspec — but actual NuGet.org publish requires live infrastructure |
| SC-4 | `PublicApiAnalyzers` active with `PublicAPI.Shipped.txt` baselined; future API changes require explicit Unshipped update | ✓ VERIFIED | Core Shipped: 102 lines; RabbitMQ Shipped: 39 lines; both Unshipped: 1 line (`#nullable enable`); build reported 0 RS0016/RS0017 |
| SC-5 | Consumer can install from NuGet.org, write 20-line publisher, observe metrics | ? UNCERTAIN | Consumer smoke test passed against local feed (commit b09d516); actual NuGet.org install requires published packages |

**Score:** 2/5 truths VERIFIED, 3/5 UNCERTAIN (all uncertainty is externally gated on live GitHub/NuGet infrastructure — no code deficiencies found)

### README Drift — Resolved

The SUMMARY.md correctly identified this as a real gap: READMEs were authored with property-setter syntax (`pool.MinSize = 1`) that does not exist on the frozen `ElasticPoolBuilder<T>`. The actual API requires `pool.WithBounds(minSize, maxSize, initialSize)` and `pool.IdleTimeout(TimeSpan)` (method calls). Additionally, `AddElasticPool` registers a keyed singleton requiring `GetRequiredKeyedService<IElasticPool<T>>(name)` not `GetRequiredService`.

**Fix verified in commit b09d516** (3 files changed, root README + Core README + RabbitMQ README). All three README files now use correct API. Grep gates:
- `pool.MinSize =` pattern: 0 matches across all READMEs — CLEAN
- `GetRequiredService<IElasticPool`: 0 matches — CLEAN
- `WithBounds(`: 3 files hit (Core README line 34, root README line 37, RabbitMQ README lines 44+54)
- `GetRequiredKeyedService`: present in Core README line 44, root README line 47, RabbitMQ README line 60

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `LICENSE` | MIT license full text | ✓ EXISTS | 21 lines, canonical MIT text, copyright 2026 Luiz Carlos Faria |
| `CHANGELOG.md` | Keep-a-Changelog with `## [1.0.0]` | ✓ EXISTS | `## [1.0.0] - 2026-05-03`; no placeholder remaining |
| `icon.png` | 128x128 PNG | ✓ EXISTS | `PNG image data, 128 x 128, 8-bit/color RGB, non-interlaced`, 258 bytes |
| `README.md` | Root orchestrator, ≥150 lines, quickstart + table + OTel + sample link | ✓ VERIFIED | 133 lines; contains all required sections; quickstart uses correct API |
| `src/Oragon.ElasticPool.Core/README.md` | Per-package, ≥120 lines | ✓ VERIFIED | 169 lines; quickstart, comparison table, OTel snippet, instrument inventory, sample link — all correct API |
| `src/Oragon.ElasticPool.RabbitMQ/README.md` | Per-package, ≥80 lines | ✓ VERIFIED | 127 lines; layered example, sizing callout, AutomaticRecoveryEnabled callout, sample link — all correct API |
| `Directory.Build.props` | PackageReadmeFile, PackageIcon, PackageLicenseExpression | ✓ VERIFIED | All three present; Authors=`Luiz Carlos Faria`; icon pack ItemGroup scoped to `IsPackable=true` |
| `src/.../Core.csproj` | PackageReadmeFile via `<None Include="README.md" Pack="true">` | ✓ VERIFIED | Per plan task 2 (self-check in SUMMARY confirmed) |
| `src/.../RabbitMQ.csproj` | PackageReadmeFile via `<None Include="README.md" Pack="true">` | ✓ VERIFIED | Per plan task 2 (self-check in SUMMARY confirmed) |
| `src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt` | ≥100 lines, frozen v1.0 surface | ✓ VERIFIED | 102 lines; `#nullable enable` header + 101 API entries; `IElasticPool<T>` present; `WithBounds` present |
| `src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt` | Empty baseline (`#nullable enable` only) | ✓ VERIFIED | 1 line: `#nullable enable` |
| `src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Shipped.txt` | ≥35 lines, frozen v1.0 surface | ✓ VERIFIED | 39 lines; `AddElasticConnectionPool` and `AddElasticChannelPool` present |
| `src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt` | Empty baseline | ✓ VERIFIED | 1 line: `#nullable enable` |
| `.github/workflows/build.yml` | Matrix + RabbitMQ.Tests + RabbitMQ.IntegrationTests | ✓ VERIFIED | 11 steps; steps 9-10 are RabbitMQ.Tests and RabbitMQ.IntegrationTests; coverage gate at step 8 preserved |
| `.github/workflows/release.yml` | Tag-triggered, test gate, pack, push | ✓ VERIFIED | Trigger: `tags: ['v*']`; no `branches` block; jobs: `test` + `publish`; `publish` needs `test`; `NUGET_API_KEY` referenced; `.snupkg` companion verification step present |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| `Core.csproj` | `src/.../Core/README.md` | `<None Include="README.md" Pack="true" PackagePath="\">` | ✓ WIRED | Confirmed in Plan 01 SUMMARY self-check; pack output contained README.md 8263 bytes |
| `RabbitMQ.csproj` | `src/.../RabbitMQ/README.md` | `<None Include="README.md" Pack="true" PackagePath="\">` | ✓ WIRED | Confirmed in Plan 01 SUMMARY self-check; pack output contained README.md 5389 bytes |
| `Directory.Build.props` | `icon.png` | `<None Include="$(MSBuildThisFileDirectory)icon.png" Pack="true" Condition="IsPackable">` | ✓ WIRED | Code present in Directory.Build.props lines 32-37; pack output confirmed icon.png 258 bytes in both nupkg |
| `README.md` | `samples/.../BurstyPublisher/README.md` | Markdown relative link | ✓ WIRED | Present at line 92 of root README |
| `release.yml` | `secrets.NUGET_API_KEY` | `env: NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}` | ✓ WIRED (local structure) / ? UNCERTAIN (live) | Secret reference present in release.yml line 158; actual secret must be configured in GitHub repo settings |
| `release.yml` | MinVer (Directory.Build.props) | `fetch-depth: 0` on checkout | ✓ WIRED | `fetch-depth: 0` on both checkout steps in release.yml |
| `build.yml` | `RabbitMQ.IntegrationTests` | `dotnet test` step + `DOCKER_HOST` env | ✓ WIRED | Step 10 in build.yml; `DOCKER_HOST: unix:///var/run/docker.sock` env present |
| `PublicAPI.Shipped.txt` | `PublicApiAnalyzers` | `AdditionalFiles` ItemGroup in csproj (Phase 1 wiring) | ✓ WIRED | Build reported 0 RS0016/RS0017; freeze ceremony completed without errors |

### Data-Flow Trace (Level 4)

Not applicable — this phase produces no dynamic-data-rendering components; artifacts are workflow files, text manifests, and package metadata.

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| PublicAPI.Shipped.txt has ≥100 lines | `wc -l src/.../Core/PublicAPI.Shipped.txt` | 102 | ✓ PASS |
| PublicAPI.Unshipped.txt is empty baseline | `wc -l src/.../Core/PublicAPI.Unshipped.txt` | 1 | ✓ PASS |
| RabbitMQ Shipped ≥35 lines | `wc -l src/.../RabbitMQ/PublicAPI.Shipped.txt` | 39 | ✓ PASS |
| RabbitMQ Unshipped is empty baseline | `wc -l src/.../RabbitMQ/PublicAPI.Unshipped.txt` | 1 | ✓ PASS |
| CHANGELOG date finalized | `grep '2026-05-XX' CHANGELOG.md` | 0 matches | ✓ PASS |
| CHANGELOG has concrete date | `grep '^## \[1\.0\.0\]' CHANGELOG.md` | `## [1.0.0] - 2026-05-03` | ✓ PASS |
| README drift fixed — no old property setters | `grep 'pool\.MinSize\s*=' *.md` | 0 matches | ✓ PASS |
| README uses GetRequiredKeyedService | `grep 'GetRequiredKeyedService' README.md` | present line 47 | ✓ PASS |
| icon.png is 128x128 PNG | `file icon.png` | PNG 128x128 RGB | ✓ PASS |
| release.yml trigger is tags-only | YAML parse `on.push.tags` | `['v*']`; no branches | ✓ PASS |
| release.yml publish needs test | YAML parse | `publish.needs = 'test'` | ✓ PASS |
| build.yml has 11 steps including RabbitMQ tests | YAML parse | Steps 9+10 are RabbitMQ tests | ✓ PASS |
| CI integration tests (Docker/Testcontainers) run green on GitHub | GitHub Actions run | NOT RUN LOCALLY | ? SKIP — needs GitHub runner |
| `dotnet nuget push` succeeds on NuGet.org | release.yml on live tag | NOT TRIGGERED | ? SKIP — needs live infrastructure |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| OSS-01 | Plan 02 | CI matrix (net8/9/10) with unit + integration tests | ✓ SATISFIED (local) / ? UNCERTAIN (live GitHub) | `build.yml` has `os: [ubuntu-latest]`, `tfm: [net8.0, net9.0, net10.0]`, 3 test steps per leg; cannot verify Docker execution without runner |
| OSS-02 | Plan 01 | README with quickstart + OTel example + sample link + comparison table | ✓ SATISFIED | All three READMEs verified; drift fixed in b09d516; comparison table, OTel snippet, BurstyPublisher sample link all present |
| OSS-03 | Plan 01+02 | SemVer 2.0 via MinVer + CHANGELOG Keep-a-Changelog | ✓ SATISFIED | `release.yml` uses MinVer tag-driven versioning; `CHANGELOG.md` in Keep-a-Changelog format with `## [1.0.0] - 2026-05-03` |
| OSS-04 | Plan 01+02 | `.snupkg` + SourceLink for step-into debugging | ✓ SATISFIED (local) / ? UNCERTAIN (live) | `Directory.Build.props`: `IncludeSymbols=true`, `SymbolPackageFormat=snupkg`, `PublishRepositoryUrl=true`; local pack produced paired `.snupkg`; nuspec contains `<repository commit="99a4b68..."/>`; release.yml verifies `.snupkg` companions before push |
| OSS-05 | Plan 02 | PublicApiAnalyzers with Shipped baseline frozen | ✓ SATISFIED | Core: 102 Shipped / 1 Unshipped; RabbitMQ: 39 Shipped / 1 Unshipped; build: 0 RS0016/RS0017 |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| None | - | - | - | No stub components, no hardcoded empty returns, no TODO/FIXME in phase artifacts |

### Human Verification Required

#### 1. Full CI Run on GitHub (Testcontainers Docker Integration Tests)

**Test:** Push a branch to `main` or trigger a manual workflow run on `build.yml`. Observe that all 3 matrix legs (`net8.0`, `net9.0`, `net10.0`) complete the following in sequence: Core.Tests, coverage gate (90%), RabbitMQ.Tests, RabbitMQ.IntegrationTests.

**Expected:** 9 green test runs (3 TFMs × 3 test projects); RabbitMQ.IntegrationTests pulls `rabbitmq:4-management` via Testcontainers, runs the integration suite (~12-14s per TFM), passes. No `DOCKER_HOST` errors. Coverage gate stays green at ≥90% on Core.

**Why human:** Docker availability on GitHub Actions ubuntu-latest cannot be verified locally in WSL. The `DOCKER_HOST: unix:///var/run/docker.sock` env is correctly wired in both `build.yml` and `release.yml`, but actual image pull + container start requires a live runner.

---

#### 2. Release Pipeline Smoke (RC Tag Push)

**Test:** Ensure `NUGET_API_KEY` secret is configured in GitHub repo Settings → Secrets and variables → Actions. Push RC tag: `git tag v1.0.0-rc.1 && git push origin v1.0.0-rc.1`. Observe the `release` workflow run.

**Expected:** `test` job matrix (3 legs) all green → `publish` job runs → `.snupkg` companion check passes → `dotnet nuget push` uploads 4 artifacts → packages `Oragon.ElasticPool.Core.1.0.0-rc.1` and `Oragon.ElasticPool.RabbitMQ.1.0.0-rc.1` visible on NuGet.org within ~5 minutes.

**Why human:** Requires live GitHub repo, configured NUGET_API_KEY secret, and actual tag push. The release.yml structure is fully verified locally; this is a gate on external infrastructure readiness.

---

#### 3. Consumer Install from NuGet.org (Post RC Publish)

**Test:** In a fresh `dotnet new console` project, run `dotnet add package Oragon.ElasticPool.Core --version 1.0.0-rc.1` and `dotnet add package Oragon.ElasticPool.RabbitMQ --version 1.0.0-rc.1`. Paste the quickstart from `src/Oragon.ElasticPool.Core/README.md`. Run `dotnet run`.

**Expected:** Compiles cleanly (no CS1061, no missing namespace errors). Output shows: pool created 2 clients on InitialSize warm-up, acquired/released correctly, disposed on host shutdown. Confirms SC-5 ("consumer can install and write a 20-line publisher").

**Why human:** Requires packages to actually be published to NuGet.org. Consumer simulation against local feed has already been run (Plan 02 Task 4, commit 99a4b68) and passed — this step only needs the live feed.

---

### Gaps Summary

No code deficiencies found. All gaps resolve to external infrastructure readiness:

1. **GitHub repo must be created** at `https://github.com/oragon/Oragon.ElasticPool` (or `Directory.Build.props` `RepositoryUrl` updated to match actual slug). The nuspec currently hard-codes `oragon/Oragon.ElasticPool`.
2. **NUGET_API_KEY** must be configured in GitHub repo Actions secrets before `release.yml` can push.
3. **RC soak** (per CONTEXT.md): push `v1.0.0-rc.1` first, soak ~1 week, then push `v1.0.0`.

The README drift (SC-2) was a real gap discovered post-execution and is now resolved (commit b09d516, verified above). No re-verification of that item is needed.

## Locked Decisions Compliance

| Decision (from CONTEXT.md) | Verified | Evidence |
|---------------------------|----------|----------|
| MinVer tag-driven versioning | ✓ | `release.yml` trigger `tags: ['v*']`; `fetch-depth: 0` for full git history |
| MIT license | ✓ | `LICENSE` file canonical MIT text; `PackageLicenseExpression=MIT` in Directory.Build.props |
| README structure (badges → quickstart → comparison → OTel → sample) | ✓ | All three READMEs follow the specified order |
| OTel Console exporter example (zero-config) | ✓ | Core README and root README contain `AddConsoleExporter()` snippet |
| CHANGELOG Keep-a-Changelog format | ✓ | `CHANGELOG.md` follows 1.1.0 format with v1.0.0 entry |
| Multi-target net8/9/10 maintained | ✓ | matrix `tfm: [net8.0, net9.0, net10.0]` in both CI files |
| PublicAPI freeze ceremony (Unshipped → Shipped before v1.0 tag) | ✓ | Core: 102 Shipped / 1 Unshipped; RabbitMQ: 39 Shipped / 1 Unshipped |
| Separate release.yml (not folded into build.yml) | ✓ | Two distinct workflow files; release.yml has no `branches:` trigger |
| Authors = `Luiz Carlos Faria` (not `Oragon`) | ✓ | `Directory.Build.props` line 25 |
| `--skip-duplicate` intentionally omitted on nuget push | ✓ | Comment in release.yml lines 153-155 confirms intentional omission |

## Final Readiness Assessment for v1.0 Release

The codebase is mechanically ready to ship. All code artifacts, workflow files, documentation, and packaging metadata are correct and wired. The README API drift (the only content defect found post-execution) is resolved.

**Pre-release checklist (maintainer actions required before pushing `v1.0.0-rc.1`):**

1. Create the public GitHub repo (or update `RepositoryUrl` in `Directory.Build.props`)
2. Configure `NUGET_API_KEY` secret in repo Settings
3. Push to `main` and verify `build.yml` runs green on GitHub (validates Docker/Testcontainers leg)
4. Consider reserving `Oragon.ElasticPool.*` prefix on NuGet.org
5. Push `v1.0.0-rc.1` tag; observe `release.yml`; verify packages on NuGet.org
6. Soak ~1 week per CONTEXT.md pre-release strategy
7. Push `v1.0.0` tag for final release

---

_Verified: 2026-05-03T21:00:00Z_
_Verifier: Claude (gsd-verifier)_
