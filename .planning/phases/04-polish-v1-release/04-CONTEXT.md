# Phase 4: Polish & v1.0 Release - Context

**Gathered:** 2026-05-03
**Status:** Ready for planning

<domain>
## Phase Boundary

Cruzar a barra de qualidade OSS e shipar v1.0 para NuGet.org. Objetivo: README que converte avaliadores em 60 segundos, CI multi-TFM verde no `main`, public API surface congelada via `PublicApiAnalyzers`, symbol packages + SourceLink permitindo step-into debugging para consumidores. **Esta fase NÃO adiciona features funcionais** — apenas processo de release, documentação, e observabilidade externa do pacote. Foco em OSS-01..05.

</domain>

<decisions>
## Implementation Decisions

### Release Mechanics & NuGet Publishing
- **Versioning**: MinVer tag-driven — pushar tag `v1.0.0` no git dispara build CI que produz `1.0.0.nupkg` + `.snupkg`; PRs/branches recebem versão pre-release auto-gerada (e.g., `1.0.0-alpha.0.5+abc123`)
- **NuGet publish trigger**: GitHub Actions workflow específico (`.github/workflows/release.yml`) acionado em push de tag `v*`; usa secret `NUGET_API_KEY` (configurado no repo settings) para `dotnet nuget push`
- **Pre-release strategy**: tagear `v1.0.0-rc.1` primeiro (RC = release candidate) → soak ~1 semana coletando feedback → tagear `v1.0.0` final se nenhum issue blocker surgir
- **NuGet package metadata** (em `Directory.Build.props` ou per-csproj):
  - PackageId: `Oragon.AdaptivePool.Core`, `Oragon.AdaptivePool.RabbitMQ`
  - Description: clara, ~200 chars, mencionando "adaptive", "elastic", "lifecycle hooks"
  - Authors: `luizcarlosfaria`
  - RepositoryUrl: GitHub URL do projeto (placeholder até ter repo)
  - License: **MIT** (consistente com outras libs Oragon, OSS-friendly)
  - Tags: `[adaptive, pool, rabbitmq, elastic, object-pool, dotnet, oss]`
  - PackageIcon: `icon.png` na raiz do repo (placeholder; design pode ser feito antes do v1.0 final)
  - PackageReadmeFile: README.md per-package (Phase 1 deferiu — agora é hora)

### README & Sample Documentation
- **README structure** (em ordem visual de leitura):
  1. Header com badges (build status, NuGet version, downloads, license)
  2. **30-second quickstart**: snippet copy-paste direto, mostra `services.AddAdaptivePool<T>(...)` + `IPoolItem<T>` em ~10 linhas runnable
  3. **Feature highlights** em bullets (3 pilares: elasticidade, auto-cura, DX)
  4. **Comparação vs Microsoft.Extensions.ObjectPool** (tabela: bounds, elasticity, health, telemetry, async — que MS ObjectPool não tem)
  5. **RabbitMQ adapter** seção dedicada com layered exemplo
  6. **OpenTelemetry example** com Console exporter (zero-config) + linha sobre OTLP collector para prod
  7. **Sample link** para `samples/...BurstyPublisher` com one-liner `dotnet run --project samples/...`
  8. Contributing, License, Acknowledgments

- **Code examples**: copy-pasteáveis com using statements completos — leitor copia direto e roda em `dotnet new console`. Mais verboso mas elimina fricção de "que namespace é esse?"

- **OTel exporter example**: Console exporter como demonstração (sem setup extra além de NuGet); linha de comentário explicando como swap para OTLP/Prometheus exporters em prod

- **License**: **MIT** (consistente com Oragon.RabbitMQ sister library, alinhado com mainstream .NET OSS)

### Claude's Discretion
- Estrutura exata do `release.yml` workflow (jobs sequência: build → test → pack → publish; gate em tag pattern)
- Quais badges incluir (lista padrão: build, NuGet, downloads, license — vs adicional como Codecov, OpenSSF Scorecard)
- Detalhes do icon.png (criar placeholder simples agora ou shipar v1.0 sem icon?)
- Naming exato das seções do README e ordem fina
- Se incluir CHANGELOG.md formal (recomendado, mantido manualmente, vinculado nas release notes do GitHub)
- Se ativar GitHub Actions matrix em macOS/Windows além de Ubuntu (Phase 1 ficou só ubuntu; v1.0 idealmente ubuntu+windows pelo menos)
- Estratégia para Codecov / coverage badge (gate 90% Core já existe; expor publicamente?)

