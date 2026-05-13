---
phase: 01-core-skeleton-fixed-size-pool
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
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
autonomous: true
requirements:
  - QUAL-01
  - QUAL-02
user_setup: []

must_haves:
  truths:
    - "`dotnet --info` resolves the SDK pinned by global.json on a fresh clone"
    - "`dotnet restore Oragon.ElasticPool.sln` succeeds with zero NU* warnings (Central Package Management active)"
    - "`dotnet build Oragon.ElasticPool.sln -c Release` succeeds for net10.0, net9.0, and net8.0 targets with TreatWarningsAsErrors enabled"
    - "`dotnet test tests/Oragon.ElasticPool.Core.Tests` runs the placeholder test under Microsoft.Testing.Platform and reports 1 passed, 0 failed"
    - "`dotnet test tests/Oragon.ElasticPool.Core.Stress` runs the stress placeholder; the stress project is NOT included in the default `dotnet test` solution sweep (it must be invoked explicitly per CONTEXT.md decision)"
    - "PublicApiAnalyzers is wired with empty Shipped + Unshipped baselines on the Core project; analyzer assembly is referenced (build does not error on missing files)"
    - "GitHub Actions `build.yml` builds + tests Core.Tests across the multi-TFM matrix without invoking the Stress project"
  artifacts:
    - path: global.json
      provides: "SDK pin (10.0.x, latestFeature roll-forward) + Microsoft.Testing.Platform runner selection"
      contains: "Microsoft.Testing.Platform"
    - path: Directory.Build.props
      provides: "Repo-wide common props: SourceLink, deterministic build, snupkg, Nullable, ImplicitUsings, LangVersion=latest, TreatWarningsAsErrors"
      contains: "ContinuousIntegrationBuild"
    - path: Directory.Packages.props
      provides: "Central Package Management with all Phase 1 package versions pinned (Logging.Abstractions, DI.Abstractions, Options, PublicApiAnalyzers, MinVer, SourceLink, xunit.v3, AwesomeAssertions, NSubstitute, FakeTimeProvider, Diagnostics.Testing, coverlet)"
      contains: "ManagePackageVersionsCentrally"
    - path: src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj
      provides: "Multi-target Core library project (net10.0;net9.0;net8.0) with PackageReferences (no inline versions), PublicApiAnalyzers active, MinVer + SourceLink as PrivateAssets=all"
      contains: "TargetFrameworks"
    - path: src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt
      provides: "Empty PublicApiAnalyzers baseline (Phase 1 starts shipping nothing)"
      min_lines: 1
    - path: src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt
      provides: "Empty PublicApiAnalyzers next-release tracking file"
      min_lines: 1
    - path: tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj
      provides: "xUnit v3 + MTP test project with AwesomeAssertions, NSubstitute, FakeTimeProvider, Diagnostics.Testing, coverlet.collector, ProjectReference to Core"
      contains: "UseMicrosoftTestingPlatformRunner"
    - path: tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj
      provides: "Separate stress project (excluded from default CI sweep) with same xUnit v3 + MTP setup, ProjectReference to Core"
      contains: "UseMicrosoftTestingPlatformRunner"
    - path: Oragon.ElasticPool.sln
      provides: "Solution containing Core, Core.Tests, Core.Stress (Stress placed in a dedicated solution folder for visibility but excluded from CI script via path filter)"
      contains: "Oragon.ElasticPool.Core"
    - path: .github/workflows/build.yml
      provides: "Minimal CI: install .NET 8/9/10 SDKs, restore, build, test only Core.Tests across TFM matrix"
      contains: "actions/setup-dotnet"
  key_links:
    - from: "src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj"
      to: "Directory.Packages.props"
      via: "Central Package Management — PackageReference without Version"
      pattern: "<PackageReference Include=\"Microsoft.Extensions.Logging.Abstractions\""
    - from: "tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj"
      to: "src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj"
      via: "ProjectReference"
      pattern: "ProjectReference Include=.*Oragon.ElasticPool.Core.csproj"
    - from: ".github/workflows/build.yml"
      to: "tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj"
      via: "explicit `dotnet test tests/Oragon.ElasticPool.Core.Tests` (NOT solution-wide), keeping Stress project out of CI default per CONTEXT.md"
      pattern: "Oragon.ElasticPool.Core.Tests"
---

