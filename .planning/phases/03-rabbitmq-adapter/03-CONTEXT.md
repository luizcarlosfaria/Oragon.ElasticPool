# Phase 3: RabbitMQ Adapter - Context

**Gathered:** 2026-05-03
**Status:** Ready for planning

<domain>
## Phase Boundary

Construir o pacote `Oragon.ElasticPool.RabbitMQ` (segundo NuGet) que adapta o Core para `IConnection` e `IChannel` do RabbitMQ.Client v7.2.1 em **camadas**: pool de connections (TCP) com pool de channels (multiplexados) sobre ele. O adapter apenas expõe extension methods de DI (`services.AddElasticConnectionPool` + `services.AddElasticChannelPool`) que configuram pools genéricos do Core com hooks específicos de RabbitMQ (IsOpen para health, CloseAsync para Release, AutomaticRecoveryEnabled=false). **Esta fase valida que as abstrações do Core são suficientes para um cenário real layered/lifecycle-sensitive — se gap aparecer no Core, refatora Core ANTES de Phase 4.** Inclui sample executável `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher` reproduzindo o cenário motivador (idle → 100k burst → idle).

</domain>

<decisions>
## Implementation Decisions

### Channel Pool Composition Strategy
- **Channel pool é layered sobre connection pool**: o `Factory` hook do channel pool chama `connectionPool.AcquireAsync()` internamente; pareamento `IChannel ↔ IPoolItem<IConnection>` rastreado via `ConditionalWeakTable<IChannel, IPoolItem<IConnection>>` para garantir que o connection seja devolvido ao pool quando o channel for descartado
- **Channel-per-connection ceiling default = 100**: bem abaixo do limite RabbitMQ (2047 default), evita single-connection bottleneck; configurável via builder do channel pool (`MaxChannelsPerConnection`)
- **Connection death + channels órfãos**: cross-pool coordination — quando o pool de connection descarta uma `IConnection` (via Check hook detectando `IsOpen=false`), o channel pool é notificado via callback registrado e marca todos os channels emprestados sobre essa connection como Unhealthy (via `ConditionalWeakTable` reverso); ao retornar, esses channels são imediatamente descartados em vez de re-enqueue. Mais lógica do que "deixar falhar no uso", mas evita expor `AlreadyClosedException` para o cliente que devolveu um channel "saudável"
- **`AutomaticRecoveryEnabled = false` SEMPRE**: forçado pelo adapter na configuração da `ConnectionFactory` (com warning log se cliente já configurou true). Pool é a única fonte de verdade de lifecycle de connection — recovery automático conflita com replacement do pool (per RESEARCH Pitfall 9)

### DI Extension API Surface
- **Connection pool registration**: `services.AddElasticConnectionPool(name, connectionFactoryConfigurator, poolConfigurator)` — closure callback configura `ConnectionFactory` (HostName, UserName, etc.) + closure separado configura pool (MinSize, MaxSize, etc.). Adapter força `AutomaticRecoveryEnabled = false` ao final da configuração da factory
- **Channel pool registration**: `services.AddElasticChannelPool(name, connectionPoolName, poolConfigurator)` — referencia connection pool por nome (consistente com keyed singleton pattern do Core)
- **`IConnectionFactory` injection — Keyed Services first-class**: 3 modos suportados ordenadamente:
  1. **Keyed Service** (preferred): se cliente registrou `services.AddKeyedSingleton<IConnectionFactory>(name, ...)`, adapter resolve por nome (suporta multi-broker scenarios — connection pool "broker-a" pega factory keyed "broker-a"). Aspire-friendly.
  2. **Closure callback**: `AddElasticConnectionPool(name, factoryCfg => { ... }, poolCfg => { ... })` para casos simples sem DI registration
  3. **`IOptions<ElasticConnectionPoolOptions>`**: bind via `IConfiguration` para appsettings.json scenarios; cliente faz `services.Configure<ElasticConnectionPoolOptions>(name, config.GetSection(...))`
  Adapter probe na ordem acima e usa o primeiro disponível
- **Default `pool.name` = `string.Empty`**: consistente com Core para single-pool apps

### Testcontainers & Sample
- **Imagem Docker**: `rabbitmq:4-management` (RabbitMQ 4.x LTS com management UI para debug)
- **Fixture pattern**: `IClassFixture<RabbitMqContainer>` por test class — container reutilizado entre tests da mesma class, isolamento entre classes
- **Sample location**: `samples/Oragon.ElasticPool.RabbitMQ.Sample.BurstyPublisher/` — projeto runnable separado (`dotnet run --project samples/...`)
- **Bursty publisher cenário**: `BackgroundService` worker que cicla: 5min idle → 30s burst de 100k mensagens → 5min idle → repete 3x. Usa pool em camadas (`AddElasticConnectionPool` + `AddElasticChannelPool`). Logs/métricas mostram pool growing durante burst, shrinking durante idle, regrow no próximo burst — exatamente o headline marketing do projeto

