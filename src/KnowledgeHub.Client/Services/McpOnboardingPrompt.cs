namespace KnowledgeHub.Client.Services;

/// <summary>
/// Builds the copy-paste prompt that instructs an AI agent how to connect to this
/// hub's MCP server and which tools/protocol to follow. Shared by Home (post-login)
/// and Login (pre-auth onboarding) so both surfaces always show the same text.
/// </summary>
public static class McpOnboardingPrompt
{
    public static string Build(string mcpUrl, string sseUrl) => $$"""
        Configure o servidor MCP do Knowledge Hub no seu ambiente para acessar a base de conhecimento e ferramentas RAG da organização:

        1. Parâmetros de Conexão:
        - URL: {{mcpUrl}}
        - Transporte: Streamable HTTP (ou SSE legado em {{sseUrl}}?access_token=aft_SUA_CHAVE)
        - Cabeçalho: Authorization: Bearer aft_SUA_CHAVE
        (Gere uma chave em /api-keys se ainda não tiver).

        2. Exemplos de Configuração por Cliente:
        - Claude Code:
          claude mcp add --transport http knowledge {{mcpUrl}} --header "Authorization: Bearer aft_SUA_CHAVE"

        - OpenCode (opencode.json):
          {
            "mcp": {
              "knowledge": {
                "type": "remote",
                "url": "{{mcpUrl}}",
                "headers": {
                  "Authorization": "Bearer aft_SUA_CHAVE"
                }
              }
            }
          }

        - Cursor / Devin / Generic:
          Adicione o servidor MCP HTTP com URL "{{mcpUrl}}" e cabeçalho "Authorization: Bearer aft_SUA_CHAVE".

        3. Protocolo de Onboarding (obrigatório na primeira sessão):
        - Primeiro use ask_question para perguntar sobre o repositório, consultando a
          documentação com read_wiki_contents antes de assumir regras de negócio.
        - Ao concluir cada tarefa, registre o que foi feito com write_note.
        - Quando precisar criar conhecimento ou memória persistente, use write_knowledge.

        4. Recomendações de Uso das Ferramentas:
        - search_knowledge(query, topK): Execute buscas semânticas e híbridas (BM25 + vetorial) para obter contexto antes de implementar código.
        - ask_knowledge(question, topK): Faça perguntas conceituais para obter respostas sintetizadas e fundamentadas com citações.
        - find_dependencies / analyze_impact: Avalie o grafo de entidades e dependências (GraphRAG) ao planejar refatorações.
        - read_document / write_note / write_knowledge: Acesse documentos e registre notas ou conhecimento no cofre Obsidian conectado.
        - set_chat_settings / set_api_key_settings: Configure seu modelo de chat ou chaves de integração upstream se desejar overrides para sua sessão.
        """;
}
