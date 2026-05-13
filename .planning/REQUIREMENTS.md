# Requirements: Oragon.ElasticPool

**Defined:** 2026-05-03
**Core Value:** Pool genérico .NET que entrega simultaneamente elasticidade real, auto-cura via lifecycle pluggável, e DX fluente — os três pilares juntos são o produto.

## v1 Requirements

Requirements for initial release (v1.0). Each maps to roadmap phases.

### API — Public Surface (Core)

- [x] **API-01**: Pool expõe interface `IElasticPool<T>` com `Acquire()` síncrono (retorno imediato quando há item livre) e `AcquireAsync(CancellationToken)` retornando `ValueTask<IPoolItem<T>>`
- [x] **API-02**: Wrapper `IPoolItem<T>` disposable expõe `.Object` e devolve ao pool em `Dispose()` / `DisposeAsync()`, com idempotência e detecção de double-dispose
- [x] **API-03**: Builder fluente `ElasticObjectPoolFactory.Build<T>(IServiceProvider, CancellationToken)` produz pool selado a partir de configuração imutável; `.Build()` valida configuração obrigatória e lança em config inválida

### HOOK — Lifecycle Hook Surface

- [x] **HOOK-01**: Hook `Factory((IServiceProvider, CancellationToken) → ValueTask<T>)` obrigatório, executado fora de locks para não bloquear hot path
- [x] **HOOK-02**: Hook `BeforeUse((T, CancellationToken) → ValueTask<PoolState>)` opcional, executado em `Acquire` antes de entregar o item ao chamador; falha aciona política de falha
- [x] **HOOK-03**: Hook `Check((T, CancellationToken) → ValueTask<PoolState>)` opcional, executado pelo background sweeper em itens ociosos
- [x] **HOOK-04**: Hook `AfterUse((T, CancellationToken) → ValueTask<PoolState>)` opcional, executado no retorno; padrão no-op (opt-in real para validação fica em v2)
- [x] **HOOK-05**: Hook `Release((T, CancellationToken) → ValueTask)` opcional para cleanup/disposal customizado (ex.: `connection.CloseAsync()`)

### BOUND — Capacity & Warm-up

- [x] **BOUND-01**: Configuração `MinSize` (piso mantido), `MaxSize` (teto absoluto), `InitialSize` (warm-up alvo), com validação `0 ≤ Min ≤ Initial ≤ Max`
- [x] **BOUND-02**: Eager warm-up assíncrono awaitable até atingir `InitialSize` (gating opcional para readiness probes), com cancelamento limpo em shutdown durante warm-up

### ELASTIC — Adaptive Sizing

- [x] **ELASTIC-01**: Crescimento sob pressão por **combinação de sinais** configurável: tamanho da fila de waiters, utilização sustentada (% em janela temporal), tempo médio de espera no `Acquire` — respeitando `MaxSize`
- [x] **ELASTIC-02**: Encolhimento automático: itens ociosos além de `IdleTimeout` são descartados pelo background sweeper, com histerese (cooldown desde último `grow`) para evitar thrashing, nunca abaixo de `MinSize`

### FAIL — Failure Policy

- [x] **FAIL-01**: Interface pública `IItemFailurePolicy<T>` invocada quando hook de saúde retorna `Unhealthy` ou quando Factory falha; recebe contexto suficiente para decidir descarte/quarentena/custom
- [x] **FAIL-02**: Política built-in `DiscardAndReplace` (descarta item, dispara reposição se abaixo de `MinSize`) como padrão do builder

### TELEM — Observability