<objective>
Stand up the empty repository skeleton: solution, project files, central package management, common build props, an empty PublicApiAnalyzers baseline, and a minimal multi-TFM CI workflow that builds and runs unit tests but excludes the stress project. Ship a green `dotnet build` + `dotnet test` baseline that Plans 02 and 03 can immediately add code/tests into.

Purpose: Lock in all repo-level decisions (SourceLink/deterministic build, CPM, MTP runner, multi-TFM, PublicApiAnalyzers from day 1, Stress isolation from CI default) before any product code exists. These are non-retrofittable in the sense that retrofitting them after code lands triggers cascading rework (re-baselining PublicAPI, fixing TreatWarningsAsErrors regressions across an existing codebase, etc.).

Output: A buildable, testable skeleton repo. No product code, no tests of product behavior — just placeholder smoke tests that prove the toolchain works end-to-end.
</objective>

<execution_context>
@/mnt/p/dynamic-pool/.claude/get-shit-done/workflows/execute-plan.md
@/mnt/p/dynamic-pool/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/REQUIREMENTS.md
@.planning/ROADMAP.md
@.planning/research/STACK.md
@.planning/phases/01-core-skeleton-fixed-size-pool/01-CONTEXT.md
@.planning/phases/01-core-skeleton-fixed-size-pool/01-RESEARCH.md

<interfaces>
<!-- This plan creates the configuration files that Plans 02 + 03 consume. -->
<!-- Stack pins (from RESEARCH.md verified versions, May 2026): -->

Directory.Packages.props will pin (verified versions):
- Microsoft.Extensions.Logging.Abstractions = 10.0.5
- Microsoft.Extensions.DependencyInjection.Abstractions = 10.0.6
- Microsoft.Extensions.Options = 10.0.5
- Microsoft.CodeAnalysis.PublicApiAnalyzers = 3.3.4
- MinVer = 6.0.0
- Microsoft.SourceLink.GitHub = 8.0.0
- xunit.v3 = 3.2.2
- xunit.v3.runner.visualstudio = 3.2.2
- AwesomeAssertions = 9.4.0
- NSubstitute = 5.3.0
- Microsoft.Extensions.TimeProvider.Testing = 10.5.0
- Microsoft.Extensions.DependencyInjection = 10.0.5 (test-only)
- Microsoft.Extensions.Logging.Console = 10.0.5 (test-only)
- Microsoft.Extensions.Diagnostics.Testing = 10.0.5
- coverlet.collector = 6.0.4

Repository structure (locked by CONTEXT.md):
  /
  ├─ src/Oragon.ElasticPool.Core/
  ├─ tests/Oragon.ElasticPool.Core.Tests/    (CI-included)
  ├─ tests/Oragon.ElasticPool.Core.Stress/   (CI-EXCLUDED per CONTEXT.md decision)
  ├─ .github/workflows/build.yml
  ├─ Directory.Build.props
  ├─ Directory.Packages.props
  ├─ global.json
  └─ Oragon.ElasticPool.sln
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Repository configuration — global.json, .gitignore, .editorconfig, Directory.Build.props, Directory.Packages.props</name>
  <files>
    global.json,
    .gitignore,
    .editorconfig,
    Directory.Build.props,
    Directory.Packages.props
  </files>
  <action>
Create the five repo-root configuration files (no source code, no projects yet).

1. `global.json` — Pin SDK and select MTP runner (per RESEARCH.md Example 2 footer):
```json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

2. `.gitignore` — Use the standard Visual Studio / .NET gitignore (download or paste the canonical github/gitignore VisualStudio.gitignore content). Must ignore: bin/, obj/, *.user, .vs/, TestResults/, coverage/, .idea/, *.suo.

3. `.editorconfig` — Standard .NET editorconfig: 4-space indent, UTF-8, LF line endings, dotnet-format-friendly. Enable nullable reference type analysis warnings. Set `csharp_new_line_before_open_brace = all`. (A standard `dotnet new editorconfig`-generated file is acceptable.)

4. `Directory.Build.props` — Repo-wide build properties (per RESEARCH.md "Source Link / Deterministic Build Configuration" snippet):
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    <Deterministic>true</Deterministic>
    <DeterministicSourcePaths Condition="'$(CI)' == 'true'">true</DeterministicSourcePaths>
    <Authors>Oragon</Authors>
    <Company>Oragon</Company>
    <Copyright>Copyright © Oragon</Copyright>
    <RepositoryType>git</RepositoryType>
    <RepositoryUrl>https://github.com/oragon/Oragon.ElasticPool</RepositoryUrl>
  </PropertyGroup>
</Project>
```

