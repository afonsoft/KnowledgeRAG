# SPEC-20260927-ragflow-vision-layout-chunking

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `ragflow-vision-layout-chunking` |
| Type | `Feature` |
| Stack | `.NET 10 / C# 12 / System.Text.Json / Document Layout Analysis / Vision LLM Bridge` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `#278` |
| Status | `Done` |

## 1. User Story

**As a** engenheiro de dados ou analista consultando documentos técnicos e relatórios financeiros no Knowledge MCP Hub
**I want** que tabelas complexas, relatórios em múltiplas colunas e diagramas em PDFs não sejam cortados no meio por limites cegos de caracteres
**So that** o RAG preserve a integridade estrutural das tabelas como blocos atômicos com seus cabeçalhos contextuais, garantindo respostas 100% precisas em dados tabulares e layouts complexos.

**Problem context:**
Modelos de chunking tradicionais (baseados em contagem fixa de caracteres ou quebras ingênuas de linha) destroem dados tabulares: se uma tabela de 50 linhas for fatiada a cada 500 caracteres, as linhas inferiores perdem a informação do cabeçalho da coluna, tornando os vetores de embedding semanticamente inúteis ou alucinatórios. Além disso, em PDFs com duas ou três colunas de texto, leitores ingênuos leem linha por linha horizontalmente, misturando parágrafos de colunas diferentes.
Inspirado na abordagem pioneira do **RAGFlow** (motor de parsing profundo baseado em análise de layout e visão computacional), esta SPEC introduz o `VisionLayoutTextChunker` no Knowledge MCP Hub. O chunker reconhece blocos de layout estruturados (tabelas, figuras, caixas de destaque, seções de colunas múltiplas) e aplica três regras de ouro:
1. **Atomicidade de Tabelas:** Tabelas completas são mantidas íntegras como chunks únicos ou, se excederem o tamanho máximo, são divididas por linhas repetindo obrigatoriamente a linha de cabeçalho (`Header Preservation`) em cada sub-bloco.
2. **Desentrelaçamento de Colunas:** Leitura na ordem real de fluxo de leitura vertical por coluna antes de seguir para a próxima.
3. **Anotação de Imagens e Diagramas:** Figuras e gráficos recebem transcrição de texto via modelo multimodal (`Vision LLM`) e são indexados como contexto descritivo.

## 2. Scope

**In scope:**
- Implementação de `VisionLayoutTextChunker` (`ITextChunker`) em `src/KnowledgeHub.Server/Ingestion/Chunking/`.
- **Tabela Atômica com Preservação de Cabeçalho:**
  - Detecção de tabelas Markdown (`|---|`) e HTML (`<table>`).
  - Algoritmo de fatiamento seguro de tabelas: se uma tabela tiver 100 linhas e o chunk comportar 25, gera 4 chunks, cada um contendo as linhas de cabeçalho (`Row 0` e delimitador) no topo.
  - Injeção de metadados: `chunk_type: "table"`, `table_rows_count`, `table_columns`.
- **Leitura Respeitosa de Colunas (Multi-Column Layout Awareness):**
  - Ordenação top-down por coluna em arquivos processados por layout analysis.
- **Enriquecimento Multimodal de Imagens/Diagramas:**
  - Suporte a anotação visual via `IChatClient` (quando o conector de origem extrair imagens de PDFs ou relatórios).
  - Geração de texto alternativo semântico descritivo indexado junto ao documento.
- Configuração em `ChunkingOptions`:
  - `PreserveTableHeaders`: bool (default `true`).
  - `MaxTableChunkRows`: int (default `30`, clamp `10–100`).
  - `EnableVisionCaptioning`: bool (default `false`).
- Seleção automática no `ChunkerSelector` quando o tipo de documento contiver tabelas ou layout rico.

