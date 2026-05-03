---
name: create-rule
description: >
  Cria uma nova rule (.claude/rules/*.md) para o Claude Code seguindo a
  documentação oficial. Use quando o usuário pedir para criar, adicionar
  ou organizar uma rule, instrução modular, padrão path-scoped, ou
  guideline por glob. Também use quando disser "criar regra para X",
  "rule de testes", "extrair do CLAUDE.md para rules", "instrução por
  glob", ou "modularizar instruções".
---

# Criar Rule no Claude Code

Cria rules em `.claude/rules/` ou `~/.claude/rules/` seguindo as convenções da documentação oficial. Rules são markdown puro, modulares, e podem ser carregadas sempre (unconditional) ou apenas quando Claude lê arquivos casando com globs (scoped).

## Antes de criar — é mesmo uma rule?

Use esta tabela para decidir. Se o conteúdo não couber claramente em "Rule", **PARE** e use o mecanismo correto.

| Situação | Mecanismo correto |
|---|---|
| Convenção universal de poucas linhas (build cmd, package manager, padrão de commit) | **CLAUDE.md** (não rule) |
| Padrão modular reutilizável (estilo de código, segurança, compliance) que sempre se aplica | **Rule unconditional** |
| Padrão específico de área do código (`src/api/**`, `notebooks/**`, `tests/**`) | **Rule scoped** com `paths` |
| Workflow multi-passo, playbook, comando reutilizável (`/deploy`, `/release`) | **Skill** (use `create-skill`) |
| Documentação extensa de referência (API docs, schemas, runbooks) | **Skill** (use `create-skill`) |
| Automação determinística que precisa rodar todo evento (lint pós-edit, bloquear `rm -rf`) | **Hook** (não rule, não skill) |

**Sinais de que NÃO é rule:**
- Tem passos numerados ("1. faça X, 2. faça Y") → é skill
- Tem mais de ~200 linhas → modularize em múltiplos arquivos ou vire skill
- Só faz sentido quando invocado explicitamente → é skill
- Precisa rodar código/script → é hook ou skill
- Vai ser lida só uma vez por mês → é skill (carrega on-demand)

Se chegou aqui, é rule. Continue.

---

## Input esperado

O usuário fornecerá:
- **Tópico** — kebab-case, descritivo (ex: `python-style`, `api-design`, `security`, `notebooks`)
- **Tipo** — `unconditional` (sempre carrega) ou `scoped` (carrega via globs `paths`)
- **Globs** — se scoped, lista de glob patterns (ex: `src/api/**/*.ts`)
- **Escopo** — `projeto` (`.claude/rules/`, default) ou `user` (`~/.claude/rules/`, todas as máquinas)
- **Conteúdo** — as regras propriamente ditas

Se o tipo for ambíguo, pergunte. Se o usuário descrever globs específicos, é scoped por definição. Se não houver globs e o conteúdo se aplica a todo o repo, é unconditional.

---

## Passo a passo

### 1. Decidir tipo

- **Unconditional** se: padrão baseline aplicável a qualquer arquivo do projeto (estilo, segurança, naming geral).
- **Scoped** se: o padrão só faz sentido para arquivos de uma área (ex: rules de testes valem para `tests/**`, rules de notebook valem para `notebooks/**`).

### 2. Decidir destino

| Destino | Caminho | Versionado | Quando usar |
|---|---|---|---|
| Projeto | `.claude/rules/<topic>.md` | Sim (commit) | Default. Padrões da equipe, do repo. |
| User-level | `~/.claude/rules/<topic>.md` | Não | Preferências pessoais que valem em qualquer projeto seu. |

User-level rules são carregadas **antes** das de projeto, então rules de projeto têm prioridade em conflito.

### 3. Criar o arquivo

Crie o diretório se não existir:
```
.claude/rules/
```

Subdiretórios são permitidos para organizar tópicos relacionados:
```
.claude/rules/
├── code-style.md
├── security.md
└── ml/
    ├── training.md
    └── pipelines.md
```

Use o **Template A** (unconditional) ou **Template B/C** (scoped) das seções abaixo.

### 4. Validar globs (se scoped)

Antes de salvar, confirme que os globs casam com arquivos reais:
```bash
ls .claude/rules/<topic>.md       # arquivo existe
# valide globs com find/ls — ex: ls src/api/**/*.ts (em shells com globstar)
```

Se nenhum arquivo casar, a rule nunca vai ativar.

### 5. Verificar via `/memory`

Numa nova sessão Claude Code:
- Rodar `/memory` — rules unconditional aparecem listadas no startup
- Para scoped: abrir um arquivo casando com o glob; a rule só carrega quando Claude lê arquivos do path
- (Opcional) Hook `InstructionsLoaded` registra exatamente quando cada rule entra em contexto

---

## Anatomia da rule

### Unconditional (sem frontmatter `paths`)

```markdown
# Code Style Guidelines

- Use 2-space indentation in all TypeScript files.
- Prefer `const` over `let` when reassignment isn't needed.
- Run `pnpm lint` before committing.
```

Sem frontmatter (ou com frontmatter vazio). Carrega no startup, prioridade igual a `.claude/CLAUDE.md`.

### Scoped (com frontmatter `paths`)

```markdown
---
paths:
  - "src/api/**/*.ts"
---

# API Development Rules

- All API endpoints must include input validation.
- Use the standard error response format.
- Include OpenAPI documentation comments.
```

Carrega só quando Claude lê arquivos casando com algum dos globs em `paths`.

---

## REGRAS — O que FAZER

### Frontmatter

- **Único campo aceito**: `paths` (lista YAML de strings glob).
- Sem `paths` → rule unconditional. **Não** invente outros campos.
- `paths` deve ser uma lista (`- "..."`), mesmo com um único glob.

### Glob patterns suportados

Tabela canônica da documentação oficial:

| Pattern | Casa com |
|---|---|
| `**/*.ts` | Todos os arquivos TypeScript em qualquer diretório |
| `src/**/*` | Todos os arquivos sob `src/` |
| `*.md` | Markdown apenas na raiz do projeto |
| `src/components/*.tsx` | React components em um diretório específico |

**Brace expansion** funciona para casar múltiplas extensões num só pattern:
```yaml
---
paths:
  - "src/**/*.{ts,tsx}"
  - "lib/**/*.ts"
  - "tests/**/*.test.ts"
---
```

### Glob patterns avançados — prefixo e sufixo

Globs casam por **nome completo do arquivo**, não só por extensão ou pasta. Use prefixos (`I*`, `Abstract*`, `Base*`) e sufixos (`*Service.cs`, `*Command.cs`) para targetar **estereótipos arquiteturais** independente de onde o arquivo viva.

Padrão Rosalyn: prefixo + radical + sufixo + extensão. Ex: `IRole`+`Service`+`.cs` → `**/I*Service.cs`.

#### Sufixos (estereótipos por papel)

| Pattern | Casa com (exemplos do projeto) | Estereótipo |
|---|---|---|
| `**/*Command.cs` | `SignInCommand`, `ActivateCommand`, `CreateRoleCommand`, `UpdateRoleCommand`, `DeleteRoleCommand` | CQRS command (record imutável) |
| `**/*CommandValidator.cs` | `UpdateRoleCommandValidator`, `CreateRoleCommandValidator`, `ActivateCommandValidator` | FluentValidation de command |
| `**/*Service.cs` | `MeService`, `AuthService`, `RoleService`, `PlatformSettingsService` | Application service (classe) |
| `**/*Repository.cs` | `RoleRepository`, `CountryRepository`, `ProfileRepository`, `ActivationTokenRepository` | Repositório EF Core (classe) |
| `**/*Endpoints.cs` | `AuthEndpoints`, `MeEndpoints`, `AdminRolesEndpoints`, `PlatformSettingsEndpoints` | Minimal API endpoint group |
| `**/*Exception.cs` | `InvalidCredentialsException`, `ForbiddenException`, `EntityNotFoundException` | Exception de domínio/aplicação |
| `**/*ExceptionHandler.cs` | `ForbiddenExceptionHandler`, `EntityNotFoundExceptionHandler` | Handler ProblemDetails |
| `**/*Configuration.cs` | `ProfileConfiguration`, `RoleConfiguration`, `CountryConfiguration` | `IEntityTypeConfiguration<T>` EF Core |
| `**/*Result.cs` | `MeResult`, `RoleResult`, `CountryResult`, `RolesResult` | DTO de retorno |
| `**/*Seed.cs` | `CountrySeed`, `RolesSeed`, `PlatformSettingsSeed` | Bootstrap/seed de dados |

#### Prefixos (interfaces e bases)

| Pattern | Casa com | Estereótipo |
|---|---|---|
| `**/I*.cs` | Toda interface — `IMeService`, `IRoleRepository`, `IUnitOfWork` | Interface (qualquer papel) |
| `**/Abstract*.cs` | Bases abstratas — `AbstractValidator<T>` derivadas | Classe abstrata |
| `**/*Base.cs` | Bases abstratas — `RepositoryBase<TEntity, TId>` derivadas | Classe abstrata |

#### Prefixo **+** sufixo combinados

Use quando o estereótipo exige ambos.

| Pattern | Casa com | Estereótipo |
|---|---|---|
| `**/I*Service.cs` | `IMeService`, `IRoleService`, `IAuthService` | **Apenas** interfaces de service (não pega `RoleService.cs`) |
| `**/I*Repository.cs` | `IRoleRepository`, `ISessionRepository`, `IProfileRoleRepository` | Contratos de repositório |
| `**/*Command*.cs` | `SignInCommand`, `UpdateRoleCommandValidator`, `CreateRoleCommandHandler` | Família inteira do command (cuidado: pega validator e handler também) |

#### Combinação com pasta (feature + estereótipo)

Glob junta path + estereótipo para escopo cirúrgico.

| Pattern | Casa com | Quando usar |
|---|---|---|
| `**/Auth/*Command.cs` | Só commands da feature Auth | Rule específica para CQRS de Auth |
| `**/Roles/I*Service.cs` | Só interfaces de service de Roles | Rule de contrato da feature Roles |
| `**/AcademiaDev.Application/**/*Validator.cs` | Todos validators da camada Application | Rule de toda a camada |
| `**/AcademiaDev.Domain/Specifications/*.cs` | Apenas specifications | Rule de Specifications pattern |

### Granularidade e organização

- **Um tópico por arquivo**. Nome descritivo (`testing.md`, `api-design.md`, `security.md`), não `rules.md`.
- **Subdiretórios** organizam famílias relacionadas (`ml/`, `frontend/`, `backend/`).
- **Symlinks suportados** para compartilhar entre projetos:
  ```bash
  ln -s ~/shared-claude-rules .claude/rules/shared
  ln -s ~/company-standards/security.md .claude/rules/security.md
  ```

### Tamanho e foco

- Manter cada rule **focada** — alvo de < 200 linhas. Se passar disso, dividir em múltiplos arquivos sob subdiretório.
- **Imperativo direto**: "Use 2-space indentation", não "You should use 2-space indentation".
- **Específico e verificável**: "Run `npm test` before committing" > "Test your changes".

### User-level rules

Para preferências pessoais que valem em todo projeto:
```
~/.claude/rules/
├── preferences.md
└── workflows.md
```

---

## REGRAS — O que NÃO FAZER

- **NÃO** incluir campos `name` ou `description` no frontmatter — eles **não existem** em rules. Rule é markdown puro com `paths` opcional. Se você está pensando em `name`/`description`, você quer uma **skill**, não uma rule.
- **NÃO** escrever workflow/playbook em rule. Workflow tem passos numerados, contexto de invocação, side effects — vira **skill**, sempre. Use `create-skill`.
- **NÃO** duplicar instrução que já está em `CLAUDE.md`. Se já existe lá e é universal, mantenha lá. Rule é para modularizar quando CLAUDE.md está crescendo demais.
- **NÃO** usar globs ambíguos ou conflitantes. Se duas rules casam o mesmo arquivo com instruções contraditórias, Claude escolhe arbitrariamente.
- **NÃO** criar rule unconditional para algo path-específico. Se a regra só vale para `tests/**`, use `paths`. Caso contrário, ela polui contexto sempre.
- **NÃO** criar rule para conteúdo que pertence ao CLAUDE.md (3-5 linhas, sempre relevante, projeto inteiro). Use rule **a partir** do momento em que o tópico merece arquivo próprio.
- **NÃO** ultrapassar ~200 linhas em um único arquivo. Modularize em múltiplos arquivos do mesmo tópico sob subdiretório.
- **NÃO** depender de URLs externas para conteúdo crítico — bundle tudo localmente.
- **NÃO** criar rule sem confirmar que Claude vai realmente lê-la. Para scoped, valide com `ls` que existem arquivos casando os globs. Sem matches, a rule é morta.
- **NÃO** misturar tipos no mesmo arquivo: ou tem `paths` (scoped) ou não tem (unconditional). Não há terceiro estado.
- **NÃO** colocar rules fora de `.claude/rules/` ou `~/.claude/rules/`. Outros caminhos não são descobertos.

---

## Templates

### Template A — Unconditional

Para padrões que se aplicam ao projeto inteiro.

```markdown
# {Título do Tópico}

- {Regra 1 — imperativo, específica, verificável}.
- {Regra 2}.
- {Regra 3}.

## {Subtema opcional, se a rule for densa}

- {Detalhe 1}.
- {Detalhe 2}.
```

Sem frontmatter. Salva em `.claude/rules/{topic}.md`.

### Template B — Scoped (single path)

Para padrões de uma única área do código.

```markdown
---
paths:
  - "{glob-pattern}"
---

# {Título do Tópico}

- {Regra 1 que só faz sentido para arquivos casando com o glob}.
- {Regra 2}.
- {Regra 3}.
```

### Template C — Scoped (múltiplos paths + brace expansion)

Para padrões que cobrem várias áreas relacionadas.

```markdown
---
paths:
  - "src/**/*.{ts,tsx}"
  - "lib/**/*.ts"
  - "tests/**/*.test.ts"
---

# {Título do Tópico}

- {Regra aplicável a todas as áreas listadas}.
- {Regra}.
```

---

## Exemplos canônicos

### Exemplo 1 — Notebooks (scoped, single path)

```markdown
---
paths:
  - "notebooks/**"
---

# Notebook rules

- Treat notebooks as exploratory, not production.
- Prefer small, runnable cells and short outputs.
- Don't import from notebooks into production code.
```

### Exemplo 2 — Production ML (scoped, multiple paths)

```markdown
---
paths:
  - "src/models/churn/**"
  - "src/pipelines/**"
---

# Production ML rules

- Any change must preserve reproducibility.
- Never train on labels that leak future information.
- Log feature distributions before and after every transformation.
```

### Exemplo 3 — Security (unconditional)

```markdown
# Security baseline

- Never log full credentials, tokens, or PII.
- All external HTTP calls must use the shared `httpClient` (timeouts + retries).
- Database queries with user input must use parameterized statements.
```

### Exemplo 4 — FluentValidation (sufixo `*CommandValidator.cs`)

Rule por estereótipo arquitetural — não importa em qual feature o validator viva.

```markdown
---
paths:
  - "**/*CommandValidator.cs"
---

# FluentValidation rules

- Validators devem ser `sealed` e ter construtor sem parâmetros.
- Use `RuleFor(c => c.Prop)` apontando direto para a propriedade — nada de
  expressões compostas dentro do `RuleFor`.
- Toda validação síncrona; não use `WhenAsync`/`MustAsync` para regras que
  pertencem ao domínio (mover para o service).
- `NotEqual(Guid.Empty)` para todos os IDs vindos do request.
- Mensagens de erro em PT-BR apenas quando expostas ao usuário; caso
  contrário, manter padrão do FluentValidation.
```

### Exemplo 5 — Service interfaces (prefixo + sufixo `I*Service.cs`)

Rule só para contratos, sem pegar implementações.

```markdown
---
paths:
  - "**/I*Service.cs"
---

# Service contract rules

- Toda assinatura `async` deve aceitar `CancellationToken ct = default` como
  último parâmetro.
- Pattern `Can{Action}Async` retornando `ValidationResult` antes de cada
  método mutador (`{Action}Async`) — espelha PR-04.
- Métodos read-only retornam `Task<T>` ou `Task<IReadOnlyList<T>>`, nunca
  `Task<List<T>>`.
- Documentar `EventIds` reservados em comentário XML na interface.
```

### Exemplo 6 — Auth feature (pasta + sufixo `**/Auth/*Command.cs`)

Rule para commands de uma feature específica.

```markdown
---
paths:
  - "**/Auth/*Command.cs"
---

# Auth command rules

- Commands são `sealed record` — imutáveis por construção.
- Não exponha `PasswordHash`, `Salt`, ou qualquer secret em command — só
  `Password` cleartext (que é descartado após hashing).
- Inclua sempre o ContextId/SessionId quando o command depende de sessão
  ativa.
```

---

## Verificação

Após criar a rule:

1. **Local correto**: arquivo em `.claude/rules/<topic>.md` (projeto) ou `~/.claude/rules/<topic>.md` (user). Subdiretórios permitidos.
2. **Frontmatter válido** (se scoped): YAML com **apenas** o campo `paths` como lista. Sem `name`, sem `description`.
3. **Globs reais** (se scoped): `ls` confirma que existem arquivos casando ao menos um dos globs.
4. **Tamanho focado**: < 200 linhas. Se maior, dividir em arquivos do mesmo tópico sob subdiretório.
5. **Sem duplicação**: o conteúdo não repete instrução já presente em `CLAUDE.md` ou outra rule.
6. **Rodar `/memory`** numa nova sessão:
   - Unconditional: deve aparecer na listagem de arquivos carregados.
   - Scoped: deve aparecer quando Claude ler um arquivo casando com o glob.
7. **Anti-pattern check**: a rule não tem passos numerados de workflow, não invoca comandos, não tem `name`/`description`. Se tiver, **mover para skill**.
