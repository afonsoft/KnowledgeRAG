using System.Globalization;

namespace KnowledgeHub.Client.Services;

/// <summary>
/// Builds the copy-paste prompt that instructs an AI agent how to connect to this
/// hub's MCP server and which tools/protocol to follow. Shared by Home (post-login)
/// and Login (pre-auth onboarding) so both surfaces always show the same text.
/// The prompt follows the UI culture so the pasted instructions read naturally.
/// </summary>
public static class McpOnboardingPrompt
{
    public static string Build(string baseUrl)
    {
        var mcpUrl = $"{baseUrl}/mcp";
        var sseUrl = $"{baseUrl}/mcp/sse";

        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt")
            return PtTemplate(mcpUrl, sseUrl, baseUrl);
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es")
            return EsTemplate(mcpUrl, sseUrl, baseUrl);
        return EnTemplate(mcpUrl, sseUrl, baseUrl);
    }

    private static string EnTemplate(string mcpUrl, string sseUrl, string baseUrl) => $$"""
        Configure the Knowledge Hub MCP server in your environment to access the organization's knowledge base and RAG tools:

        1. Connection parameters:
        - URL: {{mcpUrl}}
        - Transport: Streamable HTTP (or legacy SSE at {{sseUrl}}?access_token=aft_SUA_CHAVE)
        - Header: Authorization: Bearer aft_SUA_CHAVE
        (Generate a key at /api-keys if you don't have one).

        2. Configuration examples per client:
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
          Add the HTTP MCP server with URL "{{mcpUrl}}" and header "Authorization: Bearer aft_SUA_CHAVE".

        3. Persisting to AGENTS.md / CLAUDE.md:
        - Update your AGENTS.md or CLAUDE.md (your agent/IDE instruction file —
          e.g.: AGENTS.md at the repository root, ~/.claude/CLAUDE.md, ~/.config/opencode/AGENTS.md)
          copying sections 4 and 5 below into it, adapted to that file's format.
        - Goal: the onboarding protocol and tool-usage recommendations are stored in
          your permanent instructions and apply to ALL future sessions, not just this
          one. If the file already exists, append a "Knowledge Hub (MCP)" section
          instead of overwriting it.

        4. Onboarding protocol (required on the first session):
        - First use ask_question to ask about the repository, reading the
          documentation via read_wiki_contents before assuming business rules.
        - When you finish each task, record what was done with write_note.
        - When you need persistent knowledge or memory, use write_knowledge.

        5. Tool usage recommendations:
        - search_knowledge(query, topK): Run semantic and hybrid searches (BM25 + vector) for context before implementing code.
        - ask_knowledge(question, topK): Ask conceptual questions for synthesized, citation-grounded answers.
        - find_dependencies / analyze_impact: Evaluate the entity/dependency graph (GraphRAG) when planning refactors.
        - read_document / write_note / write_knowledge: Access documents and record notes or knowledge in the connected Obsidian vault.
        - set_chat_settings / set_api_key_settings: Configure your chat model or upstream integration keys if you want per-session overrides.

        6. A2A interoperability (Agent-to-Agent, v1.0):
        - Besides MCP, configure this hub's A2A endpoint for agent-to-agent delegation:
          - Agent Card (discovery, anonymous): {{baseUrl}}/.well-known/agent-card.json
          - Endpoint: {{baseUrl}}/a2a — JSON-RPC 2.0 and HTTP+JSON bindings ({{baseUrl}}/a2a/message:send)
          - Auth: the same Bearer aft_SUA_CHAVE; execution reuses your key's scope (allowed tools, sources and rate limit).
        - Exposed skills: ask_knowledge (default), search_knowledge, agent_chat, read_document — select via metadata {"skill": "<name>"} on the message.
        - Sending a task (JSON-RPC):
          POST {{baseUrl}}/a2a
          Authorization: Bearer aft_SUA_CHAVE
          { "jsonrpc": "2.0", "id": 1, "method": "SendMessage",
            "params": { "message": {
              "role": "ROLE_USER",
              "messageId": "<32-hex>",
              "parts": [ { "text": "your question" } ],
              "metadata": { "skill": "search_knowledge" }
            } } }
          → response: { "result": { "task": { "id": "<taskId>", "status": { "state": "TASK_STATE_*" } } } }
        - Query it later (tasks are durable — they survive a hub restart):
          { "jsonrpc": "2.0", "id": 2, "method": "GetTask", "params": { "id": "<taskId>" } }
        - Push webhook (optional): register {"method":"CreateTaskPushNotificationConfig"} with your URL; the hub POSTs the task signed with X-KH-Signature (HMAC-SHA256) when it completes.
        - When to use which: MCP = you call the tools directly; A2A = another agent delegates a task to this hub (task lifecycle submitted → working → completed/failed).
        """;

