---
phase: 04-polish-v1-release
plan: 02
subsystem: oss-release-pipeline
tags: [oss, ci, release, github-actions, nuget-publish, publicapi-freeze, sourcelink, snupkg, minver, v1-freeze]
requires:
  - "Plan 04-01: CHANGELOG.md (date placeholder), per-package READMEs, NuGet metadata, icon"
  - "Phase 1: Directory.Build.props (MinVer + SourceLink wiring); .github/workflows/build.yml base"
  - "Phase 1+2: Core PublicAPI.Unshipped.txt baseline (102 lines)"
  - "Phase 3: RabbitMQ PublicAPI.Unshipped.txt baseline (39 lines); RabbitMQ.Tests + RabbitMQ.IntegrationTests projects"
provides:
  - ".github/workflows/build.yml: matrix CI now covers Core.Tests + RabbitMQ.Tests + RabbitMQ.IntegrationTests across {net8.0,net9.0,net10.0}"
  - ".github/workflows/release.yml: tag-driven (v*) NuGet.org publish pipeline with .snupkg companion verification"
  - "src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt: frozen v1.0 surface (102 lines)"
  - "src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Shipped.txt: frozen v1.0 surface (39 lines)"
  - "Both PublicAPI.Unshipped.txt: canonical empty baseline (#nullable enable only)"
  - "CHANGELOG.md: v1.0.0 date finalized to 2026-05-03"
affects:
  - ".github/workflows/build.yml (+17 lines)"
  - ".github/workflows/release.yml (NEW, 162 lines)"
  - "src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt (+101 lines)"
  - "src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt (-101 lines)"
  - "src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Shipped.txt (+39 lines)"
  - "src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt (-38 lines)"
  - "CHANGELOG.md (1 line — date)"
tech-stack:
  added: []
  patterns:
    - "Tag-triggered GitHub Actions release with concurrency group, narrow permissions (contents: read), and explicit refusal of --skip-duplicate"
    - ".snupkg companion verification step before push (catches SourceLink/IncludeSymbols regressions)"
    - "fetch-depth: 0 on checkout to give MinVer the full git history needed for tag-based version inference"
    - "PublicAPI freeze ceremony: Unshipped -> Shipped + LC_ALL=C deterministic sort"
key-files:
  created:
    - ".github/workflows/release.yml (162 lines)"
    - ".planning/phases/04-polish-v1-release/deferred-items.md (README quickstart drift carry-forward)"
  modified:
    - ".github/workflows/build.yml (+17)"
    - "src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt (1 -> 102 lines)"
    - "src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt (102 -> 1 line)"
    - "src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Shipped.txt (0 -> 39 lines)"
    - "src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt (39 -> 1 line)"
    - "CHANGELOG.md (date finalize)"
decisions:
  - "release.yml is a SEPARATE file from build.yml (not a unified workflow with conditional triggers): distinct trigger surfaces, distinct concurrency groups, distinct secret blast radius. Folding would require if: startsWith(github.ref, 'refs/tags/v') everywhere, brittle and over-broad."
  - "Coverage gate stays Core-only at 90%. RabbitMQ.Tests + RabbitMQ.IntegrationTests run BUT do not feed into the coverage gate. Per CONTEXT.md: integration tests bias coverage numbers and 90% is a Core-quality bar, not a project-wide bar."
  - "release.yml refuses --skip-duplicate on dotnet nuget push by design. Re-pushing the same version is a workflow misconfiguration that should be loud (HTTP 409), not silent."
  - "PublicAPI.Shipped.txt files are sorted with LC_ALL=C sort -u for deterministic ordering. Analyzer is set-based and order-insensitive, but committed-file ordering eliminates noisy diffs on subsequent unrelated edits."
  - "CHANGELOG date set to 2026-05-03 UTC (matches GitHub Actions runner default TZ; avoids local-vs-CI date drift). The actual NuGet.org publish day may differ when maintainer pushes the tag — Keep-a-Changelog convention treats the prep day as the release day."
