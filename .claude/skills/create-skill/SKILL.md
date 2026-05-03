---
name: create-skill
description: >
  Cria uma nova skill para o Claude Code neste repositório. Use quando o
  usuário pedir para criar, adicionar, gerar ou scaffold uma skill, comando,
  slash command, ou automação reutilizável. Também use quando disser
  "quero ensinar o Claude a fazer X" ou "criar um atalho para Y".
---

# Criar Skill no Claude Code

Cria skills seguindo as convenções da plataforma e as boas práticas documentadas.

## Input esperado

O usuário fornecerá:
- **Nome da skill** — kebab-case (ex: `deploy-k8s`, `run-tests`)
- **O que a skill faz** — descrição do comportamento desejado
- **Quando usar** — contexto de ativação (triggers)

Se o nome não for fornecido, derive do propósito. Se ambíguo, pergunte.

## Passo a passo

### 1. Criar o diretório

```
.claude/skills/{nome-da-skill}/
```

### 2. Criar o SKILL.md

O arquivo **deve** ter frontmatter YAML + corpo markdown. Este é o único arquivo obrigatório.

### 3. Criar arquivos de referência (se necessário)

Arquivos auxiliares na mesma pasta, referenciados via links relativos no SKILL.md:
```markdown
Veja [guia avançado](./ADVANCED.md) para detalhes.
```

### 4. Validar

- Nome do diretório deve ser **idêntico** ao campo `name` do frontmatter
- Testar invocando a skill via `/nome-da-skill` ou descrição natural

---

## Anatomia do SKILL.md

```yaml
---
name: nome-da-skill
description: >
  [O que faz]. [Quando usar — com palavras-chave de trigger].
---

# Título da Skill

## O que fazer
[Instruções claras e objetivas]

## O que NÃO fazer
[Anti-patterns, armadilhas, erros comuns a evitar]

## [Seções adicionais conforme necessário]
```

---

## REGRAS — O que FAZER

### Frontmatter

- **`name`**: kebab-case, somente letras minúsculas, números e hífens. Max 64 caracteres.
  - Bom: `create-project`, `run-migrations`, `deploy-k8s`
  - O nome do diretório DEVE ser idêntico ao `name`
- **`description`**: Entre 50 e 1024 caracteres. É o campo **mais importante** — é tudo que o Claude vê no startup para decidir se a skill é relevante.
  - Estrutura obrigatória: `[O que faz]. [Quando usar].`
  - Incluir sinônimos e variações do que o usuário pode dizer
  - Usar gatilhos explícitos: "Use quando...", "Também use quando..."

### Corpo (markdown)

- **Ser direto e conciso** — o SKILL.md inteiro deve ter menos de 500 linhas (~5k tokens). Se precisar de mais, use arquivos de referência.
- **Incluir seção "O que NÃO fazer"** — anti-patterns são tão importantes quanto as instruções. Eles evitam que o Claude repita erros comuns, invente funcionalidades, ou quebre convenções.
- **Incluir templates/exemplos** quando o output tem formato específico.
- **Referenciar arquivos externos** para conteúdo extenso:
  ```markdown
  Para detalhes sobre configuração, veja [CONFIG.md](./CONFIG.md).
  ```
  Arquivos referenciados só são carregados quando o Claude precisa deles — custo zero de contexto até serem lidos.
- **Usar linguagem imperativa** — "Crie o arquivo", "Execute o comando", não "Você deve criar".
- **Escrever na língua do projeto** — se o projeto é PT-BR, a skill deve ser PT-BR.
- **Incluir steps de validação/verificação** no final — "Execute X para confirmar que funcionou".

### Organização de arquivos

```
nome-da-skill/
├── SKILL.md              # Obrigatório — instruções principais (< 500 linhas)
├── REFERENCE.md          # Opcional — documentação detalhada
├── TEMPLATES.md          # Opcional — templates reutilizáveis
└── scripts/              # Opcional — scripts utilitários
    └── helper.sh
```

- Arquivos de referência = progressive disclosure. Só carregam quando referenciados.
- Scripts executam via bash sem entrar no contexto — só o output volta.

### Description — exemplos de boas descriptions

