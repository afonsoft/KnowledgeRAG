# SPEC-20261022: context_recovery wiki enrichment + default elision+summary

| Campo | Valor |
|---|---|
| Status | Approved |
| Ticket | #602 |
| Autor | Devin |
| Data | 2026-10-22 |

## Contexto

Duas melhorias nas tools `context_*` (SPEC-20261004-context-snapshots), pedidas
pelo mantenedor:

1. O default de compactação é `none` em `context_save` (`compact`) e
   `context_recovery` (`strategy`); `context_compact` já usa `elision+summary`.
   Padronizar o default para `elision+summary` nas três — recuperar/salvar sem
   parâmetro já deve trazer o contexto compactado ao budget.
2. `context_recovery` deve chamar internamente `read_wiki_structure`
   (DeepWiki upstream) para o `repo` e devolver a estrutura da wiki junto do
   contexto — on-boarding de sessão em uma chamada só. Wiki indisponível não
   pode derrubar a recuperação.

Bug encontrado durante análise: `context_save` declara o arg `compact` no
input schema, mas o handler lia `strategy` — o arg documentado era ignorado.
Corrigido nesta SPEC lendo `compact` (com fallback silencioso a `strategy`
por compatibilidade).

## Requisitos

### RF-1 — Default `elision+summary`

- `context_save.compact` e `context_recovery.strategy` passam a default
  `elision+summary` quando o arg é omitido; `context_compact` já está correto.
- `none`/`elision` explícitos continuam válidos (enum inalterado).
- Descriptions dos input schemas refletem o novo default.
- Comportamento sob budget é inalterado: `Elide` só toca items elegíveis
  (>150 tokens estimados, fora do keep-tail) e o summarizer só roda quando
  `TotalTokens > tokenBudget` — payloads pequenos retornam idênticos.
- Fix do arg: handler de `context_save` lê `compact` (schema), com fallback
  a `strategy` por compatibilidade retroativa.

### RF-2 — Wiki structure em `context_recovery`

- Resposta structured ganha campo `wiki` (string|null): texto da
  `read_wiki_structure` do upstream DeepWiki para `repoName = <repo.Key>`.
- Caminho coberto quando DeepWiki indisponível: `wiki = null`, resposta
  normal, sem erro — incluindo `DeepWiki:Enabled=false`, toggle de
  integração off, ausência de key (usa endpoint público), upstream em erro
  (`IsError`), timeout ou exceção.
- Gate idêntico ao provider: `DeepWikiOptions.Enabled` +
  `IIntegrationStateService.IsEnabledAsync(deepwiki)`.
- Secret override por API key igual ao passthrough
  (`IApiKeyChatSettingsService.GetIntegrationSecretAsync`) — chave privada
  do chamador vale também para a wiki.
- Timeout dedicado `Context:WikiTimeoutSeconds` (default 15s) — enrichment
  não pode travar a recuperação sob upstream lento (`DeepWiki:TimeoutSeconds`
  é 60s).
- Fetch fora do `ContextCacheService`: falha transitória do upstream não
  contamina a entrada cacheada com `wiki=null`; `stats.cacheHit` continua
  refletindo só a porção snapshot/compaction.

## Fora de escopo

- `read_wiki_contents` (corpo completo) — só a estrutura.
- Wiki em `context_save`/`context_compact` ou na listagem REST de snapshots.
- Cache dedicado da estrutura da wiki (TTL/invalidação por repo já existem
  para a resposta de recovery; a wiki viaja fora do cache por escolha).

## Arquivos

- `src/KnowledgeHub.Server/Mcp/ToolProviders/ContextToolsProvider.cs`
- `src/KnowledgeHub.Server/Context/ContextOptions.cs`
- `tests/KnowledgeHub.Tests.Integration/McpContractTests.cs` (repin de schema)
- `tests/KnowledgeHub.Tests.Integration/ContextToolsTests.cs`

## Critérios de aceite

- `context_recovery` sem `strategy` retorna items compactados com
  `stats.strategy`-equivalente `elision+summary` no caminho padrão.
- Com DeepWiki habilitado e repo conhecido: `wiki` contém a estrutura; com
  DeepWiki desabilitado/erro: `wiki=null` e resposta 200/ok.
- `context_save` respeita o arg documentado `compact` e o novo default.
- Contrato MCP pinado atualizado; suite de context tools verde.