    private static string EsTemplate(string mcpUrl, string sseUrl, string baseUrl) => $$"""
        Configure el servidor MCP de Knowledge Hub en su entorno para acceder a la base de conocimiento y herramientas RAG de la organización:

        1. Parámetros de conexión:
        - URL: {{mcpUrl}}
        - Transporte: Streamable HTTP (o SSE heredado en {{sseUrl}}?access_token=aft_SUA_CHAVE)
        - Cabecera: Authorization: Bearer aft_SUA_CHAVE
        (Genere una clave en /api-keys si aún no tiene una).

        2. Ejemplos de configuración por cliente:
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

        - Cursor / Devin / Genérico:
          Agregue el servidor MCP HTTP con URL "{{mcpUrl}}" y cabecera "Authorization: Bearer aft_SUA_CHAVE".

        3. Persistencia en AGENTS.md / CLAUDE.md:
        - Actualice su AGENTS.md o CLAUDE.md (el archivo de instrucciones de su agente/IDE —
          ej.: AGENTS.md en la raíz del repositorio, ~/.claude/CLAUDE.md, ~/.config/opencode/AGENTS.md)
          copiando en él las secciones 4 y 5 siguientes, adaptadas al formato de ese archivo.
        - Objetivo: el protocolo de onboarding y las recomendaciones de uso de herramientas quedan
          grabados en sus instrucciones permanentes y pasan a valer en TODAS las sesiones futuras,
          no solo en esta. Si el archivo ya existe, añada una sección "Knowledge Hub (MCP)" en lugar de sobrescribirlo.

        4. Protocolo de onboarding (obligatorio en la primera sesión):
        - Primero use ask_question para preguntar sobre el repositorio, consultando la
          documentación con read_wiki_contents antes de asumir reglas de negocio.
        - Al concluir cada tarea, registre lo hecho con write_note.
        - Cuando necesite crear conocimiento o memoria persistente, use write_knowledge.

        5. Recomendaciones de uso de herramientas:
        - search_knowledge(query, topK): Ejecute búsquedas semánticas e híbridas (BM25 + vectorial) para obtener contexto antes de implementar código.
        - ask_knowledge(question, topK): Haga preguntas conceptuales para obtener respuestas sintetizadas y fundamentadas con citas.
        - find_dependencies / analyze_impact: Evalúe el grafo de entidades y dependencias (GraphRAG) al planificar refactorizaciones.
        - read_document / write_note / write_knowledge: Acceda a documentos y registre notas o conocimiento en el vault Obsidian conectado.
        - set_chat_settings / set_api_key_settings: Configure su modelo de chat o claves de integración upstream si desea overrides para su sesión.

        6. Interoperabilidad A2A (Agent-to-Agent, v1.0):
        - Además del MCP, configure el endpoint A2A de este hub para delegación entre agentes:
          - Agent Card (descubrimiento, anónimo): {{baseUrl}}/.well-known/agent-card.json
          - Endpoint: {{baseUrl}}/a2a — bindings JSON-RPC 2.0 y HTTP+JSON ({{baseUrl}}/a2a/message:send)
          - Auth: el mismo Bearer aft_SUA_CHAVE; la ejecución reutiliza el scope de su clave (tools permitidas, fuentes y rate limit).
        - Skills expuestas: ask_knowledge (default), search_knowledge, agent_chat, read_document — seleccione vía metadata {"skill": "<nombre>"} en el message.
        - Enviar una tarea (JSON-RPC):
          POST {{baseUrl}}/a2a
          Authorization: Bearer aft_SUA_CHAVE
          { "jsonrpc": "2.0", "id": 1, "method": "SendMessage",
            "params": { "message": {
              "role": "ROLE_USER",
              "messageId": "<32-hex>",
              "parts": [ { "text": "su pregunta" } ],
              "metadata": { "skill": "search_knowledge" }
            } } }
          → respuesta: { "result": { "task": { "id": "<taskId>", "status": { "state": "TASK_STATE_*" } } } }
        - Consultar después (las tasks son duraderas — sobreviven a un reinicio del hub):
          { "jsonrpc": "2.0", "id": 2, "method": "GetTask", "params": { "id": "<taskId>" } }
        - Push webhook (opcional): registre {"method":"CreateTaskPushNotificationConfig"} con su URL; el hub POSTea la task firmada con X-KH-Signature (HMAC-SHA256) al concluir.
        - Cuándo usar cada uno: MCP = usted llama las tools directamente; A2A = otro agente delega una tarea a este hub (task lifecycle submitted → working → completed/failed).
        """;