- [x] **TELEM-01**: `Meter` nomeado `"Oragon.ElasticPool"` obtido via `IMeterFactory`, expondo gauges (`pool.size`, `pool.available`, `pool.in_use`, `pool.waiting`) e counters (`pool.acquire.count`, `pool.acquire.duration`, `pool.factory.failures`, `pool.grow.count`, `pool.shrink.count`, `pool.health.failures`); tags com cardinalidade limitada (`pool.name`)
- [x] **TELEM-02**: `ActivitySource` nomeado `"Oragon.ElasticPool"` com spans em `Acquire`, `Release`, `HealthCheck`, `Grow`, `Shrink`; uso de `HasListeners()` para evitar custo quando ninguém escuta
- [x] **TELEM-03**: Logging via `ILogger<T>` usando `[LoggerMessage]` source-generated (allocation-free) para transições de estado, falhas de factory, evictions, decisões de política

### DI — Dependency Injection

- [x] **DI-01**: Extensão `services.AddElasticPool<T>(name, configure)` para Microsoft.Extensions.DependencyInjection com pools nomeados (named options pattern), resolução de hooks via `IServiceProvider`, e auto-registro de health checks opcional

### QUAL — Cross-cutting Quality

- [x] **QUAL-01**: `CancellationToken` propagado fim-a-fim em todos os hooks async, em `AcquireAsync`, e na cancelação de waiters pendentes (sem perda de wake-up)
- [x] **QUAL-02**: Pool implementa `IAsyncDisposable` com semântica de drain (parar de aceitar novos `Acquire`, aguardar in-flight, então liberar recursos)
- [ ] **QUAL-03**: Thread-safety verificada via testes de stress concorrente (centenas de threads, milhares de acquire/release ciclos) cobrindo paths de grow/shrink/sweep

### RMQ — RabbitMQ Adapter

- [x] **RMQ-01**: Extensão `services.AddElasticConnectionPool(name, configure)` configurando pool de `IConnection` com `BeforeUse`/`Check` baseados em `IsOpen`, `Release` chamando `CloseAsync()`, e `AutomaticRecoveryEnabled = false` por padrão (evita conflito com gestão de lifecycle do pool)
- [x] **RMQ-02**: Extensão `services.AddElasticChannelPool(name, configure)` configurando pool de `IChannel` em camada sobre o pool de `IConnection` (factory adquire connection do pool interno via `ConditionalWeakTable` para pareamento, release fecha channel e devolve connection)
- [x] **RMQ-03**: Sample executável publicador-bursty demonstrando ciclo "algumas/hora → centenas-de-milhares simultâneas → ocioso" usando o pool em camadas
- [x] **RMQ-04**: Convenções de nomenclatura, builder, e DI consistentes com `Oragon.RabbitMQ` (sister library para o lado consumidor)

### OSS — Open-Source Quality Bar

- [x] **OSS-01**: CI (GitHub Actions) com matriz de teste nos três TFMs (`net10.0`, `net9.0`, `net8.0`) executando unit + stress + integration (Testcontainers.RabbitMq) tests
- [ ] **OSS-02**: README com quickstart, exemplo de telemetria com OpenTelemetry exporters, link para sample bursty publisher, e tabela comparativa vs `Microsoft.Extensions.ObjectPool`
- [x] **OSS-03**: Versionamento semântico (SemVer 2.0) automatizado via MinVer (tag-driven), changelog mantido manualmente
- [x] **OSS-04**: Pacotes NuGet publicados com symbol packages (`.snupkg`) e SourceLink (debug step-into nas fontes do GitHub)
- [x] **OSS-05**: `Microsoft.CodeAnalysis.PublicApiAnalyzers` ativo desde o início com `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` versionados

## v2 Requirements

Deferred to future release. Tracked but not in current roadmap.

### Failure Policies (Extended)

- **FAIL-V2-01**: Política built-in `ExponentialBackoffQuarantine` (tenta reabilitar item quebrado em background com retry/backoff antes de descartar)

### Lifecycle (Extended)

- **HOOK-V2-01**: `AfterUse` hook ativo (validação no retorno) com integração à política de falha
- **LIFECYCLE-V2-01**: Per-item `MaxLifetime` / `MaxUses` para rotação programada (mitiga server-side timeouts e acumulação de estado)
- **LIFECYCLE-V2-02**: `DrainAsync(TimeSpan)` API explícita de graceful shutdown com timeout (além do dispose básico)