```yaml
# BOM — específico, com triggers
description: >
  Cria um novo projeto .NET na solution AcademiaPayAi. Suporta webapi, worker
  e classlib. Use quando o usuário quiser adicionar um projeto, serviço ou
  microsserviço à solution.

# BOM — múltiplos triggers
description: >
  Executa e analisa testes unitários e de integração. Use quando o usuário
  pedir para rodar testes, verificar cobertura, ou validar que uma mudança
  não quebrou nada.

# BOM — ação clara + contexto
description: >
  Gera Dockerfiles e manifests Kubernetes para os serviços da solution.
  Use quando o usuário mencionar deploy, container, Docker, K8s, ou Helm.
```

---

## REGRAS — O que NÃO FAZER

### Frontmatter

- **NÃO** usar camelCase, PascalCase ou snake_case no `name`. Somente kebab-case.
  - Errado: `createProject`, `create_project`, `CreateProject`
- **NÃO** usar "anthropic" ou "claude" no nome — são palavras reservadas.
- **NÃO** começar ou terminar com hífen, nem usar hífens consecutivos.
  - Errado: `-my-skill`, `my-skill-`, `my--skill`
- **NÃO** escrever descriptions genéricas ou vagas.
  - Errado: `"Ferramenta de processamento"`, `"Ajuda com tarefas diversas"`
  - Errado: `"Utility"`, `"Helper tool"`
- **NÃO** ultrapassar 1024 caracteres na description.
- **NÃO** incluir tags XML na description ou no name.

### Corpo

- **NÃO** colocar tudo no SKILL.md — se passar de 500 linhas, mova conteúdo para arquivos de referência.
- **NÃO** omitir a seção de anti-patterns — toda skill deve dizer o que NÃO fazer. Sem isso, o Claude vai inventar abordagens que podem quebrar convenções do projeto.
- **NÃO** incluir documentação de API extensa inline — mova para REFERENCE.md.
- **NÃO** incluir snippets de código grandes que poderiam ser scripts — mova para `scripts/`.
- **NÃO** escrever em tom passivo ou explicativo ("Este skill é usado para..."). Use imperativo direto ("Crie o arquivo...", "Execute...").
- **NÃO** duplicar informação que já está no CLAUDE.md — referencie em vez de copiar.
- **NÃO** assumir que o Claude sabe convenções do projeto sem explicitá-las — seja específico sobre nomes, paths, padrões.
- **NÃO** criar skills sem step de verificação — sempre termine com "como validar que funcionou".
- **NÃO** depender de URLs externas para conteúdo crítico — bundle tudo localmente.

### Organização

- **NÃO** criar o SKILL.md fora da estrutura `.claude/skills/{name}/SKILL.md`.
- **NÃO** usar nomes de diretório diferentes do campo `name` no frontmatter.
- **NÃO** criar arquivos de referência que não são linkados no SKILL.md — eles nunca serão lidos.
- **NÃO** criar skills que fazem várias coisas não relacionadas — uma skill = uma responsabilidade.

---

## Template inicial

Use este template como ponto de partida para novas skills:

```yaml
---
name: {nome-kebab-case}
description: >
  {O que a skill faz — 1-2 frases}. Use quando {trigger 1}, {trigger 2},
  ou {trigger 3}. Também use quando {variação}.
---

# {Título da Skill}

{Parágrafo breve explicando o propósito.}

## Input esperado

O usuário fornecerá:
- **{param1}** — {descrição}
- **{param2}** — {descrição}

Se {param} não for especificado, {comportamento padrão ou pergunte}.

## O que fazer

### 1. {Primeiro passo}
{Instruções}

### 2. {Segundo passo}
{Instruções}

### 3. {Verificação}
{Como validar que funcionou}

## O que NÃO fazer

- **NÃO** {anti-pattern 1} — {motivo}
- **NÃO** {anti-pattern 2} — {motivo}
- **NÃO** {anti-pattern 3} — {motivo}
```

---

## Verificação

Após criar a skill:
1. Confirmar que o diretório `.claude/skills/{name}/` existe com `SKILL.md` dentro
2. Confirmar que o campo `name` no frontmatter é idêntico ao nome do diretório
3. Confirmar que a description tem entre 50 e 1024 caracteres
4. Confirmar que o SKILL.md tem menos de 500 linhas
5. Confirmar que existe uma seção de anti-patterns ("O que NÃO fazer")
6. Testar invocando a skill com uma frase natural que deveria triggar a description
