# SPEC-20260927-rag-evaluation-triad-metrics

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `rag-evaluation-triad-metrics` |
| Type | `Feature` |
| Stack | `.NET 10 / Microsoft.Extensions.AI / OpenTelemetry / C# 12 / SQLite / Blazor WASM` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `[A DEFINIR]` |
| Status | `Draft` |

## 1. User Story

**As a** administrador ou engenheiro de IA do Knowledge MCP Hub
**I want** que o sistema avalie e monitore continuamente a qualidade das respostas geradas pelo RAG usando as métricas da Tríade Dourada (Context Relevance, Groundedness/Faithfulness e Answer Relevance)
**So that** eu possa detectar alucinações de modelos, identificar falhas de recuperação em tempo real e visualizar métricas objetivas de qualidade no painel administrativo do Blazor.

**Problem context:**
Em sistemas RAG em produção, é comum o usuário receber respostas convincentes, mas factualmente incorretas (alucinações) ou incompletas por falha na recuperação dos chunks adequados. Sem métricas automatizadas objetivas (LLM-as-a-judge ou heurísticas baseadas em embeddings), os mantenedores só descobrem falhas de qualidade quando usuários finais reclamam.
Inspirado nos conceitos consolidados em frameworks como **LlamaIndex**, **Ragas**, **TruLens** e na esteira de avaliação do **Weaviate Verba**, esta SPEC implementa o motor **RAG Quality Triad Evaluator** (`RagTriadEvaluatorService`) no Knowledge MCP Hub. O avaliador opera de forma assíncrona ou por amostragem sobre as consultas sintetizadas, pontuando cada interação em 3 dimensões (0.0 a 1.0):
1. **Context Relevance (Relevância do Contexto):** Avalia se os chunks recuperados pelo retriever são de fato pertinentes à pergunta feita, medindo a precisão do retrieval.
2. **Groundedness / Faithfulness (Fidelidade):** Avalia se cada afirmação na resposta gerada é sustentada pelas evidências dos chunks fornecidos, detectando alucinações.
3. **Answer Relevance (Relevância da Resposta):** Avalia se a resposta sintetizada atende diretamente ao que foi solicitado pelo usuário.

## 2. Scope

**In scope:**
- **Serviço de Avaliação `RagTriadEvaluatorService` (`IRagTriadEvaluator`):**
  - Execução assíncrona pós-resposta (em background sem bloquear a resposta do usuário) ou via amostragem configurável (`SampleRate = 0.2` = 20% das consultas, ou 100% no Playground).
  - Três cálculos de métrica padronizados (escala 0.00 a 1.00):
    - `ContextRelevanceScore`: razão entre sentenças dos chunks relevantes para a query e total de sentenças.
    - `GroundednessScore`: proporção de afirmações na resposta verificáveis nos chunks.
    - `AnswerRelevanceScore`: similaridade semântica entre pergunta gerada a partir da resposta e a pergunta original.
- **Armazenamento e Histórico de Qualidade:**
  - Nova entidade `RagEvaluationEntity` no EF Core: `QueryId`, `Question`, `ContextRelevance`, `Groundedness`, `AnswerRelevance`, `OverallScore`, `TimestampUtc`, `FlaggedAsHallucination`.
  - Sinalização automática de alucinação quando `Groundedness < 0.60`.
- **Métricas OpenTelemetry e Logs Estruturados:**
  - Métricas: `rag.eval.context_relevance`, `rag.eval.groundedness`, `rag.eval.answer_relevance`.
  - Alertas emitidos via Serilog quando `Groundedness` violar o piso mínimo.
- **Painel de Qualidade RAG na UI (Blazor WASM):**
  - Nova aba "Qualidade RAG" em `/monitor` ou `/settings` com gráficos de tendência temporal da tríade, taxa de alucinação e lista de respostas com baixa pontuação para inspeção.
- **Configuração no `appsettings.json`:**
  - `RagEvaluation:Enabled`: bool (default `true`).
  - `RagEvaluation:SampleRate`: double (default `0.20`).
  - `RagEvaluation:JudgeModel`: string (opcional — usa o chat client padrão ou modelo leve local).

**Out of scope:**
- Avaliação síncrona bloqueante que atrase o streaming de respostas ao usuário final (a avaliação roda em background via `Channel<RagEvaluationTask>`).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Evaluation/`:
  - `IRagTriadEvaluator.cs`: interface de avaliação.
  - `RagTriadEvaluatorService.cs`: orquestrador de avaliação da tríade.
  - `EvaluationWorker.cs`: background service que consome avaliações da fila.
  - `RagEvaluationMetrics.cs`: contadores e histogramas OpenTelemetry.
- `src/KnowledgeHub.Server/Data/Entities/RagEvaluationEntity.cs`: entidade de persistência.
- `src/KnowledgeHub.Server/Services/LlmAnswerSynthesisService.cs`: enfileiramento pós-síntese.
- `src/KnowledgeHub.Client/Pages/RagQualityDashboard.razor`: dashboard Blazor WASM.
- `tests/KnowledgeHub.Tests.Unit/Evaluation/`: testes unitários para cálculo das métricas.

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Evaluation/IRagTriadEvaluator.cs              (create)
src/KnowledgeHub.Server/Evaluation/RagTriadEvaluatorService.cs      (create)
src/KnowledgeHub.Server/Evaluation/EvaluationWorker.cs               (create)
src/KnowledgeHub.Server/Evaluation/RagEvaluationMetrics.cs          (create)
src/KnowledgeHub.Server/Data/Entities/RagEvaluationEntity.cs        (create)
src/KnowledgeHub.Server/Services/LlmAnswerSynthesisService.cs        (modify)
src/KnowledgeHub.Server/Data/KnowledgeHubDbContext.cs                (modify)
src/KnowledgeHub.Client/Pages/RagQualityDashboard.razor              (create)
tests/KnowledgeHub.Tests.Unit/Evaluation/RagTriadEvaluatorTests.cs   (create)
```