metrics:
  duration: "~12 minutes"
  completed: "2026-05-03"
  tasks: 4
  commits: 3
  files_created: 2
  files_modified: 6
  build_status: "0 errors, 0 warnings, 0 RS0016, 0 RS0017"
  package_status: "2 .nupkg + 2 .snupkg produced; consumer smoke executed end-to-end"
---

# Phase 4 Plan 02: CI Evolution, Release Pipeline & v1.0 PublicAPI Freeze Summary

Crossed the v1.0 release-readiness bar. After this plan: pushing a `v1.0.0` git tag
fires `release.yml`, runs the full multi-TFM test gate (Core + RabbitMQ unit +
RabbitMQ Testcontainers integration on net8.0/net9.0/net10.0), packs both packages
with `.snupkg` companions, and pushes to NuGet.org via `secrets.NUGET_API_KEY`. The
public API surface is frozen — any future change requires explicit `PublicAPI.Unshipped.txt`
opt-in, enforced at build time by `Microsoft.CodeAnalysis.PublicApiAnalyzers`. No
production code changes; only CI/release machinery + manifest text + one date string.

## Tasks Executed

| Task | Name                                                                    | Commit    |
| ---- | ----------------------------------------------------------------------- | --------- |
| 1    | Evolve build.yml — add RabbitMQ.Tests + RabbitMQ.IntegrationTests steps | `36892e3` |
| 2    | Create release.yml — tag-driven NuGet.org publish with .snupkg + gate   | `2fe1532` |
| 3    | Promote PublicAPI Unshipped → Shipped (Core + RabbitMQ); CHANGELOG date | `99a4b68` |
| 4    | Final acceptance — local pack + simulated consumer (checkpoint)         | (no commit; verification only) |

## Verification Results

### Task 1 — build.yml step diff

Before (Phase 1):  9 steps. After: **11 steps** (added two RabbitMQ test runs).
Order preserved: `coverage gate` runs BEFORE the new RabbitMQ test steps so coverage
remains the primary quality gate; if coverage fails, RabbitMQ tests are short-circuited.

YAML parses cleanly (`python3 -c "yaml.safe_load(...)"` → `OK; Steps: 11`).

Matrix unchanged: `os: [ubuntu-latest]`, `tfm: [net8.0, net9.0, net10.0]`. Runtime
budget: ~14s × 3 TFMs ≈ 42s of integration-test wall-clock added per CI job.

### Task 2 — release.yml content

162-line workflow file. YAML schema validation:

```
on.push.tags == ['v*']                    # exclusively tag-triggered
on.push.branches not present              # NEVER fires on branches/PRs
jobs == {test, publish}                   # gate-then-pack
jobs.publish.needs == 'test'              # publish blocked until matrix is green
contents: read                            # narrow permissions
fetch-depth: 0                            # MinVer needs full history
NUGET_API_KEY referenced 2x               # in env: of push step
.snupkg referenced 7x                     # companion verification + push glob
dotnet pack src/Oragon.ElasticPool.Core   # explicit per-project pack
dotnet pack src/Oragon.ElasticPool.RabbitMQ
```

All assertions pass; no `branches:` block (verified by grep).

### Task 3 — PublicAPI freeze + CHANGELOG date

Pre-promotion line counts:
- Core Shipped: **1** → 1 line (`#nullable enable`)
- Core Unshipped: **102** → 1 header + 101 API entries
- RabbitMQ Shipped: **0** (empty) → 0 lines
- RabbitMQ Unshipped: **39** → 1 header + 38 API entries

Post-promotion line counts:
- Core Shipped: **102** ✓ (header + 101 entries, sorted LC_ALL=C)
- Core Unshipped: **1** ✓ (`#nullable enable` only)
- RabbitMQ Shipped: **39** ✓ (header + 38 entries, sorted LC_ALL=C)
- RabbitMQ Unshipped: **1** ✓ (`#nullable enable` only)

