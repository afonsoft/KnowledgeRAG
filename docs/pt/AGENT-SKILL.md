# Agent Skill

O hub distribui três [Agent Skills](https://agentskills.io) em `skills/` —
instruções instaláveis que ensinam qualquer agente de código a usar o hub
corretamente sem ler este repositório.

```bash
npx skills add afonsoft/KnowledgeRAG -s knowledge-hub
```

| Skill | O que ensina |
|---|---|
| `knowledge-hub` | Ciclo de sessão: `context_recovery` no início, `context_save` no fim; grounding primeiro (`search_knowledge`/`memory_recall` antes de assumir); `memory_retain` proativo; protocolo de correção `Correction: <tópico>`; crédito `📚 From KnowledgeHub`; wiring MCP por harness. |
| `knowledge-hub-docs` | Referência operacional: endpoints, auth `aft_*`, catálogo de tools, transportes, jobs, troubleshooting. **Gerada** de `CLAUDE.md` + `docs/en/API.md` via `scripts/generate-skills.sh` — nunca editada à mão; CI falha se dessincronizar. |
| `knowledge-hub-architect` | Planejador de adoção: mapeia onde seu conhecimento vive para `SourceType`s, propõe banks de memória, schema de tags e um baseline de eval inicial. |

## Wiring

Snippets de config MCP por harness em `skills/knowledge-hub/wiring/`
(claude-code, cursor, devin, opencode, windsurf). Todos usam Streamable
HTTP `/mcp` + `Authorization: Bearer AFT_KEY_HERE`; harnesses sem suporte a
headers usam `/mcp/sse?access_token=...`.

## Contrato de freshness

`skills/knowledge-hub-docs/SKILL.md` é gerada das fontes canônicas —
edite as seções `<!-- skill-docs:begin/end -->` em `CLAUDE.md` /
`docs/en/API.md` e rode:

```bash
scripts/generate-skills.sh          # regenera
scripts/generate-skills.sh --check  # checagem de CI (non-zero se stale)
```

`tests/KnowledgeHub.Tests.Unit/Skills/SkillsFreshnessTests.cs` garante:
freshness do arquivo gerado, frontmatter válido por skill, JSON de wiring
parseável com placeholder `AFT_KEY_HERE` e zero segredos reais em `skills/`.