## 4. Requirements

### RF-001: Enfileiramento Assíncrono de Avaliações
- **Description:** Toda resposta gerada pelo `LlmAnswerSynthesisService` deve ter sua tripla (Pergunta, Chunks, Resposta) enviada para a fila de avaliação respeitando o `SampleRate`.
- **Rules:**
  - Se `SampleRate = 1.0`, todas as respostas são avaliadas.
  - O enfileiramento não adiciona mais de 1ms à latência de entrega da resposta ao usuário.
- **Input → Output:** Resposta sintetizada → Tarefa enfileirada no `System.Threading.Channels.Channel`.

### RF-002: Cálculo da Tríade de Métricas
- **Description:** O avaliador deve computar as pontuações de `ContextRelevance`, `Groundedness` e `AnswerRelevance`.
- **Rules:**
  - Cada métrica resulta em valor entre `0.00` e `1.00`.
  - `OverallScore` é a média harmônica ou ponderada das três métricas:
    $$\text{OverallScore} = 3 \times \frac{C \times G \times A}{(C \times G) + (G \times A) + (C \times A)}$$
  - Se `Groundedness < 0.60`, o campo `FlaggedAsHallucination` é gravado como `true`.
- **Input → Output:** Contexto + Pergunta + Resposta → `RagEvaluationResult` com scores normalizados.

### RF-003: Alertas e Telemetria de Qualidade
- **Description:** O sistema deve alimentar o OpenTelemetry e emitir alertas estruturados quando a qualidade cair.
- **Rules:**
  - Publica medições nos gauges `rag_context_relevance`, `rag_groundedness` e `rag_answer_relevance`.
  - Log de nível `Warning` emitido caso `FlaggedAsHallucination == true`.

## 5. API Contract (if applicable)

Endpoint REST `GET /api/v1/evaluation/stats`:
```json
{
  "period": "Last7Days",
  "totalEvaluations": 1420,
  "averageContextRelevance": 0.88,
  "averageGroundedness": 0.94,
  "averageAnswerRelevance": 0.91,
  "hallucinationRatePercent": 2.3,
  "recentFlaggedQueries": [
    {
      "id": "eval_8871",
      "question": "Qual a senha padrão do admin?",
      "groundednessScore": 0.32,
      "flaggedAt": "2026-09-27T12:00:00Z"
    }
  ]
}
```

## 6. Acceptance Criteria

- [ ] **Given** uma resposta onde todos os fatos constam explicitamente nos chunks recuperados **when** o `RagTriadEvaluatorService` processa **then** `GroundednessScore` é calculado $\ge 0.90$ e `FlaggedAsHallucination = false`.
- [ ] **Given** uma resposta sintetizada que inventa números e datas não presentes nos chunks **when** avaliada **then** `GroundednessScore` é $< 0.60$ e o registro é marcado com `FlaggedAsHallucination = true`.
- [ ] **Given** `SampleRate = 0.0` **when** respostas são geradas **then** nenhuma tarefa de avaliação é enfileirada, poupando chamadas de LLM.

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** Analisar `LlmAnswerSynthesisService.cs` e métricas OpenTelemetry existentes.
- [ ] **T2 — Data Entity:** Criar `RagEvaluationEntity.cs` e atualizar `KnowledgeHubDbContext.cs`.
- [ ] **T3 — Evaluator Service:** Implementar `RagTriadEvaluatorService.cs` com prompts estruturados para o judge.
- [ ] **T4 — Worker & Queue:** Implementar `EvaluationWorker.cs` em background.
- [ ] **T5 — Dashboard UI:** Implementar `RagQualityDashboard.razor` no Blazor WASM.
- [ ] **T6 — Tests:** Escrever testes unitários para verificação de scores da tríade e detecção de alucinações.

## 8. Organization Guardrails

- **Economia de Recursos:** A avaliação deve utilizar preferencialmente modelos menores e mais rápidos (ex.: `gpt-4o-mini` ou modelo local via Ollama) para minimizar custos de inferência.

## 9. Definition of Done

- [ ] Todos os requisitos (RF-001 a RF-003) implementados.
- [ ] Testes unitários com casos de alucinação comprovando a sensibilidade do avaliador.
- [ ] Dashboard Blazor WASM funcional e integrado ao menu de monitoramento.
