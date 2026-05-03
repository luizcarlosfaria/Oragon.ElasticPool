---
phase: 04-polish-v1-release
plan: 02
type: execute
wave: 2
depends_on: [01]
files_modified:
  - .github/workflows/build.yml
  - .github/workflows/release.yml
  - src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt
  - src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt
  - src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt
  - src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt
  - CHANGELOG.md
autonomous: false
requirements: [OSS-01, OSS-03, OSS-04, OSS-05]
tags: [oss, ci, release, github-actions, nuget-publish, publicapi-freeze, sourcelink, snupkg, minver]

must_haves:
  truths:
    - "build.yml matrix runs RabbitMQ.Tests (unit, no Docker dependency) on every TFM in addition to Core.Tests, and is green on main"
    - "build.yml runs RabbitMQ.IntegrationTests (Testcontainers — requires Docker; ubuntu-latest runners include Docker) on every TFM with `--filter-trait Category=Integration`-equivalent gating, green on main"
    - "release.yml is triggered by pushing a git tag matching `v*` (e.g., `v1.0.0`, `v1.0.0-rc.1`) and runs the full build + test matrix as a gate before pack/push"
    - "release.yml authenticates to NuGet.org via `secrets.NUGET_API_KEY` and pushes both `Oragon.AdaptivePool.Core.{ver}.nupkg` + `.snupkg` and `Oragon.AdaptivePool.RabbitMQ.{ver}.nupkg` + `.snupkg`"
    - "PublicAPI.Shipped.txt for both Core (102 lines) and RabbitMQ (39 lines) contains the v1.0 surface; PublicAPI.Unshipped.txt for both reduces to the canonical empty baseline (`#nullable enable` only)"
    - "After PublicAPI promotion, `dotnet build` succeeds with no `RS0016` (declared API not shipped) or `RS0017` (shipped API not declared) errors"
    - "CHANGELOG.md v1.0.0 entry has the placeholder date `2026-05-XX` finalized to a concrete date by this plan (committer's choice — see action)"
    - "release.yml does NOT run on branch pushes / PRs — only on `v*` tag pushes (separate workflow file from build.yml; no conflict)"
  artifacts:
    - path: ".github/workflows/build.yml"
      provides: "Evolved CI: matrix unchanged (ubuntu × {net8/9/10}), now also builds + runs RabbitMQ.Tests + RabbitMQ.IntegrationTests; coverage gate stays Core-only at 90%"
      contains: "RabbitMQ.Tests"
    - path: ".github/workflows/release.yml"
      provides: "Tag-triggered release pipeline: gate (build+test on matrix) → pack → push to NuGet.org with .snupkg"
      contains: "tags:"
    - path: "src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt"
      provides: "Frozen v1.0 Core public surface (102 lines from Unshipped + #nullable enable header)"
      min_lines: 100
    - path: "src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt"
      provides: "Empty baseline (#nullable enable only)"
      contains: "#nullable enable"
    - path: "src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt"
      provides: "Frozen v1.0 RabbitMQ public surface (39 lines)"
      min_lines: 35
    - path: "src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt"
      provides: "Empty baseline"
      contains: "#nullable enable"
  key_links:
    - from: ".github/workflows/release.yml"
      to: "secrets.NUGET_API_KEY"
      via: "env: NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}"
      pattern: "NUGET_API_KEY"
    - from: ".github/workflows/release.yml"
      to: "Directory.Build.props (MinVer + SourceLink wiring from Phase 1)"
      via: "dotnet pack — MinVer infers version from the matched git tag"
      pattern: "dotnet pack"
    - from: ".github/workflows/build.yml"
      to: "tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/"
      via: "dotnet test step — Testcontainers spins broker via Docker on ubuntu runner"
      pattern: "RabbitMQ.IntegrationTests"
    - from: "src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt"
      to: "Microsoft.CodeAnalysis.PublicApiAnalyzers"
      via: "AdditionalFiles ItemGroup in Core csproj (Phase 1 wiring)"
      pattern: "PublicAPI.Shipped"

user_setup:
  - service: nuget-org
    why: "Publish .nupkg + .snupkg to NuGet.org on tag push"
    env_vars:
      - name: NUGET_API_KEY
        source: "https://www.nuget.org/account/apikeys → Create — scope: Push new packages and package versions; Glob: Oragon.AdaptivePool.* (or two narrower keys, one per package). After creation, add to GitHub repo Settings → Secrets and variables → Actions as `NUGET_API_KEY`."
    dashboard_config:
      - task: "Reserve PackageId prefix `Oragon.AdaptivePool.*` on NuGet.org (optional but recommended for namespace squatting protection)"
        location: "https://www.nuget.org/account/Manage → Reserved namespaces (requires NuGet.org account verification)"
  - service: github-repo
    why: "release.yml runs on pushed tags; repo must exist remotely for tags to push and for SourceLink to resolve to GitHub source URLs"
    env_vars: []
    dashboard_config:
      - task: "Create public repo at https://github.com/oragon/Oragon.AdaptivePool (or update Directory.Build.props RepositoryUrl + this plan's references if a different org/name)"
        location: "GitHub → New repository"
      - task: "Add NUGET_API_KEY secret to repo Actions secrets"
        location: "Repo Settings → Secrets and variables → Actions → New repository secret"
      - task: "(Recommended) Add a branch protection rule on `main` requiring the `build` workflow to pass before merge"
        location: "Repo Settings → Branches → Branch protection rules"
---

<objective>
Evolve CI to cover the full test surface (Core + RabbitMQ unit + RabbitMQ integration on
every TFM), add a tag-triggered release workflow that publishes both packages with
symbol packages to NuGet.org, freeze the v1.0 public API by promoting `Unshipped → Shipped`
in both projects, and finalize the CHANGELOG date. After this plan, pushing a `v1.0.0`
git tag triggers a production NuGet.org release; the public API surface is locked.