5. `Directory.Packages.props` — Central Package Management with all Phase 1 versions:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <!-- Core library deps -->
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.6" />
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.5" />
    <PackageVersion Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="3.3.4" />
    <PackageVersion Include="MinVer" Version="6.0.0" />
    <PackageVersion Include="Microsoft.SourceLink.GitHub" Version="8.0.0" />
    <!-- Test deps -->
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.v3.runner.visualstudio" Version="3.2.2" />
    <PackageVersion Include="AwesomeAssertions" Version="9.4.0" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.5.0" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Console" Version="10.0.5" />
    <PackageVersion Include="Microsoft.Extensions.Diagnostics.Testing" Version="10.0.5" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
  </ItemGroup>
</Project>
```

Per CONTEXT.md "Decisions" section + RESEARCH.md "Standard Stack" table, all package versions above are locked decisions. Use them exactly. AwesomeAssertions (NOT FluentAssertions, NOT Shouldly — locked by CONTEXT.md).

NOTE: do NOT add `Microsoft.NET.Test.Sdk` — RESEARCH.md confirms test projects under MTP runner do not require it; xunit.v3 is self-executing.
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && test -f global.json && test -f .gitignore && test -f .editorconfig && test -f Directory.Build.props && test -f Directory.Packages.props && grep -q 'ManagePackageVersionsCentrally' Directory.Packages.props && grep -q 'AwesomeAssertions' Directory.Packages.props && grep -q 'Microsoft.Testing.Platform' global.json && grep -q 'TreatWarningsAsErrors' Directory.Build.props && grep -q 'ContinuousIntegrationBuild' Directory.Build.props && echo OK</automated>
  </verify>
  <done>All 5 root config files exist with the specified content. CPM is enabled. MTP is selected via global.json. Source Link + deterministic build properties are in Directory.Build.props. Standard .gitignore and .editorconfig are present.</done>
</task>

<task type="auto">
  <name>Task 2: Project skeletons — Core .csproj + PublicAPI baselines + both test .csproj files + placeholder tests + solution file</name>
  <files>
    src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj,
    src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt,
    src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt,
    tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj,
    tests/Oragon.ElasticPool.Core.Tests/PlaceholderSmokeTest.cs,
    tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj,
    tests/Oragon.ElasticPool.Core.Stress/PlaceholderStressFact.cs,
    Oragon.ElasticPool.sln
  </files>
  <action>
Create the three .csproj projects and a solution file. No product code — just empty Core (compiles to empty assembly) and one trivial test in each test project.

1. `src/Oragon.ElasticPool.Core/Oragon.ElasticPool.Core.csproj` — verbatim from RESEARCH.md "Example 1: Minimal Phase 1 .csproj for Core" with PublicApiAnalyzers added (RESEARCH.md Pattern: Standard Stack table requires it from day one):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
    <IsPackable>true</IsPackable>
    <PackageId>Oragon.ElasticPool.Core</PackageId>
    <Description>Generic, elastic in-process object pool for .NET with health auto-healing and built-in observability.</Description>
    <PackageTags>pool;objectpool;adaptive;elastic;async;observability;opentelemetry</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" PrivateAssets="all" />
    <PackageReference Include="MinVer" PrivateAssets="all" />
    <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
```
NOTE: Drop `<PackageReadmeFile>README.md</PackageReadmeFile>` and the README `<None Include>` for now — README is a Phase 4 artifact (per ROADMAP). Leaving the reference dangling would fail `dotnet pack`. Plan 02 brings code, Plan 03 ensures pack still works without README; Phase 4 adds README and re-enables PackageReadmeFile.

2. `src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt` — Empty baseline file. Content: a single line containing only `#nullable enable` (this is the canonical empty baseline that PublicApiAnalyzers expects).

3. `src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt` — Identical to Shipped.txt: single line `#nullable enable`. Plans 02 + 03 will populate Unshipped.txt as new public API surface lands.

4. `tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj` — verbatim from RESEARCH.md "Example 2: Phase 1 Test .csproj":
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.v3.runner.visualstudio" />
    <PackageReference Include="AwesomeAssertions" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" />
    <PackageReference Include="Microsoft.Extensions.Diagnostics.Testing" />
    <PackageReference Include="coverlet.collector" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Oragon.ElasticPool.Core\Oragon.ElasticPool.Core.csproj" />
  </ItemGroup>