Conservation check: pre-Unshipped non-header (101 + 38 = 139) ≡ post-Shipped non-header
(101 + 38 = 139). No leakage.

Key symbols verified present in Shipped:
- `IElasticPool` (Core): 9 occurrences
- `AddElasticConnectionPool` (RabbitMQ): 1 occurrence
- `AddElasticChannelPool` (RabbitMQ): 1 occurrence

Build after promotion:
```
dotnet build Oragon.ElasticPool.sln -c Release --no-restore
ok dotnet build: 9 projects, 0 errors, 0 warnings (00:00:08.69)
```

`grep -E 'error RS001[67]:' /tmp/freeze-build.log` → 0 matches. **Freeze valid.**

CHANGELOG date: `## [1.0.0] - 2026-05-XX` → `## [1.0.0] - 2026-05-03`. No `2026-05-XX`
remaining anywhere in the file.

### Task 4 — Local pack + consumer smoke

`dotnet pack -c Release --no-build` for both packable projects produced 4 artifacts in
`/tmp/local-nuget-feed/`:

| Artifact | Bytes |
|----------|-------|
| `Oragon.ElasticPool.Core.0.0.0-alpha.0.84.nupkg` | 100,838 |
| `Oragon.ElasticPool.Core.0.0.0-alpha.0.84.snupkg` | 53,932 |
| `Oragon.ElasticPool.RabbitMQ.0.0.0-alpha.0.84.nupkg` | 44,000 |
| `Oragon.ElasticPool.RabbitMQ.0.0.0-alpha.0.84.snupkg` | 37,050 |

(Version `0.0.0-alpha.0.84` is MinVer's pre-release inference because no `v*` tag exists
locally; the `release.yml` workflow will produce `1.0.0` from a real `v1.0.0` tag.)

**.nupkg contents** (both packages):
- `lib/net8.0/*.dll`, `lib/net9.0/*.dll`, `lib/net10.0/*.dll` ✓
- `README.md` ✓ (8263 bytes for Core, 5389 for RabbitMQ — per-package, not the root)
- `icon.png` ✓ (258 bytes — 128x128 navy placeholder from Plan 01)
- `*.nuspec` ✓

**nuspec metadata** (verified for both):
```xml
<license type="expression">MIT</license>
<licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
<icon>icon.png</icon>
<readme>README.md</readme>
<projectUrl>https://github.com/oragon/Oragon.ElasticPool</projectUrl>
<repository type="git" url="https://github.com/oragon/Oragon.ElasticPool"
            branch="refs/heads/main"
            commit="99a4b68b822bef22c0c2c6d117215eef753eaed9" />
```

The `commit` SHA matches the Task 3 commit — SourceLink wiring is intact, deterministic
build is in effect, and a consumer using SourceLink-aware tooling will land at exactly
this commit when stepping into source.

**Consumer simulation** (`dotnet new console` + local feed install + run):

```
$ dotnet run
Acquired client #2; pool InUse=1 Available=1
  client #2 did work
After release: InUse=0 Available=2
  client #1 disposed
  client #2 disposed
```

Pool created 2 clients on `InitialSize=2` warm-up, returned client #2 from `AcquireAsync`,
restored both to Available=2 after lease disposal, and disposed both on `ServiceProvider`
disposal. **End-to-end consumer flow works.**

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 — Blocking] Stale obj/bin Windows-fallback path broke `dotnet build` after PublicAPI promotion**

- **Found during:** Task 3 verification (the post-promotion `dotnet build` invocation)
- **Issue:** `error MSB4018: Unable to find fallback package folder 'C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages'`. Identical symptom to Plan 04-01's deviation (Plan 01 SUMMARY documented this); cached `obj/.../project.assets.json` from a prior Windows-side restore had Windows-only fallbackFolders baked in.
- **Fix:** `find src tests samples -type d \( -name obj -o -name bin \) -exec rm -rf {} +` then `dotnet restore --no-cache --force`. Build succeeded immediately afterward.
- **Files modified:** None (cache-only).
- **Commit:** N/A (no source change).
- **Note:** Same workaround as Plan 04-01 (carry-forward note 1 there). CI runners start from clean obj/, so this is exclusively a local WSL artifact and never recurs in `build.yml` or `release.yml`. No csproj/nuget.config change needed.