### Integrations (Extended)

- **INT-V2-01**: Pacote `Oragon.ElasticPool.Polly` com sample/glue de retry+circuit-breaker em volta do `AcquireAsync`

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Pool distribuído (Redis/etcd cross-process) | Problema diferente (consenso, partições, eviction races); foco é in-process |
| Persistência de estado entre restarts | Estado de TCP é process-bound; serializar não faz sentido |
| UI/dashboard de monitoramento próprio | Cliente integra via Meter/ActivitySource com Aspire/Grafana/App Insights/OTel Collector |
| Adapters além de RabbitMQ na v1 | HttpClient já tem IHttpClientFactory; ADO.NET já pool internamente; v1 prova com RabbitMQ apenas |
| Retry/circuit-breaker embutido (Polly inline) | Resilience é layer ortogonal; embed leakaria versão; documentar a receita de composição |
| Auto-tuning ML/heurístico de Min/Max | Comportamento oculto, debug pesadelo; expor métricas e deixar humano tunar |
| Sub-pools keyed (per-tenant/per-key) | Complexidade extra; cliente compõe `Dictionary<TKey, IElasticPool<T>>` |
| API exclusivamente síncrona (modo legacy) | Forçaria bloqueio em recursos async (RabbitMQ v7 é async-only); cria deadlocks |
| Compartilhamento de IChannel entre threads | RabbitMQ docs explicitamente desencorajam; pool dimensiona por concorrência |
| Re-enqueue silencioso de itens quebrados | Defeats auto-cura; falha de health-check sempre dispara política |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| API-01 | Phase 1 | Complete |
| API-02 | Phase 1 | Complete |
| API-03 | Phase 1 | Complete |
| HOOK-01 | Phase 1 | Complete |
| HOOK-02 | Phase 1 | Complete |
| HOOK-03 | Phase 1 | Complete |
| HOOK-04 | Phase 1 | Complete |
| HOOK-05 | Phase 1 | Complete |
| BOUND-01 | Phase 1 | Complete |
| BOUND-02 | Phase 1 | Complete |
| ELASTIC-01 | Phase 2 | Complete |
| ELASTIC-02 | Phase 2 | Complete |
| FAIL-01 | Phase 1 | Complete |
| FAIL-02 | Phase 1 | Complete |
| TELEM-01 | Phase 1 | Complete |
| TELEM-02 | Phase 2 | Complete |
| TELEM-03 | Phase 2 | Complete |
| DI-01 | Phase 1 | Complete |
| QUAL-01 | Phase 1 | Complete |
| QUAL-02 | Phase 1 | Complete |
| QUAL-03 | Phase 2 | Pending |
| RMQ-01 | Phase 3 | Complete |
| RMQ-02 | Phase 3 | Complete |
| RMQ-03 | Phase 3 | Complete |
| RMQ-04 | Phase 3 | Complete |
| OSS-01 | Phase 4 | Complete |
| OSS-02 | Phase 4 | Pending |
| OSS-03 | Phase 4 | Complete |
| OSS-04 | Phase 4 | Complete |
| OSS-05 | Phase 4 | Complete |

**Coverage:**
- v1 requirements: 30 total
- Mapped to phases: 30 ✓
- Unmapped: 0

**Phase distribution:**
- Phase 1 (Core Skeleton): 16 requirements (API×3, HOOK×5, BOUND×2, FAIL×2, TELEM-01, DI-01, QUAL-01, QUAL-02)
- Phase 2 (Elasticity & Health): 5 requirements (ELASTIC×2, TELEM-02, TELEM-03, QUAL-03)
- Phase 3 (RabbitMQ Adapter): 4 requirements (RMQ×4)
- Phase 4 (Polish & v1.0): 5 requirements (OSS×5)

---
*Requirements defined: 2026-05-03*
*Last updated: 2026-05-03 after roadmap traceability mapping*