</Project>
```

5. `tests/Oragon.ElasticPool.Core.Tests/PlaceholderSmokeTest.cs`:
```csharp
using AwesomeAssertions;
using Xunit;

namespace Oragon.ElasticPool.Core.Tests;

public class PlaceholderSmokeTest
{
    [Fact]
    public void Toolchain_IsWired()
    {
        // Proves: xUnit v3 + MTP + AwesomeAssertions + Core ProjectReference all link.
        // Will be deleted by Plan 02 once real tests exist.
        true.Should().BeTrue();
    }
}
```

6. `tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj` — same structure as Tests project, but stress-only deps (no Diagnostics.Testing needed yet):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net9.0;net8.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.v3.runner.visualstudio" />
    <PackageReference Include="AwesomeAssertions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Oragon.ElasticPool.Core\Oragon.ElasticPool.Core.csproj" />
  </ItemGroup>
</Project>
```

7. `tests/Oragon.ElasticPool.Core.Stress/PlaceholderStressFact.cs`:
```csharp
using AwesomeAssertions;
using Xunit;

namespace Oragon.ElasticPool.Core.Stress;

public class PlaceholderStressFact
{
    [Fact]
    public void StressProject_IsWired()
    {
        // Proves stress project compiles and runs in isolation.
        // Plan 03 replaces this with the MaxSize=1 ping-pong stress test.
        true.Should().BeTrue();
    }
}
```

8. `Oragon.ElasticPool.sln` — solution containing all three projects. Generate via `dotnet new sln -n Oragon.ElasticPool` then `dotnet sln add` for each .csproj. Keep all three in the solution (Stress is in the sln so VS shows it; CI script in Task 3 invokes Tests project explicitly to keep Stress out of CI default per CONTEXT.md).
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && dotnet restore Oragon.ElasticPool.sln 2>&1 | tee /tmp/restore.log && grep -qE '^(error|Error)' /tmp/restore.log && exit 1 || echo restore-ok && dotnet build Oragon.ElasticPool.sln -c Release --no-restore 2>&1 | tee /tmp/build.log && grep -qE 'Build succeeded' /tmp/build.log && dotnet test tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj --no-build -c Release 2>&1 | tee /tmp/test.log && grep -qE '(Passed:.*1|Passed!.*1)' /tmp/test.log && dotnet test tests/Oragon.ElasticPool.Core.Stress/Oragon.ElasticPool.Core.Stress.csproj --no-build -c Release 2>&1 | tee /tmp/stress.log && grep -qE '(Passed:.*1|Passed!.*1)' /tmp/stress.log && echo ALL-OK</automated>
  </verify>
  <done>All three projects exist and compile. Solution restores cleanly under CPM (no NU1605/NU1008/NU1010 warnings). `dotnet build` produces three target binaries (net8/net9/net10) for Core. `dotnet test` on Tests project passes the placeholder smoke test under MTP. `dotnet test` on Stress project passes the placeholder stress fact under MTP. PublicApiAnalyzers is loaded (no PublicAPI* errors fire because the empty baselines match an empty surface).</done>
</task>

<task type="auto">
  <name>Task 3: Minimal multi-TFM CI workflow (build + Core.Tests only — Stress excluded)</name>
  <files>.github/workflows/build.yml</files>
  <action>
Create a GitHub Actions workflow that proves the toolchain works in CI across the multi-TFM matrix and explicitly excludes the Stress project per CONTEXT.md decision ("stress tests: projeto separado, fora da CI default").

`.github/workflows/build.yml`:
```yaml
name: build

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

env:
  DOTNET_NOLOGO: 'true'
  DOTNET_CLI_TELEMETRY_OPTOUT: 'true'
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 'true'

jobs:
  build:
    name: build-${{ matrix.os }}-${{ matrix.tfm }}
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: false
      matrix:
        os: [ ubuntu-latest ]
        tfm: [ net8.0, net9.0, net10.0 ]
    steps:
      - uses: actions/checkout@v4

      - name: setup .NET (8.0.x, 9.0.x, 10.0.x)
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x

      - name: dotnet restore
        run: dotnet restore Oragon.ElasticPool.sln

      - name: dotnet build (Release, ${{ matrix.tfm }})
        run: dotnet build Oragon.ElasticPool.sln -c Release --no-restore -f ${{ matrix.tfm }}
        # Note: Directory.Build.props sets TreatWarningsAsErrors=true. CI sets CI=true,
        # which activates ContinuousIntegrationBuild and DeterministicSourcePaths.

      - name: dotnet test (Core.Tests only — Stress is intentionally excluded per CONTEXT.md)
        run: >
          dotnet test tests/Oragon.ElasticPool.Core.Tests/Oragon.ElasticPool.Core.Tests.csproj
          -c Release --no-build -f ${{ matrix.tfm }}
          --logger "console;verbosity=normal"
```

