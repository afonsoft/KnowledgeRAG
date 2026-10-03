"""Async client for the Knowledge MCP Hub (Streamable HTTP MCP + REST SSE)."""

from __future__ import annotations

import json
from contextlib import AsyncExitStack
from typing import Any, AsyncIterator, Optional

import httpx2 as httpx
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from mcp.shared.exceptions import MCPError

from .models import (
    AgentResult,
    AskAnswer,
    KnowledgeHubError,
    SearchResult,
    StreamEvent,
    ToolResult,
)


class KnowledgeHubClient:
    """Async client wrapping the hub's MCP server plus its REST SSE streams.

    Usage::

        async with KnowledgeHubClient("http://localhost:5009", api_key="aft_...") as kh:
            answer = await kh.ask("What is the retry policy?")
            tools = await kh.list_tools()   # dynamic catalog for your LLM
    """

    def __init__(
        self,
        base_url: str,
        api_key: str,
        *,
        mcp_path: str = "/mcp",
        timeout: float = 300.0,
        client_name: str = "knowledgehub-python-sdk",
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._api_key = api_key
        self._mcp_url = f"{self._base_url}{mcp_path}"
        self._timeout = timeout
        self._client_name = client_name
        self._stack = AsyncExitStack()
        self._session: Optional[ClientSession] = None
        self._http = httpx.AsyncClient(
            base_url=self._base_url,
            headers={"Authorization": f"Bearer {api_key}"},
            timeout=timeout,
        )

    async def __aenter__(self) -> "KnowledgeHubClient":
        await self.connect()
        return self

    async def __aexit__(self, *exc: Any) -> None:
        await self.aclose()

    async def connect(self) -> "KnowledgeHubClient":
        """Open the MCP transport and complete the initialize handshake."""
        read_stream, write_stream = await self._stack.enter_async_context(
            streamable_http_client(self._mcp_url, http_client=self._http)
        )
        session = await self._stack.enter_async_context(
            ClientSession(read_stream, write_stream)
        )
        await session.initialize()
        self._session = session
        return self

    async def aclose(self) -> None:
        await self._stack.aclose()
        await self._http.aclose()

    @property
    def session(self) -> ClientSession:
        """Raw MCP session — escape hatch for resources, prompts, notifications."""
        if self._session is None:
            raise KnowledgeHubError("not connected — use 'async with' or await connect()")
        return self._session

    # ------------------------------------------------------------------ tools

    async def list_tools(self) -> list[dict[str, Any]]:
        """Live tool catalog (dynamic per key scope / registered sources)."""
        result = await self.session.list_tools()
        return [
            {
                "name": t.name,
                "description": t.description,
                "inputSchema": t.input_schema,
            }
            for t in result.tools
        ]

    async def call_tool(
        self, name: str, arguments: Optional[dict[str, Any]] = None
    ) -> ToolResult:
        """Call any MCP tool by name; returns a normalized ToolResult."""
        try:
            result = await self.session.call_tool(name, arguments or {})
        except MCPError as exc:
            # Remote JSON-RPC error (unknown tool, missing/invalid args).
            raise KnowledgeHubError(f"tool '{name}' failed: {exc.error.message if getattr(exc, "error", None) else str(exc)}") from exc
        text = "\n".join(
            getattr(block, "text", "") for block in result.content if block.type == "text"
        )
        return ToolResult(
            text=text,
            structured=result.structured_content,
            is_error=bool(result.is_error),
        )

    async def search(
        self,
        query: str,
        top_k: int = 10,
        *,
        source_id: Optional[str] = None,
        mode: Optional[str] = None,
    ) -> SearchResult:
        """search_knowledge: hybrid retrieval (FTS5 + vector + graph, RRF)."""
        args: dict[str, Any] = {"query": query, "topK": top_k}
        if source_id:
            args["sourceId"] = source_id
        if mode:
            args["mode"] = mode
        result = await self.call_tool("search_knowledge", args)
        if result.is_error:
            raise KnowledgeHubError(result.text)
        if result.structured:
            return SearchResult.model_validate(result.structured)
        return SearchResult()

    async def ask(
        self,
        question: str,
        top_k: int = 5,
        *,
        source_id: Optional[str] = None,
        mode: Optional[str] = None,
        generate: Optional[bool] = None,
    ) -> AskAnswer:
        """ask_knowledge: retrieval + grounded cited synthesis (abstains on weak evidence)."""
        args: dict[str, Any] = {"question": question, "topK": top_k}
        if source_id:
            args["sourceId"] = source_id
        if mode:
            args["mode"] = mode
        if generate is not None:
            args["generate"] = generate
        result = await self.call_tool("ask_knowledge", args)
        if result.is_error:
            raise KnowledgeHubError(result.text)
        if result.structured:
            return AskAnswer.model_validate(result.structured)
        return AskAnswer(answer=result.text, generated=False)

    async def agent_chat(
        self,
        prompt: str,
        *,
        tools: Optional[list[str]] = None,
        max_iterations: Optional[int] = None,
        allow_write: bool = False,
        thread_id: Optional[str] = None,
        persist: bool = False,
    ) -> AgentResult:
        """agent_chat: the tool-calling agent loop (may pause for HITL approval)."""
        args: dict[str, Any] = {"prompt": prompt}
        if tools is not None:
            args["tools"] = tools
        if max_iterations is not None:
            args["maxIterations"] = max_iterations
        if allow_write:
            args["allowWrite"] = True
        if thread_id:
            args["threadId"] = thread_id
        if persist:
            args["persist"] = True
        result = await self.call_tool("agent_chat", args)
        if result.is_error:
            raise KnowledgeHubError(result.text)
        if result.structured:
            return AgentResult.model_validate(result.structured)
        return AgentResult(answer=result.text)

    async def read_document(self, path: str) -> str:
        """read_document: full text of a document (a citation's path or URI)."""
        result = await self.call_tool("read_document", {"path": path})
        if result.is_error:
            raise KnowledgeHubError(result.text)
        return result.text

    async def write_knowledge(self, title: str, content: str) -> ToolResult:
        """write_knowledge: register a document (write scope + possible HITL approval)."""
        return await self.call_tool(
            "write_knowledge", {"title": title, "content": content}
        )

    async def write_note(
        self, title: str, content: str, path: Optional[str] = None
    ) -> ToolResult:
        """write_note: append a note into the vault."""
        args: dict[str, Any] = {"title": title, "content": content}
        if path:
            args["path"] = path
        return await self.call_tool("write_note", args)

    async def set_chat_settings(
        self,
        endpoint: Optional[str] = None,
        model: Optional[str] = None,
        api_key: Optional[str] = None,
    ) -> ToolResult:
        """set_chat_settings: pin a chat LLM endpoint/model for this key."""
        args: dict[str, Any] = {}
        if endpoint:
            args["endpoint"] = endpoint
        if model:
            args["model"] = model
        if api_key:
            args["apiKey"] = api_key
        return await self.call_tool("set_chat_settings", args)

    async def set_api_key_settings(self, provider: str, api_key: str) -> ToolResult:
        """set_api_key_settings: upstream key (firecrawl|deepwiki|tavily|context7)."""
        return await self.call_tool(
            "set_api_key_settings", {"provider": provider, "apiKey": api_key}
        )

    # --------------------------------------------------------------- streaming

    async def stream_ask(
        self,
        question: str,
        *,
        top_k: Optional[int] = None,
        mode: Optional[str] = None,
    ) -> AsyncIterator[StreamEvent]:
        """POST /api/ask/stream — meta/token/abstain/done/error events."""
        body: dict[str, Any] = {"question": question}
        if top_k is not None:
            body["topK"] = top_k
        if mode:
            body["mode"] = mode
        async for event in self._stream("/api/ask/stream", body):
            yield event

    async def stream_agent(
        self,
        prompt: str,
        *,
        messages: Optional[list[dict[str, str]]] = None,
        max_iterations: Optional[int] = None,
        allow_write: bool = False,
    ) -> AsyncIterator[StreamEvent]:
        """POST /api/agent/stream — meta/token/tool_start/tool_end/done/error."""
        body: dict[str, Any] = {"prompt": prompt}
        if messages is not None:
            body["messages"] = messages
        if max_iterations is not None:
            body["maxIterations"] = max_iterations
        if allow_write:
            body["allowWrite"] = True
        async for event in self._stream("/api/agent/stream", body):
            yield event

    async def _stream(self, path: str, body: dict[str, Any]) -> AsyncIterator[StreamEvent]:
        async with self._http.stream(
            "POST",
            path,
            json=body,
            headers={"Accept": "text/event-stream"},
        ) as response:
            if response.status_code >= 400:
                await response.aread()
                raise KnowledgeHubError(
                    f"{path} returned {response.status_code}: {response.text}"
                )
            event_type = "message"
            async for line in response.aiter_lines():
                if not line:
                    continue
                if line.startswith(":"):
                    continue  # heartbeat
                if line.startswith("event:"):
                    event_type = line[6:].strip()
                elif line.startswith("data:"):
                    raw = line[5:].strip()
                    try:
                        envelope = json.loads(raw)
                    except json.JSONDecodeError:
                        continue
                    data = envelope.get("data", envelope) if isinstance(envelope, dict) else envelope
                    yield StreamEvent(type=event_type, data=data if isinstance(data, dict) else {"value": data})

    # ------------------------------------------------------------ LangChain

    async def as_langchain_tools(self) -> list[Any]:
        """Expose the hub's live tool catalog as LangChain StructuredTools.

        Requires the ``langchain`` extra: ``pip install knowledgehub-sdk[langchain]``.
        Each tool forwards its JSON-schema arguments to ``call_tool`` and returns
        the text portion (what LLMs consume). Must be awaited::

            tools = await kh.as_langchain_tools()
        """
        try:
            from langchain_core.tools import StructuredTool
        except ImportError as exc:  # pragma: no cover
            raise ImportError(
                "Install the langchain extra: pip install knowledgehub-sdk[langchain]"
            ) from exc

        tools = []
        for spec in await self.list_tools():
            name = spec["name"]

            async def _call(_name=name, **kwargs: Any) -> str:
                # StructuredTool passes every optional arg as None — the hub
                # validates types strictly, so strip nulls before the call.
                result = await self.call_tool(
                    _name, {k: v for k, v in kwargs.items() if v is not None}
                )
                if result.is_error:
                    return f"ERROR: {result.text}"
                return result.text

            tools.append(
                StructuredTool.from_function(
                    coroutine=_call,
                    name=name,
                    description=spec.get("description") or "",
                    args_schema=_schema_to_pydantic(name, spec.get("inputSchema") or {}),
                )
            )
        return tools


def _schema_to_pydantic(name: str, schema: dict[str, Any]) -> Any:
    """Build a pydantic model from a JSON Schema dict for StructuredTool args."""
    from pydantic import Field, create_model

    type_map = {
        "string": str,
        "integer": int,
        "number": float,
        "boolean": bool,
        "array": list,
        "object": dict,
    }
    fields: dict[str, Any] = {}
    required = set(schema.get("required") or [])
    for prop, spec in (schema.get("properties") or {}).items():
        typ = spec.get("type")
        if isinstance(typ, list):  # union like ["string","null"] — first non-null
            typ = next((t for t in typ if t != "null"), "string")
        py_type = type_map.get(typ, str)
        default = ... if prop in required else None
        fields[prop] = (Optional[py_type] if prop not in required else py_type, Field(default=default, description=spec.get("description")))
    return create_model(f"{name.title().replace('_', '')}Args", **fields) if fields else None
