# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
