# Short-term memory — session state (overwritten each session, ≤100 lines)

- **Sessão atual**: análise `vectorize-io/hindsight` → 8 SPECs **Approved** em `.specs/SPEC-20261111-*` + **Epic E29 #609** (issues #610-#617, feature+todo; #611-613 blocked-by #610). Ordem: #616/#614 → #610 → #611 → #612 → #613 → #615/#617.
- **Conceito-chave**: `MemoryBank` novo (N banks/key); ciclo retain→consolidação→reflect→pages; pages projetadas no vault Obsidian `_memory/`.
- **Refs**: clone `/tmp/hindsight`; knowledge atualizado `.claude/knowledge/hindsight-patterns.md`; long-term `.claude/memory/20261111-memory.md`.
- **Carry-over (2026-10-07)**: SPEC-20261007-knowledge-review `Approved` #567, branch `feature/devin-20261007-knowledge-review`; deploy `rag.afonsoft.dev` ok; `.github/workflows` protegido.
- **Baseline**: build 0 warnings · 1129+ unit · 306+ integration (última verificação conhecida).