Purpose: Address OSS-01 (full multi-TFM CI matrix including integration tests),
OSS-03 (SemVer 2.0 release pipeline — MinVer wiring already shipped Phase 1; this
plan adds the `dotnet nuget push` workflow), OSS-04 (`.snupkg` + SourceLink — already
wired in Directory.Build.props from Phase 1, this plan exercises it via
`dotnet pack` and pushes the resulting `.snupkg` files), and OSS-05 (PublicApiAnalyzers
freeze ceremony for v1.0).

Output: Two updated/new GitHub Actions workflow files, four updated PublicAPI text
files (two projects × Shipped+Unshipped), one updated CHANGELOG (date finalize). No
production code changes; no new libraries added. The dependency on Plan 01 is
hard: PublicAPI promotion validates against `dotnet build`, which requires the
metadata changes from Plan 01 to be in place (otherwise NuGet pack errors propagate
and obscure analyzer errors).
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

# Plan 01 of this phase — produced LICENSE, READMEs, package metadata, icon.png, CHANGELOG.
# This plan finalizes CHANGELOG date and promotes PublicAPI; otherwise independent of Plan 01 outputs at the file level except CHANGELOG.md.
@.planning/phases/04-polish-v1-release/04-01-SUMMARY.md

# Phase 1 SUMMARY — established MinVer 6.0.0 + SourceLink.GitHub 8.0.0 wiring + the existing build.yml.
@.planning/phases/01-core-skeleton-fixed-size-pool/01-SUMMARY.md

# Phase 3 SUMMARY — heads-up #2 explicitly flags this plan's CI evolution as required.
@.planning/phases/03-rabbitmq-adapter/03-SUMMARY.md

@.github/workflows/build.yml
@Directory.Build.props
@Directory.Packages.props
@src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt
@src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt
@src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt
@src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt

<interfaces>
<!-- GitHub Actions workflow contract for release.yml. -->
<!-- Trigger: tag push matching `v*`. Permissions: contents:read (no write needed
     since we don't auto-create GitHub Releases in v1.0 — manual via gh CLI or UI). -->
<!-- Required secret: NUGET_API_KEY (configured in repo Actions secrets). -->
<!-- MinVer: with no overrides, picks up the matched tag (e.g., v1.0.0) and
     sets Version=1.0.0; symbol pkg snupkg is auto-produced via Directory.Build.props
     SymbolPackageFormat=snupkg + IncludeSymbols=true. -->

<!-- xUnit v3 trait filter syntax under MTP (Microsoft.Testing.Platform):
     dotnet test ... -- --filter-trait "Category=Integration"
     The `--` separates dotnet args from MTP runner args. Phase 3 SUMMARY
     heads-up #2 confirms this is the correct CI invocation. -->

<!-- xUnit v3 + MTP CI invocation pattern from Phase 1 build.yml — keep:
     dotnet test --project <csproj> --configuration Release --no-build -f <tfm>
     For RabbitMQ.IntegrationTests, the trait filter goes after `--`:
     dotnet test --project tests/...IntegrationTests/...csproj
       --configuration Release --no-build -f net10.0
       -- --filter-trait Category=Integration -->
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Evolve build.yml to cover RabbitMQ unit + integration tests across the matrix</name>
  <files>.github/workflows/build.yml</files>
  <action>
Modify the existing `.github/workflows/build.yml` (DO NOT replace wholesale — the Phase 1 file has Core test + coverage logic that must stay verbatim). Add three new steps to the `build` job, AFTER the existing "enforce 90% line coverage on Core" step and BEFORE the "upload coverage report" step:

```yaml
      # --- Phase 4: RabbitMQ adapter unit tests (no Docker required) ---
      - name: dotnet test (RabbitMQ.Tests — unit, NSubstitute mocks, no broker)
        run: >
          dotnet test --project tests/Oragon.AdaptivePool.RabbitMQ.Tests/Oragon.AdaptivePool.RabbitMQ.Tests.csproj
          --configuration Release --no-build -f ${{ matrix.tfm }}

      # --- Phase 4: RabbitMQ adapter integration tests (Testcontainers — needs Docker) ---
      # ubuntu-latest runners include Docker by default; Testcontainers manages broker
      # lifecycle. Tests run for ~12-14s per TFM (per Phase 3 SUMMARY observed timings).
      - name: dotnet test (RabbitMQ.IntegrationTests — Testcontainers RabbitMQ 4-management)
        run: >
          dotnet test --project tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests.csproj
          --configuration Release --no-build -f ${{ matrix.tfm }}
        env:
          # Testcontainers will pull rabbitmq:4-management at first run; on warm
          # runners this is cached. Honor Docker default endpoint.
          DOCKER_HOST: unix:///var/run/docker.sock
```

**Constraints:**
- Do NOT change the matrix (`os: [ubuntu-latest]`, `tfm: [net8.0, net9.0, net10.0]`) — CONTEXT.md "Deferred Ideas" explicitly defers macOS/Windows runners to v1.1.
- Do NOT add a coverage gate for RabbitMQ tests — CONTEXT.md keeps the 90% gate Core-only. The `coverlet.console` step and the `enforce 90% line coverage on Core` step stay verbatim.
- Do NOT add `--filter-trait` for the IntegrationTests run — Phase 3's IntegrationTests project is wholly integration tests; it does NOT mix unit + integration in one assembly. Filtering would be redundant. Verify by reading `tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/` — every file should set `[Trait("Category","Integration")]` or be a fixture; no plain unit tests live there. If unit tests are found mixed in (unexpected per Phase 3 SUMMARY), add `-- --filter-trait Category=Integration` to the IntegrationTests run step.
- Do NOT modify the existing solution-level `dotnet build` step — `dotnet build Oragon.AdaptivePool.sln` already builds the test projects; `--no-build` on the test runs is correct.
- Do NOT add `if: matrix.tfm == 'net10.0'` or any matrix-narrowing condition to the new steps — OSS-01 requires full matrix coverage.

**Performance note:** Adding ~14s × 3 TFMs ≈ 42s of integration-test wall-clock per CI job. Acceptable; CI runs are not on the critical path for v1.0.

**Comment header:** Add a one-line comment above the new section:

```yaml
      # --- Phase 4: full RabbitMQ adapter test surface (unit + integration) ---
```

so future maintenance can spot the Phase 4 boundary at a glance.

After modification, the `build.yml` job step order should be:
1. checkout
2. setup .NET
3. dotnet restore
4. dotnet build (Release, matrix.tfm)
5. dotnet test (Core.Tests)
6. install coverlet.console + reportgenerator
7. collect coverage on Core.Tests
8. enforce 90% line coverage on Core
9. **NEW**: dotnet test (RabbitMQ.Tests)
10. **NEW**: dotnet test (RabbitMQ.IntegrationTests)
11. upload coverage report (artifact)

(Order of steps 9-10 vs the coverage steps is intentional: coverage gate stays self-contained and runs first; if it fails, RabbitMQ test runs are skipped. CONTEXT.md keeps the 90% Core gate as the primary quality gate.)
  </action>
  <verify>
    <automated>
test -f .github/workflows/build.yml \
  && grep -q 'RabbitMQ.Tests/Oragon.AdaptivePool.RabbitMQ.Tests.csproj' .github/workflows/build.yml \
  && grep -q 'RabbitMQ.IntegrationTests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests.csproj' .github/workflows/build.yml \
  && grep -q 'os: \[ ubuntu-latest \]' .github/workflows/build.yml \
  && grep -q 'tfm: \[ net8.0, net9.0, net10.0 \]' .github/workflows/build.yml \
  && grep -q '90%' .github/workflows/build.yml \
  && python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/build.yml')); print('OK' if 'jobs' in d and 'build' in d['jobs'] and 'steps' in d['jobs']['build'] else 'BAD')" | grep -q '^OK$' \
  && echo OK
    </automated>
  </verify>
  <done>
    `.github/workflows/build.yml` contains two new test steps (RabbitMQ.Tests +
    RabbitMQ.IntegrationTests) on the existing matrix, the coverage gate is
    untouched, the YAML parses cleanly, and a fresh push to a feature branch (or
    a manual trigger if `workflow_dispatch` is added) would exercise the full
    surface. Phase 1's coverage gate at 90% on Core remains the primary quality
    bar.
  </done>
</task>

<task type="auto">
  <name>Task 2: Create release.yml — tag-triggered build/pack/push to NuGet.org with .snupkg + SourceLink</name>
  <files>.github/workflows/release.yml</files>
  <action>
Create a new workflow file at `.github/workflows/release.yml` that mirrors `build.yml`'s build+test gate, then packs and publishes both packages with symbol packages to NuGet.org. Trigger: pushed tags matching `v*` (covers `v1.0.0`, `v1.0.0-rc.1`, `v0.x.y` pre-releases — MinVer infers prerelease semantics from tag suffix).

**Full file content** (use Write tool, paste verbatim — DO NOT use heredoc/echo):

```yaml
name: release

# Trigger: pushed tags matching the SemVer-prefixed pattern. Examples:
#   v1.0.0           → MinVer infers Version=1.0.0
#   v1.0.0-rc.1      → MinVer infers Version=1.0.0-rc.1 (prerelease)
#   v1.0.0-alpha.5   → MinVer infers Version=1.0.0-alpha.5 (prerelease)
# Branch pushes and PRs are intentionally NOT triggers here — build.yml owns those.
on:
  push:
    tags:
      - 'v*'

# Concurrency group on the tag name; prevents overlapping releases for the same tag
# in the unlikely case the tag is rewritten (e.g., manual force-push correction).
concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false

permissions:
  contents: read

env:
  DOTNET_NOLOGO: 'true'
  DOTNET_CLI_TELEMETRY_OPTOUT: 'true'
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 'true'

jobs:
  # --- Gate: full multi-TFM test sweep, identical to build.yml's surface ---
  test:
    name: test-${{ matrix.os }}-${{ matrix.tfm }}
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: true
      matrix:
        os: [ ubuntu-latest ]
        tfm: [ net8.0, net9.0, net10.0 ]
    steps:
      - uses: actions/checkout@v4
        # MinVer needs full git history + tags to compute the version from the
        # matched tag. Default fetch-depth=1 would break version inference.
        with:
          fetch-depth: 0

      - name: setup .NET (8.0.x, 9.0.x, 10.0.x)
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x

      - name: dotnet restore
        run: dotnet restore Oragon.AdaptivePool.sln

      - name: dotnet build (Release, ${{ matrix.tfm }})
        run: dotnet build Oragon.AdaptivePool.sln -c Release --no-restore -f ${{ matrix.tfm }}

      - name: dotnet test (Core.Tests)
        run: >
          dotnet test --project tests/Oragon.AdaptivePool.Core.Tests/Oragon.AdaptivePool.Core.Tests.csproj
          --configuration Release --no-build -f ${{ matrix.tfm }}

      - name: dotnet test (RabbitMQ.Tests)
        run: >
          dotnet test --project tests/Oragon.AdaptivePool.RabbitMQ.Tests/Oragon.AdaptivePool.RabbitMQ.Tests.csproj
          --configuration Release --no-build -f ${{ matrix.tfm }}

      - name: dotnet test (RabbitMQ.IntegrationTests)
        run: >
          dotnet test --project tests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests/Oragon.AdaptivePool.RabbitMQ.IntegrationTests.csproj
          --configuration Release --no-build -f ${{ matrix.tfm }}
        env:
          DOCKER_HOST: unix:///var/run/docker.sock

  # --- Pack + push: runs once after all matrix legs of `test` are green ---
  publish:
    name: pack-and-push
    needs: test
    runs-on: ubuntu-latest
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: setup .NET (8.0.x, 9.0.x, 10.0.x)
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x

      - name: dotnet restore
        run: dotnet restore Oragon.AdaptivePool.sln

      # Build all TFMs (no -f flag) so pack produces a multi-target package.
      - name: dotnet build (Release, all TFMs)
        run: dotnet build Oragon.AdaptivePool.sln -c Release --no-restore

      # Pack only the two packable projects. IsPackable=true is set per Phase 1
      # convention only on Core and RabbitMQ csproj; pack on the .sln would also
      # work but explicit per-project is faster and clearer in the log.
      - name: dotnet pack — Core
        run: >
          dotnet pack src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj
          --configuration Release --no-build
          --output ./artifacts

      - name: dotnet pack — RabbitMQ
        run: >
          dotnet pack src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj
          --configuration Release --no-build
          --output ./artifacts

      - name: list produced artifacts
        run: ls -la ./artifacts

      # Sanity: every .nupkg must have a co-located .snupkg (SymbolPackageFormat=snupkg
      # in Directory.Build.props guarantees this when IncludeSymbols=true). Fail the
      # release if any .nupkg lacks a paired .snupkg — that means SourceLink wiring
      # silently regressed.
      - name: verify .snupkg companions
        shell: bash
        run: |
          set -euo pipefail
          missing=0
          for nupkg in ./artifacts/*.nupkg; do
            snupkg="${nupkg%.nupkg}.snupkg"
            if [ ! -f "$snupkg" ]; then
              echo "::error::Missing symbol package for $nupkg (expected $snupkg)"
              missing=$((missing+1))
            fi
          done
          if [ $missing -gt 0 ]; then
            echo "::error::$missing .nupkg(s) lack .snupkg companions"
            exit 1
          fi
          echo "All .nupkg files have .snupkg companions."

      - name: upload artifacts (debugging aid for failed pushes)
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: nuget-packages
          path: ./artifacts/*.*
          if-no-files-found: error
          retention-days: 14

      # Push every .nupkg in artifacts/. NuGet picks up the matching .snupkg
      # automatically when both files share a basename.
      # --skip-duplicate is a safety net: if a tag is somehow re-released and the
      # exact version is already on NuGet.org, the push fails with HTTP 409. We
      # treat that as a fatal error here (NOT --skip-duplicate'd) because re-tagging
      # the same version is a workflow error that should be loud, not silent.
      - name: dotnet nuget push (Oragon.AdaptivePool.Core + RabbitMQ to NuGet.org)
        env:
          NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
        run: >
          dotnet nuget push "./artifacts/*.nupkg"
          --api-key "$NUGET_API_KEY"
          --source https://api.nuget.org/v3/index.json
```

**Why a separate file, not folded into build.yml:**
- Distinct trigger surfaces (tag push vs. branch/PR push) — folding requires a giant `if: startsWith(github.ref, 'refs/tags/v')` smear across every step, which is brittle.
- Distinct concurrency groups — release should NOT cancel an in-progress branch CI run, and vice versa.
- Distinct secret blast radius — `NUGET_API_KEY` is referenced ONLY in release.yml. Folded, every PR run would have the key in scope (but `secrets` aren't actually exposed to PR runs from forks; still, defense in depth).

**MinVer behavior verification:** With Phase 1's MinVer 6.0.0 wired in `Directory.Build.props` (no `MinVerTagPrefix` configured — defaults to no prefix), MinVer matches tags `v1.0.0` by stripping the `v` and using `1.0.0`. **Confirm by reading Directory.Build.props at execute time** — if `MinVerTagPrefix` is set to anything other than empty/v, adjust the tag pattern in this workflow accordingly. Phase 1 SUMMARY says MinVer is wired with defaults, so `v*` tags should produce versions like `1.0.0` directly. If the very first `dotnet pack` log shows `Version=0.0.0-alpha.0.X` for a tag pushed as `v1.0.0`, the fix is to add `<MinVerTagPrefix>v</MinVerTagPrefix>` to Directory.Build.props (a one-line change deferred to release-time troubleshooting if needed; not pre-emptively in this plan because Phase 1 didn't specify it and convention with MinVer 6.x is `v` prefix detection at runtime).

**WARNING — destructive precaution:** This workflow performs a public, irreversible push to NuGet.org. It MUST NEVER trigger on a branch push by accident. The trigger pattern is `tags: ['v*']` ONLY — no `branches` block. Verify after writing.
  </action>
  <verify>
    <automated>
test -f .github/workflows/release.yml \
  && python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/release.yml')); \
     assert 'on' in d, 'missing on'; \
     assert 'push' in d['on'], 'missing on.push'; \
     assert 'tags' in d['on']['push'], 'missing on.push.tags'; \
     assert d['on']['push']['tags'] == ['v*'], f'tags must be exactly [v*], got {d[\"on\"][\"push\"][\"tags\"]}'; \
     assert 'branches' not in d['on']['push'], 'release.yml MUST NOT trigger on branches'; \
     assert 'jobs' in d, 'missing jobs'; \
     assert 'test' in d['jobs'] and 'publish' in d['jobs'], 'missing test/publish jobs'; \
     assert d['jobs']['publish'].get('needs') == 'test', 'publish must depend on test'; \
     print('OK')" \
  && grep -q 'NUGET_API_KEY' .github/workflows/release.yml \
  && grep -q 'dotnet nuget push' .github/workflows/release.yml \
  && grep -q '\.snupkg' .github/workflows/release.yml \
  && grep -q 'fetch-depth: 0' .github/workflows/release.yml \
  && grep -q 'dotnet pack src/Oragon.AdaptivePool.Core' .github/workflows/release.yml \
  && grep -q 'dotnet pack src/Oragon.AdaptivePool.RabbitMQ' .github/workflows/release.yml \
  && ! grep -q '^  branches:' .github/workflows/release.yml \
  && echo OK
    </automated>
  </verify>
  <done>
    `.github/workflows/release.yml` exists; YAML parses; trigger is exclusively
    `push.tags: ['v*']`; `test` job mirrors build.yml's matrix surface plus
    RabbitMQ unit + integration tests; `publish` job needs(`test`), packs both
    packable projects, verifies .snupkg companions, and runs
    `dotnet nuget push` with `NUGET_API_KEY` secret. The workflow is dormant
    until a tag is pushed.
  </done>
</task>

<task type="auto">
  <name>Task 3: Promote PublicAPI Unshipped → Shipped for both packable projects + finalize CHANGELOG date</name>
  <files>
    src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt,
    src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt,
    src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt,
    src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt,
    CHANGELOG.md
  </files>
  <action>
The v1.0 freeze ceremony. After this task, any future change to the public API surface of either package will be REJECTED at build time (RS0016 / RS0017 errors) until the developer either updates `PublicAPI.Unshipped.txt` (additive) or breaks compatibility intentionally and bumps the major version.

**Promotion process for EACH project (Core, then RabbitMQ):**

1. **Read** `PublicAPI.Shipped.txt` (currently 1 line: `#nullable enable`) and `PublicAPI.Unshipped.txt`.
2. **Merge**: Build the new `PublicAPI.Shipped.txt` content as:
   - First line: `#nullable enable`
   - Followed by EVERY non-header line from `PublicAPI.Unshipped.txt` (i.e., everything except the leading `#nullable enable` line)
   - Sort the merged body lines alphabetically (canonical form for analyzer; matches what `dotnet build /t:PublicApiAnalyzers` would auto-generate). Use a stable, locale-independent sort: `LC_ALL=C sort`.
3. **Reset Unshipped**: Overwrite `PublicAPI.Unshipped.txt` with EXACTLY one line: `#nullable enable` followed by a single trailing newline. This is the canonical empty baseline.

**Concrete commands** (executable from repo root):

```bash
# --- Core ---
SHIP_CORE=src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt
UNSHIP_CORE=src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt

# Sanity: count current Unshipped (must be ~102 per Phase 3 SUMMARY)
LC_ALL=C wc -l "$UNSHIP_CORE"

# Build new Shipped: header + sorted body of (current Shipped sans header) + (Unshipped sans header)
{
  echo '#nullable enable'
  {
    grep -v '^#nullable enable$' "$SHIP_CORE" || true
    grep -v '^#nullable enable$' "$UNSHIP_CORE" || true
  } | grep -v '^$' | LC_ALL=C sort -u
} > "$SHIP_CORE.new"
mv "$SHIP_CORE.new" "$SHIP_CORE"

# Reset Unshipped
printf '#nullable enable\n' > "$UNSHIP_CORE"

# Sanity
LC_ALL=C wc -l "$SHIP_CORE" "$UNSHIP_CORE"
# Expect Shipped ≥ 100 lines, Unshipped == 1

# --- RabbitMQ ---
SHIP_RMQ=src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt
UNSHIP_RMQ=src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt

LC_ALL=C wc -l "$UNSHIP_RMQ"  # expect ~39

{
  echo '#nullable enable'
  {
    grep -v '^#nullable enable$' "$SHIP_RMQ" || true
    grep -v '^#nullable enable$' "$UNSHIP_RMQ" || true
  } | grep -v '^$' | LC_ALL=C sort -u
} > "$SHIP_RMQ.new"
mv "$SHIP_RMQ.new" "$SHIP_RMQ"

printf '#nullable enable\n' > "$UNSHIP_RMQ"

LC_ALL=C wc -l "$SHIP_RMQ" "$UNSHIP_RMQ"
# Expect Shipped ≥ 35 lines, Unshipped == 1
```

Notes:
- `sort -u` deduplicates accidentally; in correct state there are no dups, so `-u` is harmless.
- `LC_ALL=C` is critical — locale-sensitive sort is non-deterministic across CI/local envs. Analyzer doesn't care about order at all (it set-compares), but the file is committed to git, so deterministic ordering eliminates noisy diffs on subsequent unrelated edits.

**Compile gate (the cerimonial moment):** After both files are updated, run:

```bash
dotnet build Oragon.AdaptivePool.sln -c Release
```

Acceptance: build is GREEN. Specifically:
- No `RS0016` (Symbol declared in source but not part of declared API).
- No `RS0017` (Symbol part of declared API but not declared in source).

If RS0016 fires, the Unshipped baseline was incomplete (a public symbol exists in code that wasn't tracked). Add the missing line(s) to `PublicAPI.Shipped.txt` and re-run.
If RS0017 fires, a previously-declared API symbol no longer exists in source (someone deleted public surface). For a v1.0 freeze, this means the Unshipped → Shipped merge captured something stale; re-derive Shipped from JUST the source-of-truth Unshipped (since Phase 1 left Shipped empty).

**Empirical check before promotion (recommended):** Run `dotnet build` on the unmodified files first. If it's green, then Unshipped is the authoritative source for the merge. If it's red, fix the Unshipped baseline first (out-of-scope for this plan; signals a Phase 3 regression).

**3. Finalize CHANGELOG.md date:**

Plan 01 wrote `## [1.0.0] - 2026-05-XX` as a placeholder. Replace `2026-05-XX` with the actual date when this plan executes (today, in the executor's timezone). Use:

```bash
TODAY=$(date -u +%Y-%m-%d)
sed -i "s/^## \[1.0.0\] - 2026-05-XX$/## [1.0.0] - $TODAY/" CHANGELOG.md
grep -q "^## \[1.0.0\] - $TODAY$" CHANGELOG.md  # verify
```

(Use UTC via `-u` to match GitHub Actions runner default timezone, avoiding a TZ-dependent date drift between local and CI.)

This date represents "the day the v1.0 release was prepared" — the actual NuGet.org publish date may differ by hours when the maintainer pushes the `v1.0.0` tag. That's acceptable; CHANGELOG dates are by SemVer convention "the day of the release", and the prep day is close enough (typically same day).

**No production code is modified.** Only text manifests + one date string.
  </action>
  <verify>
    <automated>
# Shipped contents
test "$(LC_ALL=C wc -l < src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt)" -ge 100 \
  && test "$(LC_ALL=C wc -l < src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt)" -ge 35 \
  && head -1 src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt | grep -q '^#nullable enable$' \
  && head -1 src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt | grep -q '^#nullable enable$' \
  && grep -q 'IAdaptivePool' src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt \
  && grep -q 'AddAdaptiveConnectionPool' src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt \
  && grep -q 'AddAdaptiveChannelPool' src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt \
# Unshipped is exactly the empty baseline (1 line, #nullable enable)
  && test "$(wc -l < src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt)" -eq 1 \
  && test "$(wc -l < src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt)" -eq 1 \
  && grep -qx '#nullable enable' src/Oragon.AdaptivePool.Core/PublicAPI.Unshipped.txt \
  && grep -qx '#nullable enable' src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Unshipped.txt \
# CHANGELOG date finalized (no longer placeholder)
  && ! grep -q '2026-05-XX' CHANGELOG.md \
  && grep -qE '^## \[1\.0\.0\] - 2026-[0-9]{2}-[0-9]{2}$' CHANGELOG.md \
# Build is green with frozen API surface — no RS0016 / RS0017
  && dotnet build Oragon.AdaptivePool.sln -c Release 2>&1 | tee /tmp/freeze-build.log \
  && ! grep -E 'error RS001[67]:' /tmp/freeze-build.log \
  && (grep -E '(Build succeeded|Compilação com êxito)' /tmp/freeze-build.log || grep -E '0 Error' /tmp/freeze-build.log) \
  && echo OK
    </automated>
  </verify>
  <done>
    Both PublicAPI.Shipped.txt files contain the canonical v1.0 surface (sorted,
    deduplicated, headed by `#nullable enable`); both PublicAPI.Unshipped.txt
    files reduced to the empty baseline; CHANGELOG.md v1.0.0 date is finalized;
    `dotnet build` is GREEN with no RS0016/RS0017 errors. The public API is
    officially frozen for v1.0; future additions require explicit Unshipped.txt
    updates.
  </done>
</task>

<task type="checkpoint:human-verify" gate="blocking">
  <name>Task 4: Final acceptance — clean clone build/pack + simulated consumer install</name>
  <what-built>
    Plan 01 + Plan 02 in combination produce the v1.0-ready repository state:
    multi-TFM CI covering Core + RabbitMQ unit + integration tests; tag-triggered
    release.yml ready to push to NuGet.org; PublicAPI surface frozen; per-package
    READMEs + LICENSE + CHANGELOG + icon all wired into the .nupkg artifacts.
  </what-built>
  <how-to-verify>
This is the human-gated final acceptance for OSS-01..05 success criterion #5
("Consumer following README quickstart can install both packages from NuGet.org,
write a 20-line bursty publisher, and observe pool metrics in Aspire/OTel
collector with no extra config"). Because actual NuGet.org publish requires a
real GitHub repo + secret + tag push (out of scope for the executor — that's
the maintainer's release moment), the verification SIMULATES the consumer flow
locally using a local NuGet feed. The maintainer/user runs through this script
and approves once they observe the expected behavior.

**Step-by-step (executor prepares; user runs):**

1. **Clean repo build, pack, and stage local feed** (executor automates):

```bash
# From repo root
rm -rf /tmp/local-nuget-feed && mkdir -p /tmp/local-nuget-feed
dotnet build Oragon.AdaptivePool.sln -c Release
dotnet pack src/Oragon.AdaptivePool.Core/Oragon.AdaptivePool.Core.csproj \
  -c Release --no-build -o /tmp/local-nuget-feed
dotnet pack src/Oragon.AdaptivePool.RabbitMQ/Oragon.AdaptivePool.RabbitMQ.csproj \
  -c Release --no-build -o /tmp/local-nuget-feed
ls -la /tmp/local-nuget-feed
# Expect: 2 × .nupkg + 2 × .snupkg

# Confirm package contents
for pkg in /tmp/local-nuget-feed/*.nupkg; do
  echo "=== $pkg ==="
  unzip -l "$pkg" | grep -E 'README\.md|icon\.png|\.dll|\.nuspec'
done
```

2. **Inspect SourceLink metadata in a built DLL** (executor):

```bash
# Pick one DLL from one TFM
DLL=$(find src/Oragon.AdaptivePool.Core/bin/Release/net10.0 -name 'Oragon.AdaptivePool.Core.dll' | head -1)
# SourceLink section in PE/COFF debug info — verified via dotnet-symbol or by
# extracting the .snupkg PDB and running dotnet-pdb2pdb / sourcelink test.
# Quick smoke: nuspec includes <repository ... commit="..." />:
unzip -p /tmp/local-nuget-feed/Oragon.AdaptivePool.Core.*.nupkg '*.nuspec' | \
  grep -E '(repository|projectUrl|readme|icon|license)'
```

Expected nuspec fields (sample):
```xml
<repository type="git" url="https://github.com/oragon/Oragon.AdaptivePool" commit="<sha>" />
<projectUrl>https://github.com/oragon/Oragon.AdaptivePool</projectUrl>
<license type="expression">MIT</license>
<readme>README.md</readme>
<icon>icon.png</icon>
```

3. **Simulated consumer install** (USER runs — this is the human verification):

```bash
mkdir -p /tmp/consumer-smoke && cd /tmp/consumer-smoke
dotnet new console -n PoolDemo --framework net10.0
cd PoolDemo
# Add the local feed
dotnet nuget add source /tmp/local-nuget-feed -n local-adaptive-pool || \
  dotnet nuget update source local-adaptive-pool --source /tmp/local-nuget-feed
dotnet add package Oragon.AdaptivePool.Core --source /tmp/local-nuget-feed --prerelease
dotnet add package Oragon.AdaptivePool.RabbitMQ --source /tmp/local-nuget-feed --prerelease

# Replace Program.cs with the README quickstart (Core variant — lowest barrier)
cat > Program.cs <<'EOF'
using Microsoft.Extensions.DependencyInjection;
using Oragon.AdaptivePool.Core.Abstractions;
using Oragon.AdaptivePool.Core.DependencyInjection;

var services = new ServiceCollection();
services.AddAdaptivePool<MyClient>("demo", pool =>
{
    pool.MinSize = 1; pool.InitialSize = 2; pool.MaxSize = 8;
    pool.IdleTimeout = TimeSpan.FromMinutes(1);
    pool.Factory((sp, ct) => ValueTask.FromResult(new MyClient()));
    pool.Release((c, ct) => { c.Dispose(); return ValueTask.CompletedTask; });
});

await using var sp = services.BuildServiceProvider();
var pool = sp.GetRequiredService<IAdaptivePool<MyClient>>();
await pool.ReadyAsync();

await using (var lease = await pool.AcquireAsync())
{
    Console.WriteLine($"Acquired client #{lease.Value.Id}; pool InUse={pool.InUse} Available={pool.Available}");
    lease.Value.DoWork();
}
Console.WriteLine($"After release: InUse={pool.InUse} Available={pool.Available}");

public sealed class MyClient : IDisposable
{
    private static int _ctr;
    public int Id { get; } = Interlocked.Increment(ref _ctr);
    public void DoWork() => Console.WriteLine($"  client #{Id} did work");
    public void Dispose() => Console.WriteLine($"  client #{Id} disposed");
}
EOF
dotnet run
# Expected stdout (something like):
#   Acquired client #1; pool InUse=1 Available=1
#     client #1 did work
#   After release: InUse=0 Available=2
```

4. **(Optional) Step-into debugging** (USER, only if Visual Studio / VS Code is available):
   - Open the `PoolDemo` project, set a breakpoint inside the `using` block on `lease.Value`.
   - F11 (Step Into) on `pool.AcquireAsync(...)` — IDE should fetch source from
     GitHub via SourceLink (when the repo is public). For LOCAL `local-nuget-feed`
     packages without a public commit, SourceLink falls back gracefully (debugger
     shows "Source not available" rather than crashing — that's expected here).
   - Real consumers installing from NuGet.org **after** Plan 02's release.yml
     publishes will get full step-into.

5. **`build.yml` smoke** (USER, optional):
   - Push to a feature branch and observe the GitHub Actions `build` workflow
     run. Confirm 9 leg passes (3 TFMs × 3 test runs each: Core.Tests,
     RabbitMQ.Tests, RabbitMQ.IntegrationTests). Phase 3 SUMMARY's empirical
     timings predict ~2-3 min per leg.

6. **`release.yml` dry-run** (USER, ONLY when ready to actually publish):
   - Verify `NUGET_API_KEY` secret is configured in repo Settings.
   - `git tag v1.0.0-rc.1 && git push origin v1.0.0-rc.1` (RC first per CONTEXT.md
     pre-release strategy).
   - Watch the `release` workflow run. On success, the package is on NuGet.org.
   - **Do NOT push `v1.0.0` until RC has soaked ~1 week per CONTEXT.md.**

**What to look for:**
- Step 1: 4 files in /tmp/local-nuget-feed (2 .nupkg + 2 .snupkg).
- Step 2: nuspec contains all 5 expected fields (repository commit, projectUrl, license, readme, icon).
- Step 3: `dotnet run` output shows the pool acquired and released a client cleanly. NO build errors. NO missing-package errors.
- Step 4 (optional): IDE F11 either shows source (NuGet.org installed) or "Source not available" gracefully (local feed). Crashes are bugs.

**What would fail this checkpoint:**
- `dotnet pack` produces .nupkg without .snupkg companion → SourceLink/IncludeSymbols regression in Directory.Build.props.
- nuspec lacks `<repository commit="...">` → SourceLink-driven nuspec generation broke.
- Consumer's `dotnet add package` resolves but `dotnet build` fails → public surface mismatch (PublicAPI.Shipped.txt missing a symbol the consumer code uses).
- Consumer's `dotnet run` throws → API doc drifted from actual signatures (README quickstart bug).
  </how-to-verify>
  <resume-signal>
    Type "approved" if all six steps produce expected output. Type "issues: <description>"
    with specifics if any step fails — the planner will revise based on the failure
    surface. Per CONTEXT.md, the actual `git tag v1.0.0` push is the MAINTAINER's
    decision moment after RC soak; this checkpoint approves the release MACHINERY,
    not the v1.0 tag itself.
  </resume-signal>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| GitHub Actions runner ← `secrets.NUGET_API_KEY` | Secret is exposed only in env of the `dotnet nuget push` step, only on tag-triggered runs (`release.yml`). PR runs and branch pushes (`build.yml`) cannot read this secret. |
| GitHub Actions runner → NuGet.org | TLS-protected push to `https://api.nuget.org/v3/index.json`. NuGet.org authenticates via API key + applies its own server-side signing on accepted packages. |
| Pushed tag → MinVer-inferred version | Anyone with push access to `main` and tag-create permission can trigger a release. Mitigated by GitHub branch protection + tag protection rules (recommended in user_setup but not enforced by this workflow). |
| Public NuGet consumers ← published `.snupkg` | Symbol packages are public OSS artifacts; consumers can step-into source on GitHub via SourceLink. No untrusted input flows back to maintainers. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-04-06 | Spoofing | NUGET_API_KEY exposure | mitigate | Key referenced ONLY in release.yml's `publish` job env; release.yml only triggers on `v*` tag pushes (verified by Task 2 YAML check that no `branches:` block exists). PR runs from forks cannot read repo secrets per GitHub default. |
| T-04-07 | Tampering | release.yml triggered by hostile tag | mitigate | Recommend GitHub tag protection rule restricting `v*` tag creation to maintainers (in user_setup as `dashboard_config` task). Without it, anyone with push access can ship; this is a known OSS tradeoff. v1.0 ships without enforcement; v1.1+ adds OSS Scorecard / SLSA per CONTEXT.md "Deferred Ideas". |
| T-04-08 | Repudiation | Tag-driven release vs. CHANGELOG | mitigate | release.yml records the commit SHA in the nuspec (`<repository commit="...">`); CHANGELOG.md ties version to date; git history provides authoritative provenance. v1.0 does not auto-create GitHub Releases (manual via `gh release create` post-publish per CONTEXT.md). |
| T-04-09 | Information disclosure | SourceLink → public GitHub source | accept | Source is intentionally public (OSS). SourceLink only resolves to commit SHAs that are reachable from the published tag — no leak of unmerged branches. |
| T-04-10 | Denial of service | NuGet.org push rate limits / quotas | accept | NuGet.org imposes per-API-key quotas; v1.0 publish is two packages × one version, well below any conceivable rate limit. Hostile tag spam by an account with push access is a privilege-escalation attack (T-04-07), not a DoS. |
| T-04-11 | Elevation of privilege | Workflow `permissions:` over-scoped | mitigate | release.yml declares `permissions: contents: read` only — workflow cannot create releases, write to issues, or modify code. Push to NuGet.org is via API key, not GitHub token. |
| T-04-12 | Tampering | `--skip-duplicate` blanket on push | mitigate | Plan explicitly rejects `--skip-duplicate` flag — re-pushing the same version is treated as a fatal error so the maintainer notices a workflow misconfiguration loudly. |
| T-04-13 | Information disclosure | Integration test broker credentials | accept | Testcontainers manages its own broker lifecycle with auto-generated credentials; tests use ephemeral Docker containers. No secrets cross the workflow boundary. |
</threat_model>

<verification>
## Plan-level acceptance

After all 4 tasks (3 auto + 1 checkpoint) complete:

1. CI surface (build.yml): 3 TFMs × 3 test projects (Core.Tests + RabbitMQ.Tests + RabbitMQ.IntegrationTests) = 9 test runs per push to main. Coverage gate at 90% on Core unchanged. (Verified by Task 1's YAML grep + Task 4 step 5 optional smoke.)
2. Release pipeline (release.yml): Triggers exclusively on `v*` tags; runs full test gate; packs both projects; verifies `.snupkg` companions; pushes to NuGet.org with `secrets.NUGET_API_KEY`. (Verified by Task 2's YAML schema check + Task 4 step 6 optional dry-run.)
3. PublicAPI freeze: Both Shipped.txt files contain the v1.0 surface; both Unshipped.txt are empty baselines; build is green with no RS0016/RS0017. (Verified by Task 3 automated check.)
4. CHANGELOG date finalized; root + per-package metadata coherent. (Verified by Task 3 grep + Task 4 step 2 nuspec inspection.)
5. Local consumer simulation completes successfully: `dotnet add package` from local feed, `dotnet run` of README quickstart produces expected output. (Verified by Task 4 step 3, USER-driven.)

## Post-conditions

After Plan 02 lands and the human checkpoint approves:
- **OSS-01 SATISFIED**: full `ubuntu-latest × {net8.0, net9.0, net10.0}` matrix runs unit + stress (Phase 1 carry-forward as separate nightly per CONTEXT.md note in build.yml comments) + integration tests, green on main.
- **OSS-03 SATISFIED**: MinVer + tag-driven SemVer + CHANGELOG (Keep-a-Changelog) + release.yml all wired. Pushing `v1.0.0` produces exactly `Oragon.AdaptivePool.Core.1.0.0.nupkg` + `.snupkg` (and same for RabbitMQ).
- **OSS-04 SATISFIED**: `.snupkg` companion verified per pack; SourceLink wiring already from Phase 1; `dotnet nuget push` ships both `.nupkg` and `.snupkg` via the wildcard glob.
- **OSS-05 SATISFIED**: PublicApiAnalyzers freeze ceremony complete; v1.0 surface locked.
- **OSS-02 SATISFIED** (cross-plan): per-package + root READMEs from Plan 01 + sample link + OTel example + comparison table + ship via NuGet metadata wired in this plan's release.yml.

The actual `git tag v1.0.0` push is the maintainer's decision; this plan delivers
the machinery and proves it locally. The 1-week RC soak (CONTEXT.md pre-release
strategy) starts when the maintainer pushes `v1.0.0-rc.1`.
</verification>

<success_criteria>
1. `.github/workflows/build.yml` contains the two new RabbitMQ test steps in the right order; YAML is valid.
2. `.github/workflows/release.yml` exists; triggers on `tags: ['v*']` ONLY (no branches); has `test` (matrix) and `publish` (needs: test) jobs; references `secrets.NUGET_API_KEY`; verifies `.snupkg` companions; runs `dotnet nuget push`. YAML is valid.
3. `src/Oragon.AdaptivePool.Core/PublicAPI.Shipped.txt`: ≥100 lines, sorted, headed by `#nullable enable`. Unshipped: exactly `#nullable enable\n`.
4. `src/Oragon.AdaptivePool.RabbitMQ/PublicAPI.Shipped.txt`: ≥35 lines, sorted, headed by `#nullable enable`. Unshipped: exactly `#nullable enable\n`.
5. `dotnet build Oragon.AdaptivePool.sln -c Release` is GREEN with no RS0016/RS0017 errors.
6. `CHANGELOG.md` has `## [1.0.0] - YYYY-MM-DD` with a real date (no `2026-05-XX` placeholder remaining).
7. `dotnet pack` produces .nupkg containing README.md + icon.png + valid nuspec; `.snupkg` companion exists for each .nupkg.
8. (Checkpoint-approved) Consumer simulation: `dotnet add package Oragon.AdaptivePool.Core` from local feed → `dotnet run` of README quickstart produces expected output without errors.
</success_criteria>

<output>
After completion, create `.planning/phases/04-polish-v1-release/04-02-SUMMARY.md` documenting:
- Final state of `build.yml` (step diff vs. Phase 1's version)
- Full content of new `release.yml` with annotations for design decisions (separate file, concurrency group, fetch-depth=0, .snupkg verification, no `--skip-duplicate`, narrow permissions)
- Pre-promotion line counts of PublicAPI.Unshipped (Core + RabbitMQ) and post-promotion line counts of Shipped (must increase by exactly the pre-promotion non-header line counts)
- Actual finalized CHANGELOG date
- Output of the `dotnet build` after PublicAPI promotion (must show 0 errors, 0 RS0016, 0 RS0017)
- Output of `dotnet pack` for both projects: file sizes, content listings (verify README.md + icon.png present)
- nuspec inspection: extracted XML showing `<repository commit="...">`, `<projectUrl>`, `<license type="expression">MIT</license>`, `<readme>README.md</readme>`, `<icon>icon.png</icon>`, refined `<description>` and `<tags>`
- USER feedback on the checkpoint (Task 4) — approval, identified issues, or course corrections
- Carry-forward to release moment: confirmation that the maintainer must (a) create the GitHub repo if not already public, (b) configure NUGET_API_KEY secret, (c) push `v1.0.0-rc.1` first, (d) soak ~1 week, (e) push `v1.0.0` after no blockers surface — these steps are OUTSIDE the GSD execution but documented for the maintainer
- Phase 4 closure: all five OSS-XX requirements (OSS-01..05) marked SATISFIED in REQUIREMENTS.md / ROADMAP.md
</output>