Hard rules baked in:
- Stress project is NEVER invoked here. Per CONTEXT.md, a dedicated nightly job comes in Phase 4. This workflow only runs Core.Tests.
- Multi-TFM matrix: net8/net9/net10. Project research confirms net9 is in the matrix until EOL transition is complete.
- `actions/setup-dotnet@v4` installs all three SDKs so multi-target build works.
- `windows-latest` is intentionally NOT included in Phase 1 to keep the Phase 1 CI minimum lean; full OS matrix is a Phase 4 OSS-01 deliverable per ROADMAP.

Optionally add a `.github/dependabot.yml` later — defer to Phase 4 (OSS-01 covers it).
  </action>
  <verify>
    <automated>cd /mnt/p/dynamic-pool && test -f .github/workflows/build.yml && grep -q 'Oragon.ElasticPool.Core.Tests' .github/workflows/build.yml && ! grep -q 'Oragon.ElasticPool.Core.Stress' .github/workflows/build.yml && grep -q 'matrix:' .github/workflows/build.yml && grep -q 'net8.0' .github/workflows/build.yml && grep -q 'net9.0' .github/workflows/build.yml && grep -q 'net10.0' .github/workflows/build.yml && echo CI-OK</automated>
  </verify>
  <done>`.github/workflows/build.yml` exists. Stress project is referenced nowhere in the workflow. The Core.Tests project IS referenced. The TFM matrix covers net8.0, net9.0, net10.0. setup-dotnet is pinned to v4 with all three SDK lines.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Developer machine → repo | Untrusted developer config could leak secrets via committed files |
| CI runner → NuGet.org | Restore pulls third-party packages; supply-chain risk |
| CI runner → GitHub artifacts | Build outputs are public on PRs |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-01-01 | Tampering | Directory.Packages.props | mitigate | Pin exact versions (no floating ranges); CPM with transitive pinning enabled prevents transitive version drift |
| T-01-02 | Information Disclosure | .gitignore | mitigate | Standard VS .gitignore excludes .vs/, *.user, secrets.json, *.pfx, appsettings.Development.json from accidental commit |
| T-01-03 | Tampering | .github/workflows/build.yml | mitigate | Pin actions to major version (`@v4`); no `pull_request_target` (only `pull_request`) so workflow runs in fork context with read-only token |
| T-01-04 | Denial of Service | CI matrix | accept | 3-TFM matrix is small; no rate-limit risk on GHA free tier for a fresh repo |
| T-01-05 | Elevation of Privilege | MinVer / SourceLink build tasks | mitigate | Both pinned to specific versions in CPM; PrivateAssets="all" prevents transitive propagation to consumers |
| T-01-06 | Repudiation | Deterministic build settings | mitigate | `Deterministic=true` + `ContinuousIntegrationBuild=true` produce reproducible binaries enabling verification of CI output |
</threat_model>

<verification>
After all 3 tasks complete, the following must hold from a clean checkout:

```bash
cd /mnt/p/dynamic-pool
dotnet restore Oragon.ElasticPool.sln                          # green
dotnet build Oragon.ElasticPool.sln -c Release --no-restore     # green, all 3 TFMs
dotnet test tests/Oragon.ElasticPool.Core.Tests --no-build -c Release  # 1 passed
dotnet test tests/Oragon.ElasticPool.Core.Stress --no-build -c Release # 1 passed (manual invocation only)
test ! -e bin && test ! -e obj                                   # no stray top-level build artifacts
```

Visual sanity:
- `Oragon.ElasticPool.sln` opens in VS / Rider listing all three projects
- `cat src/Oragon.ElasticPool.Core/PublicAPI.Shipped.txt` shows only `#nullable enable`
- `cat src/Oragon.ElasticPool.Core/PublicAPI.Unshipped.txt` shows only `#nullable enable`
- `.github/workflows/build.yml` does not mention Stress
</verification>