</decisions>

<code_context>
## Existing Code Insights

### Reusable Assets (from Phases 1-3)
- `.github/workflows/build.yml` (Phase 1) — já tem matriz `ubuntu-latest × {net8.0, net9.0, net10.0}`, executa Core unit + stress + coverage gate. Phase 4 adiciona release.yml separado mas pode evoluir build.yml para incluir RabbitMQ unit + integration (Testcontainers needs Docker — runner-side decision)
- `Directory.Build.props` — já tem MinVer 6.0.0 + SourceLink.GitHub 8.0.0 wired; Phase 4 adiciona `<PackageReadmeFile>`, `<PackageIcon>`, refina `<Description>`
- `Directory.Packages.props` — CPM strict, todos pacotes pinados
- `PublicAPI.Shipped.txt` + `PublicAPI.Unshipped.txt` per project — analyzer ativo desde Phase 1; Phase 4 promove Unshipped → Shipped no final como ato de "freezing" o v1.0 surface
- 5 NuGet projects: Core, Core.Tests, Core.Stress, Core.Benchmarks, RabbitMQ + 2 test projects + 1 sample. Apenas Core e RabbitMQ são publicáveis (`<IsPackable>true</IsPackable>` somente nesses dois)

### Established Patterns
- Multi-target net10/9/8 — manter
- Sister library Oragon.RabbitMQ — README, license, NuGet metadata devem alinhar visualmente para reconhecimento de marca

### Integration Points
- GitHub Actions secret `NUGET_API_KEY` — assumes que será configurado nos repo settings após criar repo público
- NuGet.org — publish destination
- GitHub Releases — link a tag para mostrar release notes (manualmente ou via release-please-style automation)

</code_context>

<specifics>
## Specific Ideas

- **Comparação vs Microsoft.Extensions.ObjectPool é o "money shot" do README** — diferenciador central do produto. Tabela visual:
  | Feature | M.E.OP | Oragon.AdaptivePool |
  |---------|--------|---------------------|
  | Min/Max bounds | ❌ (só MaximumRetained) | ✅ |
  | Elastic grow under pressure | ❌ | ✅ (composite signal) |
  | Auto-shrink on idle | ❌ | ✅ (hysteretic) |
  | Health checks | ❌ | ✅ (5 hooks) |
  | OpenTelemetry built-in | Limited | ✅ |
  | Pluggable failure policy | ❌ | ✅ |
  | Layered (channel/connection) | ❌ | ✅ (RabbitMQ) |

- **PublicAPI promotion**: ato cerimonial — copiar `PublicAPI.Unshipped.txt` para `PublicAPI.Shipped.txt`, esvaziar Unshipped, commitar antes do tag `v1.0.0`. Após v1.0, qualquer adição vai pra Unshipped novamente

- **CHANGELOG.md** mantido manualmente em formato Keep-a-Changelog. v1.0.0 entry lista as 30 requirements implementadas, agradece contributors (mesmo que só seja o autor por enquanto)

- **Icon placeholder**: SVG simples gerado por script (texto "AP" estilizado em círculo) → PNG 128x128. Pode ser substituído pré v1.0 por icon profissional sem afetar release pipeline

- **CI evolução**: build.yml inclui RabbitMQ.Tests (unit, sem Docker) + opcionalmente RabbitMQ.IntegrationTests (Testcontainers — GitHub Actions Linux runners têm Docker, então é viável)

- **Coverage badge**: Codecov upload já é gratuito para OSS public repos; expor o gate 90% Core publicamente é marketing legítimo

</specifics>

<deferred>
## Deferred Ideas

- Aspire integration package (`Oragon.AdaptivePool.RabbitMQ.AspireClient`) — defer para v1.1+ baseado em demanda
- Polly integration glue — defer
- HttpClient/Npgsql adapters — defer (REQUIREMENTS marca v2/v3)
- v2 features (quarantine policy, AfterUse ativo, MaxLifetime, DrainAsync explícito) — explicitamente fora deste milestone
- OSS Scorecard / SLSA provenance — defer (overhead alto para primeira release)
- Multi-runner CI (macOS, Windows) — Phase 4 mantém ubuntu-only; expandir em v1.1 se necessário
- Codecov gate failure → blocking PR (atualmente 90% local; expor como signal sem block)

</deferred>