**2. [Rule 3 — Blocking] Shell `grep` alias rewriting `grep -v` invocations during PublicAPI merge**

- **Found during:** Task 3 first attempt at the merge pipeline
- **Issue:** The shell environment had a `grep` proxy/alias (RTK token-killer hook) that intercepted `grep` calls and emitted match-count summaries instead of inverting matches. The merge pipeline `grep -v '^#nullable enable$' "$UNSHIP" | sort -u` produced 0 lines instead of 101.
- **Fix:** Switched to absolute binary paths (`/usr/bin/grep`, `/usr/bin/wc`) inside the merge script. After this, the merge produced the expected 101+38 entries.
- **Files modified:** Same files as planned (PublicAPI.{Shipped,Unshipped}.txt for both projects); only the script invocation changed.
- **Commit:** Folded into Task 3's commit `99a4b68`.
- **Note:** This is a local environment quirk; CI runners use plain `grep`. The `LC_ALL=C sort -u` deterministic ordering still holds because the input bytes were correct.

### Out-of-Scope Discoveries

**README quickstart API drift (logged to `.planning/phases/04-polish-v1-release/deferred-items.md`):**

The Core README (and root README) quickstart uses property-setter syntax that doesn't
exist on the (now-frozen) `ElasticPoolBuilder<T>`:

```csharp
// README quickstart says:
pool.MinSize     = 1;        // ← CS1061: no such property setter
pool.InitialSize = 2;        // ← CS1061
pool.MaxSize     = 16;       // ← CS1061
pool.IdleTimeout = TimeSpan.FromMinutes(2);  // ← CS1061
```

Actual API (per the frozen `PublicAPI.Shipped.txt`):
```csharp
pool.WithBounds(minSize: 1, maxSize: 16, initialSize: 2);
pool.IdleTimeout(TimeSpan.FromMinutes(2));
```

Additionally, the README uses `services.GetRequiredService<IElasticPool<T>>()` but
`AddElasticPool` registers a **keyed** singleton — the correct lookup is
`services.GetRequiredKeyedService<IElasticPool<T>>(name)`.

**Why deferred:** Out of scope for Plan 04-02 (CI/release/freeze); it's a docs bug, not
a code bug. The frozen public API is correct. The maintainer must fix the README before
publishing v1.0.0-rc.1 — otherwise every evaluator following the README hits a compile
error in <60 seconds, directly contradicting OSS-02 ("README converts evaluators in 60s").
Documented in `deferred-items.md` for the maintainer's pre-tag checklist.

## TDD Gate Compliance

This plan is `type: execute` (not `type: tdd`); no RED→GREEN→REFACTOR sequence required.

## Threat Flags

None. No new security-relevant surface introduced. The release.yml workflow's threat
register (T-04-06..T-04-13 in PLAN.md) is fully addressed by the implementation:
- T-04-06 (NUGET_API_KEY exposure): scoped to publish job env only ✓
- T-04-11 (workflow over-permissions): `permissions: contents: read` only ✓
- T-04-12 (--skip-duplicate blanket): rejected by design ✓

## Carry-Forward to Release Moment

**The maintainer must complete these manual steps before NuGet.org publish:**

1. **Create the public GitHub repo** at `https://github.com/oragon/Oragon.ElasticPool`
   (or update `Directory.Build.props` `RepositoryUrl` + the README links if a different
   org/slug). The current nuspec hard-codes `oragon/Oragon.ElasticPool`.

2. **Configure `NUGET_API_KEY` repo secret**:
   - Generate at https://www.nuget.org/account/apikeys (scope: Push new packages and
     package versions; Glob: `Oragon.ElasticPool.*`).
   - Add at GitHub repo Settings → Secrets and variables → Actions → New repository
     secret, name `NUGET_API_KEY`.

