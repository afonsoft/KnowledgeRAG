# SPEC-20260927-chunk-window-retrieval-and-autocut

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chunk-window-retrieval-and-autocut` |
| Type | `Feature` |
| Stack | `.NET 10 / SQLite-Vec / PgVector / FTS5 / System.Text.Json` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `#270` |
| Status | `Done` |

## 1. User Story

**As a** usuário ou agente de IA consultando respostas via `ask_knowledge` ou `search_knowledge`
**I want** que chunks recuperados com alta relevância expandam sua janela de contexto para incluir seus blocos vizinhos adjacentes no mesmo documento e que a lista de resultados corte caudas irrelevantes via Autocut
**So that** o LLM receba parágrafos e seções completas sem quebra de raciocínio no meio de frases e sem poluição de chunks irrelevantes na cauda longa do TopK.

**Problem context:**
Existe um dilema clássico na engenharia de RAG:
- Chunks pequenos (150 a 300 tokens) geram os vetores de embedding mais precisos e densos, evitando que múltiplos tópicos misturados diluam o score de similaridade.
- Porém, na hora de sintetizar a resposta, o LLM frequentemente precisa ler o contexto circundante (a frase anterior, o parágrafo posterior ou a tabela explicativa) para não alucinar ou interpretar um fragmento fora de contexto.
Além disso, definir um `TopK` fixo (ex.: sempre 5 ou 10) é ineficiente: para perguntas simples com 1 chunk excelente, os outros 9 são ruído puro; para perguntas abrangentes, 5 pode ser insuficiente.
Inspirado na arquitetura do **Weaviate Verba** (`goldenverba/components/retriever/WindowRetriever.py`), esta SPEC implementa duas inovações no Knowledge MCP Hub:
1. **Window Retrieval (Expansão de Janela de Chunks):** Para cada chunk de alta relevância (score normalizado acima de um threshold, ex.: 80%), o recuperador busca os chunks adjacentes dentro de uma janela simétrica ($chunk\_index \pm W$, onde $W \in [1..3]$) no mesmo documento-pai e costura o texto contínuo antes de entregar ao LLM.
2. **Autocut (Poda Dinâmica de Cauda por Derivada de Score):** Em vez de retornar um TopK rígido, o algoritmo analisa a curva descendente de pontuação e corta a lista no primeiro salto/degrau significativo de queda de relevância (elbow drop), garantindo que apenas o cluster de chunks coesos chegue à etapa de geração.

## 2. Scope

**In scope:**
- **Atributos de Sequência no Chunk (`ChunkSequenceMetadata`):**
  - Garantir que cada chunk indexado possua `ChunkIndex` (inteiro sequencial 0, 1, 2... dentro do documento-pai) e `DocumentId`.
- **Motor de Expansão de Janela (`WindowContextExpander`):**
  - Parâmetros configuráveis: `WindowSize` (default: 1, range 0–3) e `WindowThresholdPercent` (default: 80%).
  - Para chunks com score normalizado $\ge 0.80$, coleta os IDs de chunks vizinhos $[Index - W, Index + W]$.
  - Executa uma única query otimizada `WHERE DocumentId = @docId AND ChunkIndex IN (@indexes)` para resgatar os vizinhos.
  - Ordena e une os textos dos chunks do mesmo documento, eliminando sobreposições (`dedup window merging`).
- **Algoritmo Autocut de Limite Dinâmico (`AutocutFilter`):**
  - Modos de limite: `Fixed` (TopK tradicional) ou `Autocut` (sensibilidade configurável $N \in [1..3]$).
  - O Autocut calcula a diferença de pontuação consecutiva $\Delta_i = Score_i - Score_{i+1}$. Quando detecta o $N$-ésimo salto abrupto ou queda relativa $> 40\%$ da média, trunca a lista ali.
- **Configuração e Integração:**
  - Opções em `RetrievalOptions` e suporte em `SearchKnowledgeTool` e `AskKnowledgeTool`.
  - Exposição de flag na UI de Playground e Settings para ativar `Window Retrieval` e `Autocut`.

