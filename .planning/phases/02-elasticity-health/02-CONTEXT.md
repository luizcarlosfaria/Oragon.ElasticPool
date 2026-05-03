# Phase 2: Elasticity & Health - Context

**Gathered:** 2026-05-03
**Status:** Ready for planning

<domain>
## Phase Boundary

Adicionar sobre o engine fixed-size do Phase 1 a camada de **elasticidade adaptativa** + **saúde proativa**: composite-signal grow, hysteretic shrink, background sweep com `Check` hook ativo, e telemetria completa (ActivitySource spans em grow/shrink/health, source-gen `[LoggerMessage]` em transições, counters dedicados). O motor passa a crescer sob pressão real e encolher na ociosidade, mantendo a superfície pública do Phase 1 inalterada (apenas adições configuráveis no builder e novos eventos de telemetria). **Fora desta fase:** RabbitMQ adapter (Phase 3), pipeline de release OSS (Phase 4), quarentena com backoff (v2).

</domain>

<decisions>
## Implementation Decisions

### Composite-Signal Grow Thresholds
- **Waiter queue threshold**: default `1` (qualquer espera dispara crescimento adaptativo), MAS configurável via builder fluent: `.GrowOnWaiterCount(int n)` permitindo cliente tornar mais tolerante a spikes
- **Utilização sustentada**: default ≥ `80%` (HikariCP-style equilibrado), MAS configurável via builder fluent: `.GrowOnUtilizationPercent(double p)` para tuning por carga (low-latency apps querem 75%, latency-tolerant querem 90%)
- **Janela de amostragem para utilização**: `30s` rolling window (balance reatividade vs ruído)
- **p95 wait time threshold**: `100ms` (alvo de latência razoável para resource pools); configurável via builder fluent `.GrowOnWaitTimeP95(TimeSpan)`
- Os três sinais são combinados: grow dispara se **qualquer um** dos thresholds for excedido (OR semântico, não AND), respeitando `MaxSize` como teto absoluto

### Shrink Hysteresis & Sweep Cadence
- `IdleTimeout` default = `60s` (HikariCP default; item ocioso descartado após esse período até atingir MinSize)
- Cooldown windows após grow antes de shrinkar = `3` windows (com window de utilização = 30s, dá ~90s de cooldown — evita thrashing)
- Shrink batch size = `1 item por sweep tick` (gentle decay; encolhe gradualmente sem oscilação abrupta)
- Sweep interval default = `30s` (alinha com janela de utilização; uma decisão de grow/shrink por janela)
- Configurável via builder: `.IdleTimeout(TimeSpan)`, `.ShrinkCooldownWindows(int)`, `.SweepInterval(TimeSpan)`

### Telemetry Tag Schema & Logging
- ActivitySource spans em `Acquire`, `Release`, `HealthCheck`, `Grow`, `Shrink` — sempre com tags `pool.name` + `outcome` (grew/shrunk/healthy/unhealthy/skipped); cardinalidade bounded (sem reason codes detalhados que explodiriam em prod)
- Log level para grow/shrink events = `Information` (raros mas operacionalmente significativos; visíveis em prod sem ruído)
- Span granularity para sweep = 1 span por sweep tick com `events` para cada item processado (não 1 span por item — explosão de cardinalidade)
- Counters separados: `pool.factory.failures` (já em Phase 1) vs `pool.health.failures` novo (sweep + BeforeUse/AfterUse Unhealthy decisões)
- Adicionar counters: `pool.grow.count`, `pool.shrink.count`, `pool.sweep.duration`, `pool.acquire.wait.duration` (histogram)

### Claude's Discretion
- Exact algorithm for composite-signal evaluation (order de avaliação dos 3 sinais, short-circuit ou compute todos para telemetria)
- Estrutura interna do "utilization sampler" (rolling window via `Channel<sample>` ou `Stopwatch`-based?)
- Histogram bucket boundaries para `pool.acquire.wait.duration` (default OTel ou customizado?)
- Estratégia exata de cooldown tracking (count-based vs timestamp-based)
- Como o sweep coordena com grow active: pode pular sweep se grow rodou recentemente OU sempre roda mas pula shrink durante cooldown
- Backoff exato em sweep failures: exponential 30s → 60s → 120s → 5min (cap) per RESEARCH.md, mas curva exata pode ser tuned

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets (from Phase 1)
- `Internals/AdaptivePool.cs` — engine sealed que precisará receber:
  - `_growLock` ou similar para coordenar decisões de grow
  - `UtilizationSampler` field (novo)
  - `_lastGrowAt` timestamp ou `_growCooldownRemaining` counter para hysteresis
  - `RunSweepLoopAsync()` background task fired em `StartAsync()` (que pode precisar ser adicionado se ainda não existe)
