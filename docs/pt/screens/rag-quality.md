# Qualidade RAG

**Rota:** `/rag-quality`

![Qualidade RAG](../../screenshots/rag-quality.png)

## Como funciona

`RagQualityDashboard.razor` mostra as avaliações da tríade RAG pós-resposta
(`RagEvaluations`):

- **Cards** — avaliações dos últimos 7 dias e a taxa de alucinação entre elas.
- **Respostas flagadas** — respostas que a tríade (relevância de contexto,
  groundedness, relevância da resposta) marcou, com o timestamp do flag;
  clicar mostra o motivo.
- **Retenção** — as linhas vivem 90 dias; o dashboard lê
  `GET /api/v1/evaluation/stats`, então reflete exatamente o que a API tem.