    private static string PtTemplate(string mcpUrl, string sseUrl, string baseUrl) => $$"""
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

        3. Persistência no AGENTS.md / CLAUDE.md:
        - Atualize o seu AGENTS.md ou CLAUDE.md (o arquivo de instruções do seu agente/IDE —
          ex.: AGENTS.md na raiz do repositório, ~/.claude/CLAUDE.md, ~/.config/opencode/AGENTS.md)
          copiando para ele as seções 4 e 5 abaixo, adaptadas ao formato desse arquivo.
        - Objetivo: o protocolo de onboarding e as recomendações de uso das ferramentas ficam
          gravados nas suas instruções permanentes e passam a valer em TODAS as sessões futuras,
          não apenas nesta. Se o arquivo já existir, acrescente uma seção "Knowledge Hub (MCP)" em vez de sobrescrevê-lo.

        4. Protocolo de Onboarding (obrigatório na primeira sessão):
        - Primeiro use ask_question para perguntar sobre o repositório, consultando a
          documentação com read_wiki_contents antes de assumir regras de negócio.
        - Ao concluir cada tarefa, registre o que foi feito com write_note.
        - Quando precisar criar conhecimento ou memória persistente, use write_knowledge.

        5. Recomendações de Uso das Ferramentas:
        - search_knowledge(query, topK): Execute buscas semânticas e híbridas (BM25 + vetorial) para obter contexto antes de implementar código.
        - ask_knowledge(question, topK): Faça perguntas conceituais para obter respostas sintetizadas e fundamentadas com citações.
        - find_dependencies / analyze_impact: Avalie o grafo de entidades e dependências (GraphRAG) ao planejar refatorações.
        - read_document / write_note / write_knowledge: Acesse documentos e registre notas ou conhecimento no cofre Obsidian conectado.
        - set_chat_settings / set_api_key_settings: Configure seu modelo de chat ou chaves de integração upstream se desejar overrides para sua sessão.

        6. Interoperabilidade A2A (Agent-to-Agent, v1.0):
        - Além do MCP, configure o endpoint A2A deste hub para delegação entre agentes:
          - Agent Card (descoberta, anônimo): {{baseUrl}}/.well-known/agent-card.json
          - Endpoint: {{baseUrl}}/a2a — bindings JSON-RPC 2.0 e HTTP+JSON ({{baseUrl}}/a2a/message:send)
          - Auth: o mesmo Bearer aft_SUA_CHAVE; a execução reutiliza o escopo da sua chave (tools permitidas, fontes e rate limit).
        - Skills expostas: ask_knowledge (default), search_knowledge, agent_chat, read_document — selecione via metadata {"skill": "<nome>"} na message.
        - Enviar uma tarefa (JSON-RPC):
          POST {{baseUrl}}/a2a
          Authorization: Bearer aft_SUA_CHAVE
          { "jsonrpc": "2.0", "id": 1, "method": "SendMessage",
            "params": { "message": {
              "role": "ROLE_USER",
              "messageId": "<32-hex>",
              "parts": [ { "text": "sua pergunta" } ],
              "metadata": { "skill": "search_knowledge" }
            } } }
          → resposta: { "result": { "task": { "id": "<taskId>", "status": { "state": "TASK_STATE_*" } } } }
        - Consultar depois (as tasks são duráveis — sobrevivem a restart do hub):
          { "jsonrpc": "2.0", "id": 2, "method": "GetTask", "params": { "id": "<taskId>" } }
        - Push webhook (opcional): cadastre {"method":"CreateTaskPushNotificationConfig"} com a sua URL; o hub POSTa a task assinada com X-KH-Signature (HMAC-SHA256) ao concluir.
        - Quando usar: MCP = você chama as tools diretamente; A2A = outro agente delega uma tarefa a este hub (task lifecycle submitted → working → completed/failed).
        """;
}
