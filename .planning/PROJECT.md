# Oragon.AdaptivePool

## What This Is

Biblioteca .NET genérica para pools de objetos pesados que **se adaptam** à pressão real
da aplicação — crescem sob carga, encolhem na ociosidade, e auto-curam instâncias quebradas
via hooks de ciclo de vida pluggáveis. Multi-target `net10.0` / `net9.0` / `net8.0`,
distribuída como NuGet OSS, com primeiro adapter para `IConnection` e `IChannel` do
RabbitMQ.Client v7+. Voltada a desenvolvedores .NET que precisam reusar recursos
caros de criar (conexões, clients, handlers) em cargas de tráfego altamente variáveis.

## Core Value

Um pool genérico que **simultaneamente** entrega elasticidade real (min/max com crescimento
e encolhimento automáticos), auto-cura (detecta e substitui objetos quebrados sem o
cliente saber) e DX fluente (builder limpo, async-first, DI-first) — os três pilares
juntos são o produto e nenhum pode ser sacrificado.

## Requirements

### Validated

<!-- Shipped and confirmed valuable. -->

(Nenhum ainda — ship to validate)

### Active

<!-- Current scope. Building toward these. -->

**Core (`Oragon.AdaptivePool.Core`):**

- [ ] Pool genérico `IAdaptivePool<T>` com `Acquire()` síncrono (retorno imediato se há item livre) e `AcquireAsync(CancellationToken)` (espera/cresce sob pressão)
- [ ] Wrapper disposable `IPoolItem<T>` expondo `.Object` e devolvendo ao pool no `Dispose()`/`DisposeAsync()`
- [ ] Builder fluente `AdaptiveObjectPoolFactory.Build<T>(IServiceProvider, CancellationToken)` com `.Factory(...)`, `.BeforeUse(...)`, `.Check(...)`, `.AfterUse(...)`, `.Release(...)`, `.Build()`
- [ ] Configuração de bounds: `MinSize`, `MaxSize`, `InitialSize` (eager warm-up opcional)
- [ ] Crescimento sob pressão por **combinação de sinais**: espera no `Acquire`, utilização sustentada (%), tamanho da fila de waiters
- [ ] Encolhimento automático: itens ociosos além de `IdleTimeout` são descartados até atingir `MinSize`
- [ ] Validação de saúde **on-borrow** (hook `BeforeUse`) com decisão `Healthy` / `Unhealthy`
- [ ] Validação de saúde em **background sweep** periódico (hook `Check`)
- [ ] Validação opcional **on-return** (hook `AfterUse`)
- [ ] Política plugável de falha (`IItemFailurePolicy<T>`): descarte+reposição, quarentena com retry/backoff, ou estratégia customizada
- [ ] Disposal/cleanup via hook `Release` (ex.: `connection.CloseAsync()`)
- [ ] Telemetria built-in via `System.Diagnostics.Metrics.Meter` (gauges de tamanho, contadores de borrow/return/fail/grow/shrink)
- [ ] Tracing via `ActivitySource` em `Acquire`/`Release`/health-check
- [ ] Logging via `ILogger<T>` em transições de estado e falhas
- [ ] Integração DI: extensão `services.AddAdaptivePool<T>(...)` para Microsoft.Extensions.DependencyInjection
- [ ] Cancellation token propagado em todos os hooks async e em `AcquireAsync`
- [ ] Thread-safety verificada sob carga concorrente real

**RabbitMQ Adapter (`Oragon.AdaptivePool.RabbitMQ`):**

- [ ] Extension `services.AddAdaptiveConnectionPool(...)` configurando pool de `IConnection` com health check baseado em `IsOpen`
- [ ] Extension `services.AddAdaptiveChannelPool(...)` configurando pool de `IChannel` em camada sobre o pool de `IConnection` (factory pega conexão do pool de conexões)
- [ ] Convenções de nomenclatura e DX consistentes com `Oragon.RabbitMQ` (sister library)
- [ ] Sample mostrando publisher de alta variação (de algumas/hora a centenas-de-milhares simultâneas)

**Quality / OSS:**

- [ ] CI rodando matriz de teste nos três TFMs (`net10.0`, `net9.0`, `net8.0`)
- [ ] Testes de carga/concorrência reproduzindo o cenário motivador (burst → idle → burst)
- [ ] README com quickstart, sample completo, e exemplo de telemetria com OpenTelemetry
- [ ] Versionamento semântico (SemVer 2.0)
- [ ] Pacotes publicados no NuGet.org

### Out of Scope

<!-- Explicit boundaries. Includes reasoning to prevent re-adding. -->