**Out of scope:**
- Reescrita do chunker Markdown base (o chunker atual já pode emitir `ChunkIndex`).
- Expansão de janela entre documentos diferentes (a janela é restrita ao mesmo documento-pai).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Retrieval/`:
  - `WindowContextExpander.cs`: motor de expansão de vizinhos e stitching de texto.
  - `AutocutFilter.cs`: algoritmo de corte dinâmico de score.
  - `HybridRetriever.cs`: incorporação do Autocut pós-RRF e da expansão de janela antes do retorno final.
- `src/KnowledgeHub.Server/Data/Entities/KnowledgeChunkEntity.cs`: campo `ChunkIndex` no índice.
- `src/KnowledgeHub.McpEngine/Tools/SearchKnowledgeTool.cs` e `AskKnowledgeTool.cs`: novos parâmetros.
- `tests/KnowledgeHub.Tests.Unit/Retrieval/`: testes unitários para Autocut e Window Expander.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Retrieval/HybridRetriever.cs`
- `src/KnowledgeHub.Server/Retrieval/RrfRanker.cs`
- `src/KnowledgeHub.Server/Services/LlmAnswerSynthesisService.cs`

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Retrieval/WindowContextExpander.cs            (create)
src/KnowledgeHub.Server/Retrieval/AutocutFilter.cs                   (create)
src/KnowledgeHub.Server/Retrieval/RetrievalWindowOptions.cs           (create)
src/KnowledgeHub.Server/Retrieval/HybridRetriever.cs                 (modify)
src/KnowledgeHub.Server/Services/LlmAnswerSynthesisService.cs        (modify)
src/KnowledgeHub.McpEngine/Tools/SearchKnowledgeTool.cs              (modify)
src/KnowledgeHub.McpEngine/Tools/AskKnowledgeTool.cs                 (modify)
tests/KnowledgeHub.Tests.Unit/Retrieval/AutocutFilterTests.cs        (create)
tests/KnowledgeHub.Tests.Unit/Retrieval/WindowContextExpanderTests.cs(create)
```

## 4. Requirements

### RF-001: Sequenciamento Numérico de Chunks por Documento
- **Description:** Durante a etapa de chunking na ingestão, cada chunk gerado a partir de um documento deve receber um `ChunkIndex` estritamente sequencial (0, 1, 2, 3...) persistido no banco de dados.
- **Rules:**
  - O `ChunkIndex` deve possuir índice composto com `DocumentId` (`IX_KnowledgeChunks_DocumentId_ChunkIndex`) para busca ultra-rápida de vizinhos.
- **Input → Output:** Documento dividido em 5 pedaços → Chunks gravados com índices de 0 a 4.

### RF-002: Expansão de Janela de Vizinhos (Window Retrieval)
- **Description:** Após a recuperação dos chunks candidatos pelo índice híbrido, o sistema deve identificar os chunks com score de alta relevância e buscar seus vizinhos.
- **Rules:**
  - Apenas chunks com score normalizado $\ge \text{WindowThresholdPercent}$ (default 80%) disparam expansão.
  - Para um chunk de índice $K$ com janela $W=1$, são incluídos $K-1$ e $K+1$ (se existirem no mesmo documento).
  - Vários chunks vizinhos recuperados do mesmo documento são costurados em ordem cronológica de `ChunkIndex` formando um único `EnrichedContextBlock`.
- **Input → Output:** Chunk #2 atinge score 0.91 → Sistema resgata chunks #1, #2 e #3 e entrega contexto unificado ao LLM.

### RF-003: Algoritmo Autocut para Poda de Cauda
- **Description:** Quando o modo de limite estiver em `Autocut`, a quantidade de chunks retornados deve ser determinada dinamicamente pela curvatura de pontuação.
- **Rules:**
  - As pontuações são normalizadas no intervalo $[0, 1]$.
  - O algoritmo rastreia quedas consecutivas: $\text{Drop}_i = Score_i - Score_{i+1}$.
  - Se a sensibilidade for $N=1$, o corte é efetuado no primeiro ponto de inflexão onde a queda for maior que a média global de diferenças, descartando chunks residuais de baixa pontuação.
  - O número de chunks retornados fica sempre entre 1 e o limite máximo de segurança `MaxClamp` (ex.: 20).
- **Input → Output:** Scores: `[0.95, 0.92, 0.90, 0.45, 0.41, 0.38]` com Autocut $N=1$ → Trunca após 0.90, retornando apenas os 3 primeiros.

### RF-004: Parametrização em Chamadas MCP e Playground
- **Description:** Permitir aos clientes MCP e usuários do Playground ligar/desligar a expansão de janela e o Autocut.
- **Rules:**
  - Parâmetros: `windowSize` (int, 0 a 3, default 1), `limitMode` ("Autocut" | "Fixed", default "Autocut"), `autocutSensitivity` (int, 1 a 3, default 1).
  - Chamadas sem esses parâmetros usam os defaults definidos no `appsettings.json`.

## 5. API Contract (if applicable)

Chamada MCP `search_knowledge`:
```json
{
  "query": "como funciona o loop de agentes",
  "topK": 10,
  "limitMode": "Autocut",
  "autocutSensitivity": 1,
  "windowSize": 1
}
```

Resposta MCP:
```json
{
  "totalMatches": 2,
  "limitModeApplied": "Autocut",
  "items": [
    {
      "chunkId": "chk_100",
      "documentId": "doc_55",
      "chunkIndex": 2,
      "score": 0.94,
      "title": "Agent Loop Architecture",
      "content": "O loop principal executa...",
      "windowExpanded": true,
      "expandedChunkIndices": [1, 2, 3],
      "expandedContent": "[Contexto anterior: Inicialização de estado...]\nO loop principal executa...\n[Contexto seguinte: Tratamento de exceção de tool call...]"
    }
  ]
}
```

## 6. Acceptance Criteria

- [x] **Given** um documento com 5 chunks (0 a 4) indexados **when** o chunk #2 é retornado com score 0.88 e `windowSize = 1` **then** os chunks #1, #2 e #3 são carregados e combinados em `expandedContent`.
- [x] **Given** os chunks #2 e #3 ambos retornados com alta pontuação pelo vetor **when** a expansão de janela $W=1$ roda para ambos **then** a união dos índices é $\{1, 2, 3, 4\}$ sem duplicações de texto.
- [x] **Given** uma lista de scores `[0.96, 0.94, 0.91, 0.40, 0.38, 0.20]` **when** `AutocutFilter.Apply(results, sensitivity: 1)` é executado **then** a lista é podada contendo exatamente os 3 primeiros itens.
- [x] **Given** `windowSize = 0` **when** a busca roda **then** nenhuma query adicional de vizinhos é despachada e o chunk original é preservado intacto.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Chunk no início do documento (index 0) | index = 0, window = 1 | Busca apenas vizinho posterior (index 1), sem tentar index -1 |
| Chunk no final do documento | index = max, window = 1 | Busca apenas vizinho anterior (index max-1) |
| Todos os chunks possuem scores idênticos | [0.8, 0.8, 0.8, 0.8] | Autocut não encontra drop e retorna até o TopK fixo |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** Examinar `KnowledgeChunkEntity.cs` e mapeamento de chunks na ingestão.
- [x] **T2 — Data Schema:** Garantir índice de `ChunkIndex` no DbContext e migração se necessário.
- [x] **T3 — Autocut:** Implementar `AutocutFilter.cs` com testes de corte de curva de distribuição.
- [x] **T4 — Window Expander:** Implementar `WindowContextExpander.cs` com merge contíguo de texto.
- [x] **T5 — Pipeline Integration:** Integrar no `HybridRetriever.cs` e `LlmAnswerSynthesisService.cs`.
- [x] **T6 — MCP & Tests:** Atualizar ferramentas MCP e validar testes unitários completos.

## 8. Organization Guardrails

- **Controle de Tokens:** A expansão de janela deve respeitar um teto máximo de tokens de contexto por documento (clamp de 2000 tokens) para evitar sobrecarga de contexto.
- **Cache:** A expansão de janela deve aproveitar o cache em memória de documentos para não penalizar latência com round-trips desnecessários.

## 9. Definition of Done

- [x] Todos os requisitos (RF-001 a RF-004) implementados.
- [x] Testes unitários do Autocut cobrindo diferentes distribuições de score (degrau único, declínio suave, cauda ruidosa).
- [x] Testes unitários do Window Expander cobrindo fronteiras (início, meio e fim de documento).
- [x] Integração no pipeline RAG sem quebra de testes prévios.