- `Telemetry/TelemetryEmitter.cs` — extender com novos counters e ActivitySource (ainda não criada na Phase 1, era só Meter); precisa adicionar `private static readonly ActivitySource _activitySource = new("Oragon.AdaptivePool")`
- `Telemetry/PoolDiagnosticsLog.cs` — adicionar `[LoggerMessage]` para Grew (1005), Shrunk (1006), SweepStarted (1007), SweepCompleted (1008), SweepFailureBackoff (1009)
- `Hooks/HookDelegates.cs` — `CheckDelegate<T>` já existe; agora será **invocado** pelo sweep (Phase 1 documentou como "placeholder")
- `Builder/AdaptivePoolBuilder.cs` — adicionar métodos fluent: `GrowOnWaiterCount`, `GrowOnUtilizationPercent`, `GrowOnWaitTimeP95`, `IdleTimeout`, `ShrinkCooldownWindows`, `SweepInterval`, `MaxBackoff`
- `Internals/PoolEntry.cs` — adicionar `LastReturnedAt` timestamp se não existe; `LastValidatedAt` para skip-if-recently-validated em sweep
- `TimeProvider` injetado via builder no Phase 1 — já é o ponto de extensão para `FakeTimeProvider` em testes determinísticos
- `WaitBehavior` enum — sem mudanças

### Established Patterns (Phase 1)
- Lock-free hot path via `Interlocked` + `ConcurrentQueue` + `Channel<TCS>` waiter — manter
- `[LoggerMessage]` source-gen para logging allocation-free — extender
- `Meter` via `IMeterFactory` com fallback `new Meter` — pattern já estabelecido
- xUnit v3 + AwesomeAssertions + NSubstitute + `FakeTimeProvider` — o stack de testes
- Stress tests separados em `Oragon.AdaptivePool.Core.Stress` excluídos da CI default

### Integration Points
- `PeriodicTimer.WaitForNextTickAsync(ct)` para sweep loop (BCL net6+, drift-free)
- `MetricCollector<T>` (`Microsoft.Extensions.Diagnostics.Testing`) para asserções de counter em tests
- `Activity.Current` / `MeterListener` para asserções de spans em tests

</code_context>

<specifics>
## Specific Ideas

- **Anchor stress test do Phase 2**: burst → idle → burst cycle (centenas de threads, milhares de cycles, FakeTimeProvider acelera tempo) verificando: pool cresce sob burst, encolhe durante idle (após cooldown), volta a crescer no segundo burst sem perder waiters. Esse é o teste-âncora análogo ao MaxSize=1 ping-pong do Phase 1.
- **Determinismo via FakeTimeProvider**: TODO teste de hysteresis/sweep DEVE usar `FakeTimeProvider` para evitar flakiness. Wall-clock-based timing tests são proibidos (conforme PITFALLS.md).
- **Cooldown via counter, não timestamp**: contar windows decorridos desde último grow (incrementa no tick do sweeper) é mais determinístico para teste do que comparar timestamps. `_growCooldownRemaining` é decrementado no início de cada sweep tick; shrink só roda quando == 0.
- **Sweep adaptive backoff** (per RESEARCH Pitfall): se Check hook lança ou retorna Unhealthy em N consecutivos itens (ex.: 3+), backoff exponencial: próximo sweep em 60s ao invés de 30s, depois 120s, depois 5min cap. Reseta em primeiro success. Evita amplificar carga em outage do downstream.
- **Sample pode parecer overkill mas é diferenciador**: composite-signal vs HikariCP single-signal é o headline marketing. Investir em testes que provam comportamento sob cada sinal isoladamente + combinados.

</specifics>

<deferred>
## Deferred Ideas

- Quarentena com backoff (v2) — REQUIREMENTS marca como FAIL-V2-01
- AfterUse hook ativo (v2) — HOOK-V2-01 — em Phase 2 a assinatura permanece, mas integração com failure policy ativa fica para v2
- Per-item MaxLifetime/MaxUses rotação — LIFECYCLE-V2-01
- DrainAsync(TimeSpan) explícito — LIFECYCLE-V2-02 — Phase 2 mantém apenas `DisposeAsync()` do Phase 1
- Microsoft.Extensions.Diagnostics.HealthChecks integration (auto-register pool como health check) — defer, demanda real first
- Tuning empírico das thresholds (calibração benchmark-driven) — defer para Phase 4 ou v1.x; em v1.0 entregamos os defaults razoáveis acima e expomos APIs configuráveis

</deferred>