**Out of scope:**
- Treinamento local de modelos de deep learning de visão (utiliza OCR/Layout outputs dos conectores ou APIs multimodais já suportadas).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Ingestion/Chunking/`:
  - `VisionLayoutTextChunker.cs`: implementação do chunker.
  - `TableChunkSplitter.cs`: fatiamento de tabelas com retenção de cabeçalhos.
  - `ChunkerSelector.cs`: roteamento para o novo chunker.
- `src/KnowledgeHub.Server/Configuration/ChunkingOptions.cs`: campos de configuração.
- `tests/KnowledgeHub.Tests.Unit/Chunking/`: testes unitários com tabelas longas e layouts de colunas.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Ingestion/Chunking/ITextChunker.cs`
- `src/KnowledgeHub.Server/Ingestion/Chunking/ChunkerSelector.cs`
- `src/KnowledgeHub.Server/Ingestion/Chunking/MarkdownTextChunker.cs`

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Ingestion/Chunking/VisionLayoutTextChunker.cs (create)
src/KnowledgeHub.Server/Ingestion/Chunking/TableChunkSplitter.cs      (create)
src/KnowledgeHub.Server/Ingestion/Chunking/ChunkerSelector.cs        (modify)
src/KnowledgeHub.Server/Configuration/ChunkingOptions.cs             (modify)
tests/KnowledgeHub.Tests.Unit/Chunking/TableChunkSplitterTests.cs    (create)
tests/KnowledgeHub.Tests.Unit/Chunking/VisionLayoutChunkerTests.cs  (create)
```

## 4. Requirements

### RF-001: Detecção e Isolamento Atômico de Tabelas
- **Description:** Blocos de tabela no texto não devem ser fracionados aleatoriamente no meio de uma linha.
- **Rules:**
  - O analisador identifica o início e fim de blocos tabulares (`| ... |` ou `<table>...</table>`).
  - O bloco de tabela nunca é intercalado com parágrafos de texto adjacentes no mesmo chunk.
- **Input → Output:** Texto com parágrafo + tabela + parágrafo → Chunk 1 (texto), Chunk 2 (tabela atômica), Chunk 3 (texto).

### RF-002: Fatiamento de Tabelas Longas com Repetição de Cabeçalho
- **Description:** Quando uma tabela exceder `MaxTableChunkRows`, ela deve ser dividida em múltiplos chunks preservando a linha de cabeçalho original em todos eles.
- **Rules:**
  - `HeaderRow` e `SeparatorRow` da tabela original são gravados no topo de cada chunk resultante.
  - Os chunks subsequentes recebem a anotação `[Continuação da Tabela - Linhas X a Y]`.
- **Input → Output:** Tabela de 80 linhas com limite 30 → 3 chunks contendo o cabeçalho original e as respectivas fatias de dados.

### RF-003: Metadados Estruturais para Busca Vetorial
- **Description:** Chunks gerados por esse analisador devem incluir tags no campo `MetadataJson`.
- **Rules:**
  - `is_table = true`, `parent_section_title`, `table_headers = ["Coluna A", "Coluna B"]`.
  - Permite que a busca vetorial ou híbrida priorize chunks tabulares quando a pergunta contiver termos numéricos ou agregados ("qual o valor total", "tabela de preços").

## 5. API Contract (if applicable)

Configuração no `appsettings.json`:
```json
{
  "Ingestion": {
    "Chunking": {
      "DefaultStrategy": "VisionLayout",
      "PreserveTableHeaders": true,
      "MaxTableChunkRows": 30,
      "EnableVisionCaptioning": false
    }
  }
}
```

## 6. Acceptance Criteria

- [x] **Given** um documento contendo uma tabela Markdown de 60 linhas **when** fatiado pelo `VisionLayoutTextChunker` com limite de 25 linhas **then** cada chunk gerado possui os cabeçalhos das colunas preservados nas primeiras linhas.
- [x] **Given** uma tabela com 10 linhas que cabe no tamanho de chunk **when** processada **then** ela permanece inteira como um único chunk sem divisões.
- [x] **Given** um texto com layout de duas colunas extraído via layout analysis **when** chunked **then** o conteúdo da coluna da esquerda é processado integralmente antes do conteúdo da coluna da direita.

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** Analisar implementação de `ITextChunker` e `MarkdownTextChunker`.
- [x] **T2 — Table Splitter:** Implementar `TableChunkSplitter.cs` com lógica de extração de header e split por linhas.
- [x] **T3 — Vision Chunker:** Implementar `VisionLayoutTextChunker.cs` e integrá-lo no `ChunkerSelector.cs`.
- [x] **T4 — Unit Tests:** Validar fatiamento de tabelas com dezenas de linhas e tabelas malformadas.

## 8. Organization Guardrails

- **Segurança:** O processamento de tabelas deve ser imune a ataques de regex catastrófico (ReDoS) em tabelas gigantes, usando loops determinísticos baseados em `ReadOnlySpan<char>`.

## 9. Definition of Done

- [x] Todos os requisitos (RF-001 a RF-003) implementados.
- [x] Testes unitários com 100% de sucesso em cenários tabulares e layouts ricos.
- [x] `ChunkerSelector` atualizado e documentado.