- **Pool distribuído (cross-process, Redis-backed)** — foco é in-process; coordenação distribuída é um problema diferente com tradeoffs próprios
- **Persistência de estado** — pool é efêmero por natureza; persistir bounds/métricas é responsabilidade do consumidor
- **UI/dashboard de monitoramento próprio** — exposição via `Meter`/`ActivitySource` permite o cliente integrar com qualquer stack (Grafana, App Insights, Aspire Dashboard)
- **Adapters além de RabbitMQ na v1** — `HttpClient`, `Npgsql/DbConnection`, etc. ficam para milestones futuros; v1 prova o conceito com RabbitMQ
- **Sobreposição com pools nativos** — não substituir `HttpClientFactory` nem o pool interno do ADO.NET; agir onde não há solução adequada (ex.: `IConnection` do RabbitMQ não tem pool oficial)

## Context

**Motivação real:** O autor publica mensagens RabbitMQ em workloads altamente variáveis —
de "algumas mensagens por hora" a "centenas de milhares simultaneamente". O
`Microsoft.Extensions.ObjectPool` é fixo (não cresce sob pressão real, não encolhe na
ociosidade), e o RabbitMQ.Client v7+ não oferece pooling de `IConnection`/`IChannel` —
deixando essa responsabilidade para o consumidor. Hoje a solução manual envolve criar
e descartar conexões ad-hoc, com custo TCP repetido nos picos e desperdício de
recursos quando ociosos.

**Sister library:** `Oragon.RabbitMQ` (mesma autoria) cobre o lado consumidor com
fluent builder + minimal API + DI-first. O AdaptivePool complementa o lado publisher
(e qualquer cenário que precise de conexões/canais sob demanda elástica), mantendo
convenções consistentes (fluent builder, factory pattern, DI-first, RabbitMQ.Client v7+).

**Ecossistema:** .NET 10/9/8 são todos suportados ativamente em 2026. Recursos como
`IMeterFactory`, `ActivitySource`, `ValueTask`, `IAsyncDisposable` estão disponíveis
nos três TFMs — não há necessidade de polyfills significativos.

**Diferenciação no NuGet:** `Microsoft.Extensions.ObjectPool` (fixo, sem health),
`Polly` (resilience patterns, não pooling), `DotNetty` (custom pooling, network-specific).
Não há equivalente direto que combine elasticidade + auto-cura + generalidade fluente.

## Constraints

- **Tech stack**: .NET 10/9/8 multi-target — não usar APIs exclusivas do .NET 10 sem `#if NET10_0_OR_GREATER` guards
- **Dependências Core**: minimalistas — `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions` (BCL provê `Meter`, `ActivitySource`, `ValueTask`)
- **Async-first**: API assíncrona é a primária; `Acquire()` síncrono só funciona quando há item livre imediato (sem espera)
- **Thread-safety**: pool deve operar sob alta concorrência sem deadlocks nem starvation; cobertura por testes de stress
- **OSS bar**: testes em CI nos 3 TFMs, README com quickstart, sample executável, semver, NuGet com symbols/source link
- **Compatibilidade RabbitMQ**: adapter v1 alinhado com RabbitMQ.Client 7.x (`IConnection`, `IChannel` async-first)

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Nome `Oragon.AdaptivePool` | "Adaptive" captura tanto elasticidade quanto auto-cura num único termo; prefixo `Oragon.` consistente com outras libs do autor | — Pending |
| Multi-target `net10.0` / `net9.0` / `net8.0` | Cobre LTS atuais e a release mais recente; sem polyfills significativos necessários | — Pending |
| Dois pacotes (Core + RabbitMQ adapter) | Core sem dependência externa permite outros adapters futuros; separação clara de responsabilidades | — Pending |
| Hooks lifecycle com 5 estágios (Factory/BeforeUse/Check/AfterUse/Release) | Cobertura granular do ciclo de vida sem forçar todos os hooks a serem usados; cada um é opcional exceto Factory | — Pending |
| Telemetria built-in (Meter + ActivitySource + ILogger) | OSS sério em 2026 espera observabilidade nativa OpenTelemetry-friendly; `Meter` é universal e leve | — Pending |
| Política de falha plugável (vs. estratégia fixa) | Casos de uso variam (RabbitMQ quer descarte+reposição, HTTP pode preferir quarentena); flexibilidade > prescrição | — Pending |
| Combinação de sinais de pressão (vs. único sinal) | Sinal único (ex.: só "espera no Get") é frágil; combinação (espera + utilização + fila) reflete carga real | — Pending |
| Apenas RabbitMQ em adapters v1 | Foco em provar o conceito com um caso real e bem motivado antes de generalizar | — Pending |
| OSS público | Maximiza valor da biblioteca e força disciplina de qualidade (docs, testes, semver) | — Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd-complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-05-03 after initialization*