### Claude's Discretion
- Detalhes da invalidation cross-pool (event/callback shape interno; bidirectional ConditionalWeakTable; cleanup discipline em DI lifecycle)
- Exact warning message + log level quando `AutomaticRecoveryEnabled = true` for sobrescrito
- Estratégia exata de probe ordering keyed → closure → IOptions (talvez ServiceCollection scan vs runtime check)
- Naming exato dos `ElasticConnectionPoolOptions` / `ElasticChannelPoolOptions` (match Core naming?)
- Sample exact message size, exchange/queue topology (declarar tópico transient? quorum queue?)
- Whether to expose `ConnectionFactoryDefaults` builder helper (set sensible defaults like `Heartbeat=60s`)

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets (from Phases 1+2)
- `Oragon.ElasticPool` (já publicado em local feed) — todo o engine com elasticidade, healthcheck, telemetria
- Builder fluent + DI extension `services.AddElasticPool<T>(name, configure)` — adapter constrói em cima
- `IItemFailurePolicy<T>` + `DiscardAndReplaceFailurePolicy<T>` — funcionam para `IConnection` e `IChannel`
- `Telemetry/TelemetryEmitter` exporta Meter "Oragon.ElasticPool" — adapter herda automaticamente; pool.name tag distingue connection vs channel pool ("rabbitmq-conn-default", "rabbitmq-channel-default")

### Established Patterns
- Sister library `Oragon.RabbitMQ` (consumer side) — convenções de fluent builder, factory pattern, DI-first, RabbitMQ.Client v7+. Adapter deve seguir as mesmas convenções para experiência consistente
- xUnit v3 + AwesomeAssertions + NSubstitute + FakeTimeProvider — stack de testes (mas integration tests usam wall-clock + Testcontainers, não FakeTimeProvider)
- Stress projects excluídos da CI default — mas Testcontainers integration tests SÃO incluídos na CI (broker é cheap-ish em container)

### Integration Points
- `RabbitMQ.Client` 7.2.1 — pinned no Phase 1 STACK. `IConnection`, `IChannel` (renomeado do `IModel` v6), `ConnectionFactory.CreateConnectionAsync()`, `connection.CreateChannelAsync()`, `connection.IsOpen`, `connection.CloseAsync()`, `channel.IsOpen`, `channel.CloseAsync()`
- `Testcontainers.RabbitMq` para integration tests — pinned recente (verificar versão atual)
- `Microsoft.Extensions.Hosting` (test-only) já adicionado em Phase 1 fix para CR-03 IHostApplicationLifetime

</code_context>

<specifics>
## Specific Ideas

- **Anchor stress test do Phase 3**: `BurstyPublisherIntegrationTest` com Testcontainers — cicla idle → burst → idle, observa via Meter (`pool.grow.count` deve incrementar durante burst, `pool.shrink.count` durante idle, sem deadlocks, sem leaked connections/channels após teste)
- **Convenções ALINHADAS com Oragon.RabbitMQ**: nomes de extension methods (`AddElasticConnectionPool` paralelo a `AddRabbitMQConsumer`), Builder pattern, mesmo prefixo de namespace
- **Critical pitfall (per RESEARCH Pitfall 11)**: channel_max default RabbitMQ = 2047. Sample MUST demonstrate spreading channels across múltiplas connections quando MaxChannelsPerConnection é hit
- **Critical pitfall (per RESEARCH Pitfall 10)**: IChannel **MUST NOT** be shared across publishing threads — pool dimensiona por concorrência, cada publisher acquires own channel
- **Connection factory configuration warning**: se cliente seta `AutomaticRecoveryEnabled = true`, log Warning + override silencioso para false (não falhar o startup, mas avisar)
- **Sample MUST be discoverable**: README link + `dotnet run --project samples/...BurstyPublisher` deve funcionar standalone (precisa de Docker para Testcontainers OR documenta connection string para broker existente)

</specifics>

<deferred>
## Deferred Ideas

- HttpClient adapter — REQUIREMENTS marca v2/v3 (out of scope v1)
- Npgsql/DbConnection adapter — same
- Aspire integration package `Oragon.ElasticPool.RabbitMQ.AspireClient` — defer; Phase 4 talvez tese
- Per-tenant/keyed sub-pools (vhost-keyed) — explicitly out of scope per REQUIREMENTS
- Built-in publisher confirms wrapper — adapter NÃO interfere com publisher confirms; AfterUse hook NÃO swallow exceptions de confirm logic (per RESEARCH Pitfall 12)
- Connection string parsing helper — adapter recebe `ConnectionFactory` configurada, cliente parse URI se quiser (`ConnectionFactory.Uri`)
- HealthChecks integration (`AddHealthChecks().AddRabbitMQ()` integrando com pool) — defer

</deferred>