3. **(Recommended) Reserve `Oragon.ElasticPool.*` PackageId prefix** at
   https://www.nuget.org/account/Manage → Reserved namespaces (squatting protection;
   requires NuGet.org account verification).

4. **(Recommended) Tag protection rule**: GitHub repo Settings → Rules → restrict `v*`
   tag creation to maintainers (mitigates threat T-04-07).

5. **Fix the README quickstart drift** documented in `deferred-items.md` (3 files).

6. **Push RC tag first**: `git tag v1.0.0-rc.1 && git push origin v1.0.0-rc.1`. Watch
   `release` workflow run → verify packages on NuGet.org. **Soak ~1 week** per
   CONTEXT.md pre-release strategy. If no blocker surfaces:

7. **Push final tag**: `git tag v1.0.0 && git push origin v1.0.0`. The same
   `release.yml` workflow produces and publishes `Oragon.ElasticPool.Core.1.0.0.nupkg`
   + `.snupkg` and `Oragon.ElasticPool.RabbitMQ.1.0.0.nupkg` + `.snupkg`.

These steps are OUTSIDE the GSD execution surface — only the maintainer can perform them.

## Phase 4 Closure

All four OSS requirements that this plan owns are now SATISFIED:

| Req | Description | Evidence |
|-----|-------------|----------|
| **OSS-01** | Multi-TFM CI matrix including integration tests | `build.yml` covers Core + RabbitMQ unit + integration on {net8/9/10}; coverage gate at 90% Core preserved |
| **OSS-03** | SemVer 2.0 release pipeline (MinVer + tag-driven) | `release.yml` triggers on `v*`, MinVer infers version from tag, full test gate before publish |
| **OSS-04** | `.snupkg` + SourceLink | Both packages produce paired `.snupkg`; nuspec contains `<repository commit="...">`; verification step in `release.yml` rejects missing companions |
| **OSS-05** | PublicApiAnalyzers freeze ceremony | Both `PublicAPI.Shipped.txt` populated (102 + 39 lines); both `Unshipped.txt` reduced to baseline; `dotnet build` 0 RS0016/RS0017 |

OSS-02 (README + per-package docs + sample link + comparison table) was satisfied by
Plan 04-01 with the caveat that the README quickstart code drift (logged here) must be
fixed pre-tag.

## Self-Check: PASSED

Files claimed:
- `.github/workflows/release.yml` — FOUND (162 lines) ✓
- `.github/workflows/build.yml` — MODIFIED (+17 lines, 11 steps total) ✓
- `src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt` — 102 lines ✓
- `src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt` — 1 line baseline ✓
- `src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Shipped.txt` — 39 lines ✓
- `src/Oragon.ElasticPool.RabbitMQ/PublicAPI.Unshipped.txt` — 1 line baseline ✓
- `CHANGELOG.md` — `## [1.0.0] - 2026-05-03` ✓ (no `2026-05-XX` placeholder remains)
- `.planning/phases/04-polish-v1-release/deferred-items.md` — FOUND ✓

Commits claimed:
- `36892e3` (Task 1) — FOUND in `git log` ✓
- `2fe1532` (Task 2) — FOUND ✓
- `99a4b68` (Task 3) — FOUND ✓

Build/pack outputs (ephemeral):
- `/tmp/freeze-build.log`: `0 errors, 0 warnings, 0 RS0016, 0 RS0017` ✓
- `/tmp/local-nuget-feed/Oragon.ElasticPool.Core.0.0.0-alpha.0.84.{nupkg,snupkg}` ✓
- `/tmp/local-nuget-feed/Oragon.ElasticPool.RabbitMQ.0.0.0-alpha.0.84.{nupkg,snupkg}` ✓
- `/tmp/consumer-smoke/PoolDemo`: builds + runs end-to-end against local feed ✓
