# Phase 1: Core Skeleton — Fixed-Size Pool - Context

**Gathered:** 2026-05-03
**Status:** Ready for planning

<domain>
## Phase Boundary

Entregar um pool de tamanho fixo (`MinSize == MaxSize == InitialSize`) totalmente funcional e testado em `Oragon.ElasticPool.Core`, com toda a superfície pública e decisões arquiteturais não-retrofitáveis travadas corretamente: assinaturas dos 5 hooks, contrato `ValueTask<IPoolItem<T>>`, dispose síncrono+assíncrono com idempotência, builder fluente, integração DI, contagem de itens com rollback em falha de Factory, política de falha plugável (com `DiscardAndReplace` default), telemetria base via `IMeterFactory`. **Fora desta fase:** crescimento elástico (Phase 2), sweeper em background (Phase 2), adapter RabbitMQ (Phase 3), pipeline de release OSS (Phase 4).

</domain>

<decisions>
## Implementation Decisions

### Repository Layout & Solution Structure
- Estrutura: `src/` (projetos publicáveis) + `tests/` (unit + integration) + `samples/` + `.github/workflows/`
- Nomes de projetos: `Oragon.ElasticPool.Core`, `Oragon.ElasticPool.RabbitMQ` (Phase 3), `Oragon.ElasticPool.Core.Tests`, `Oragon.ElasticPool.Core.Stress` (projeto separado, fora da CI default), `Oragon.ElasticPool.Core.Benchmarks` (Phase 4)
- `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` por projeto (cada `.csproj` mantém os seus)
- `README.md` único na raiz do repositório com seções por pacote

### Public API Micro-Decisions
- `IPoolItem<T>.Value` expõe a instância pooled (alinha com `Lazy<T>.Value`, `Nullable<T>.Value`)
- Esgotamento (`MaxSize` atingido + todos in-use): comportamento **configurável via builder** — `.WhenExhausted(WaitBehavior.Wait)` (default) ou `.WhenExhausted(WaitBehavior.Throw)` lança `PoolExhaustedException` imediatamente; `Wait` respeita `CancellationToken` do `AcquireAsync`
- Builder: apenas `Factory` é obrigatório; `Build()` lança `InvalidOperationException` se ausente; demais hooks (`BeforeUse`, `Check`, `AfterUse`, `Release`) são opcionais com defaults no-op
- `services.AddElasticPool<T>(name, configure)` requer `name` explícito; default = `string.Empty` para apps single-pool; usa named options pattern internamente para múltiplos pools tipados no mesmo `T`

### Test & Tooling Infrastructure
- Test framework: **xUnit v3 + Microsoft.Testing.Platform + Awesome Assertions + NSubstitute** (Awesome Assertions = fork OSS recente do FluentAssertions com mesma sintaxe, mantido após mudança de licença Xceed)
- Stress tests: projeto separado `Oragon.ElasticPool.Core.Stress` excluído da CI default (job dedicado nightly em Phase 4)
- Time mocking: `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` para todo teste sensível a tempo (per PITFALLS.md recommendation)
- Code coverage: gate de **90% no Core** na CI; sem gate em adapters/samples (apenas relatório); ferramentas: coverlet + ReportGenerator → Codecov

### Claude's Discretion
- Escolha exata de quais counters expor em TELEM-01 (mínimo: `pool.acquire.count`, `pool.factory.failures`; resto fica para Phase 2 quando grow/shrink/health surgem)
- Política de naming interno (private/internal classes) e estrutura de namespaces dentro de `Oragon.ElasticPool.Core`
- Detalhes do `PoolState` enum (incluir `Quarantined`? por ora apenas `Healthy`/`Unhealthy`, com espaço para extensão em v2)
- Forma exata da exception `PoolExhaustedException` (mensagem, properties como `MaxSize`, `WaitTime`)
- Estratégia exata de double-dispose detection (Interlocked flag, Disposed property pública?)

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets
- Nenhum — projeto greenfield, sem código pré-existente. Toda decisão é de design do zero.

### Established Patterns
- **Sister library `Oragon.RabbitMQ`** (consumer side) já estabelece convenções de nomenclatura, builder fluent, factory pattern, DI-first, RabbitMQ.Client v7+. Seguir consistência.
- **Microsoft.Extensions.ObjectPool** (`DefaultObjectPool<T>`) serve como referência de implementação interna em alguns aspectos (estrutura de fila, counters), mas o produto é fundamentalmente diferente (elástico vs fixo).

### Integration Points
- Microsoft.Extensions.DependencyInjection.Abstractions — extension `services.AddElasticPool<T>(...)` integra com `IServiceCollection` e named options pattern
- Microsoft.Extensions.Logging.Abstractions — `ILogger<T>` consumido nos hooks via DI
- System.Diagnostics.Metrics.IMeterFactory — Meter obtido via DI (não `new Meter(...)`)
- System.Diagnostics.ActivitySource — instanciado uma vez no Core, namespace `"Oragon.ElasticPool"`

</code_context>

<specifics>
## Specific Ideas

- **API sketch original do usuário** (capturado na conversa de questionamento) usa `pool.Accquire()` (sic) sync. Manter sync `Acquire()` para fast-path (item livre disponível) e `AcquireAsync(CancellationToken)` para path completo (espera/cresce). Em Phase 1 (fixed-size, sem grow), `Acquire()` sync funciona quando há item livre; senão chama `AcquireAsync` internamente ou lança per `WaitBehavior`.
- **Decisões não-retrofitáveis explícitas** (PITFALLS.md): hook signatures DEVEM aceitar `CancellationToken`, retornos DEVEM ser `ValueTask<T>` (não `Task<T>`), wrapper DEVE ter finalizer, `Meter` DEVE ser obtido via `IMeterFactory`, `IDisposable` + `IAsyncDisposable` ambos. Nenhum desses pode mudar em v1.x sem breaking change.
- **Single instance lock target**: usar `object` (compatível com net8/9/10), NÃO `System.Threading.Lock` (net9+ only) — evita `#if` e garante uniformidade da API pública.
- **Stress test `MaxSize=1` ping-pong** (success criterion #2) é o teste-âncora para validar correção de waiter-queue + counter rollback + cancelamento; deve passar antes de qualquer outra feature ser considerada complete.

</specifics>

<deferred>
## Deferred Ideas

- `Oragon.ElasticPool.OpenTelemetry` companion package com pre-wiring de OTel — defer até Phase 4 baseado em demanda
- `Oragon.ElasticPool.Polly` glue package — defer para v1.x baseado em demanda
- Suporte a `Microsoft.Extensions.Diagnostics.HealthChecks` integration (auto-register pool como health check) — defer para Phase 2 ou v1.x
- `PoolItemContext` / state bag rico nos hooks — defer; em v1, hooks recebem apenas `(T item, CancellationToken ct)`
- Quarentena com backoff (FAIL-V2-01) — explicitamente v2 conforme REQUIREMENTS.md
- AfterUse ativo (HOOK-V2-01) — em Phase 1 a assinatura existe mas default é no-op; ativação real vai para v2

</deferred>
