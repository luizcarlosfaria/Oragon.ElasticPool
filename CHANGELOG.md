# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Synchronous overloads for every builder hook.** `AdaptivePoolBuilder<T>.Factory`, `BeforeUse`, `Check`, `AfterUse`, and `Release` now accept either an asynchronous delegate (existing behavior) or a synchronous delegate with the same name. Sync overloads wrap the result in a completed `ValueTask`/`ValueTask<T>` internally — zero allocation on the fast path. Hooks can be mixed freely (e.g., async `Factory` + sync `BeforeUse` + sync `Release`). Five new public delegate types: `FactorySyncDelegate<T>`, `BeforeUseSyncDelegate<T>`, `CheckSyncDelegate<T>`, `AfterUseSyncDelegate<T>`, `ReleaseSyncDelegate<T>`.

### Changed

- **Fail-fast `ArgumentNullException` for hook-builder methods.** `AdaptivePoolBuilder<T>.BeforeUse`, `Check`, `AfterUse`, and `Release` (asynchronous overloads) now throw `ArgumentNullException` when a `null` delegate is passed. Previously they silently stored `null`, deferring the failure to a later `NullReferenceException` deep inside the engine. `Factory` already failed fast; this aligns the other four hooks with that behavior.
- **RabbitMQ adapter cleaned up via the new sync overloads.** `BeforeUse` and `Check` of the connection pool and channel pool no longer wrap pure `IsOpen` checks in `ValueTask.FromResult(...)` — they use the new synchronous overloads directly. `Factory` and `Release` continue to be async (genuine I/O via `CreateConnectionAsync` / `CloseAsync` / `DisposeAsync`).

- **Test stack migrated to 100% OSS / pure MTP**: removed VSTest-only dependencies (`xunit.runner.visualstudio`, `coverlet.collector`, `Microsoft.NET.Test.Sdk` was never present), replaced `NSubstitute` with `Moq` (BSD-3, community standard, 4.20.72+ to skip the SponsorLink controversy of 4.20.0–4.20.1), pinned `Microsoft.Testing.Extensions.TrxReport` 1.9.1 for MTP-native TRX reports. `dotnet test --solution` now works uniformly on Windows / Linux / macOS via Microsoft.Testing.Platform; tests are discovered natively by VS 2022 17.14+, JetBrains Rider, and VS Code (C# Dev Kit) without VSTest. Coverage gate via `coverlet.msbuild` (`/p:CollectCoverage=true /p:Threshold=90`).
- Stress test project (`Oragon.AdaptivePool.Core.Stress`) now opt-in via `<IsTestProject>false</IsTestProject>` so `dotnet test --solution` excludes it by default; invoke explicitly via project path or `dotnet run --project tests/Oragon.AdaptivePool.Core.Stress`.
- Repository now ships a project-local `NuGet.Config` that clears inherited `<fallbackPackageFolders>` (e.g., from machine-wide Visual Studio installs that pin Windows-only paths) to ensure deterministic, OS-agnostic restore.
- Removed obsolete `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` from test csprojs (superseded by `global.json` `test.runner` on .NET 10 SDK).
- CI workflow simplified to use `dotnet test --solution` + `coverlet.msbuild` `/p:` properties (no more `coverlet.console` workaround).

## [1.0.0] - 2026-05-03

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