<success_criteria>
This plan is complete when:
- [ ] `dotnet restore Oragon.ElasticPool.sln` succeeds with no NU* errors and no inline-version warnings
- [ ] `dotnet build Oragon.ElasticPool.sln -c Release` succeeds for net10/net9/net8 with TreatWarningsAsErrors=true
- [ ] `dotnet test tests/Oragon.ElasticPool.Core.Tests` reports 1 passed, 0 failed under MTP
- [ ] `dotnet test tests/Oragon.ElasticPool.Core.Stress` reports 1 passed, 0 failed (when invoked explicitly)
- [ ] CI build.yml exists, references the multi-TFM matrix, and does NOT invoke the Stress project
- [ ] PublicApiAnalyzers is wired (build does not emit PublicAPI* warnings against the empty baselines)
- [ ] Source Link / deterministic / snupkg properties active in Directory.Build.props
- [ ] CPM active in Directory.Packages.props with all Phase 1 versions pinned
- [ ] global.json pins SDK to 10.0.100 (latestFeature) and selects Microsoft.Testing.Platform runner
- [ ] AwesomeAssertions (v9.4.0) is the assertion library — NOT FluentAssertions, NOT Shouldly
</success_criteria>

<source_coverage_audit>
## Phase 1 Sources → This Plan

This plan addresses **scaffolding** only. Pool requirements (API/HOOK/BOUND/FAIL/TELEM/DI) land in Plans 02 and 03.

**Requirements (REQ) directly addressed:**
- QUAL-01 (CancellationToken end-to-end): ENABLED by setting up the multi-TFM project that will host the implementation in Plan 02
- QUAL-02 (IDisposable + IAsyncDisposable drain): ENABLED by setting up the multi-TFM project that will host the implementation in Plan 02

(Plan 01 does not implement business behavior — it builds the floor that Plans 02 and 03 stand on.)

**Phase 1 success criteria addressed by this plan:**
- None directly. This plan creates the scaffolding required by criteria 1–6 (which are all satisfied by Plans 02 + 03 jointly).

**CONTEXT.md decisions implemented (D-XX equivalents from CONTEXT.md "Decisions" section):**
- Repository layout (src/ + tests/ + .github/workflows/) — implemented as project tree
- Project naming (`Oragon.ElasticPool.Core`, `.Core.Tests`, `.Core.Stress`) — implemented as csproj names
- Stress project separate from CI default — implemented in build.yml (Stress not invoked)
- PublicApiAnalyzers wired with empty baselines — implemented in Core.csproj + PublicAPI.*.txt
- AwesomeAssertions instead of FluentAssertions/Shouldly — pinned in Directory.Packages.props
- xUnit v3 + Microsoft.Testing.Platform — pinned in Directory.Packages.props + UseMicrosoftTestingPlatformRunner in test csproj
- FakeTimeProvider package available from day 1 — pinned in Directory.Packages.props (consumption is in Plan 03)
- Single README at repo root — DEFERRED to Phase 4 (per ROADMAP); PackageReadmeFile reference removed from Core.csproj to avoid pack failure

**RESEARCH.md patterns/constraints implemented:**
- "Standard Stack" table versions — pinned in Directory.Packages.props
- Source Link / Deterministic build settings — Directory.Build.props
- Central Package Management — Directory.Packages.props
- Recommended project structure (src/ + tests/) — implemented

**RESEARCH.md items deferred (not in scope for this plan):**
- All Pattern 1–9 implementations (delegates, builder, engine, telemetry, DI, drain) — Plan 02
- All test patterns (smoke, ping-pong, FakeTimeProvider, MetricCollector) — Plan 03
- Coverage gate (90%) — Plan 03 implements; this plan only includes coverlet.collector PackageReference

**Audit verdict:** No unplanned items. All scaffolding decisions land here; all behavior lands in Plans 02 + 03.
</source_coverage_audit>

<output>
After completion, create `.planning/phases/01-core-skeleton-fixed-size-pool/01-01-SUMMARY.md` documenting:
- The five root config files and their purpose
- The three project skeletons (Core, Tests, Stress) and how they reference Directory.Packages.props
- The CI workflow design (multi-TFM matrix, explicit Stress exclusion)
- Confirmation that AwesomeAssertions, xUnit v3, MTP, FakeTimeProvider, and PublicApiAnalyzers are wired
- Confirmation that `dotnet build` and `dotnet test` (Core.Tests) are green at the end of the plan
- Heads-up to Plan 02: PackageReadmeFile is intentionally absent until Phase 4; do not re-add it without ensuring README.md exists
</output>
