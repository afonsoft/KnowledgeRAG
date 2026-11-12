# Session memory — 2026-11-12 (E30 merged)

## Estado
- **PR #618 (Epic E29)**: MERGED squash `bcbca16` — 8 SPECs agent memory.
- **PR #622 (Epic E30)**: MERGED squash `e352701` 22:21 UTC — #620 context/timeline/stats + #621 prompt provenance.
- Issues #620/#621 → done + CLOSED. Epic #619 CLOSED. SPECs → Done (PR #623 housekeeping).
- Branch work consolidada; `main` local atualizado p/ `e352701`.

## E30 entregue
- **#620**: `memory_context` (digest session-start determinístico, synthesize opt-in), `memory_timeline` (day/hour/week, OccurredStart??MentionedAt, from>to→400), `memory_stats` (per-bank + agregado global). REST `/api/memory/banks/{key}/context|timeline|stats` + `/api/memory/stats`. SKILL.md: session start abre com memory_context.
- **#621**: `MemoryDocument` += SourcePrompt (defense-screened, cap 2000 + promptTruncated metadata), SourceSessionId, Origin (explicit|passive|import|system, check), PromptHash; idx (BankId,SourceSessionId). Migrations dual. retain dedupe (bank,sess,promptHash)→promptSeen:true antes da extração; doc provenance-only `prov:{sess}:{hash16}`. recall/list += sessionId; provenance em DTOs/recall/timeline (≤500 chars).

## Gates finais
- Unit 1931/1931 | Integration 375/375 | Blackbox 1/1 | Parity 3/3 | format limpo | CI todos verdes (SonarCloud/CodeQL/Qodana/Snyk/Trivy/Docker/UI).
- Flake conhecido: `Semantic_Mode_PreservesVectorResults` (timing sob carga paralela — passa isolado; pré-existente).

## Próximo Epic (gaps gentle-ai restantes)
- memory_bank_merge (name drift), RDD-lite no knowledge-review (candidato congelado + lentes por risco), deny-list de paths (.env/.ssh), bench de jornada (turns/custo), delegation trigger table.

## Bugs conhecidos
- xunit 2.9.3 não honra SkipException → early-return; packages.lock.json sujos por restore local — NÃO commitar; deleções bootstrap wwwroot/lib pré-existentes do ambiente.
