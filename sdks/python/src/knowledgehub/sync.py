"""Synchronous facade — runs the async client on a private event loop thread.

Every operation is serialized through a single worker task on the loop, so
anyio cancel scopes in the MCP transport are entered/exited in the same task.

Usage::

    kh = SyncKnowledgeHubClient("http://localhost:5009", api_key="aft_...")
    answer = kh.ask("What is the retry policy?")
    kh.close()
"""

from __future__ import annotations

import asyncio
import concurrent.futures
import threading
from typing import Any, Callable, Coroutine, Optional

from .client import KnowledgeHubClient
from .models import AgentResult, AskAnswer, SearchResult, ToolResult


class SyncKnowledgeHubClient:
    """Blocking wrapper around :class:`KnowledgeHubClient` for non-async apps."""

    def __init__(self, base_url: str, api_key: str, **kwargs: Any) -> None:
        self._loop = asyncio.new_event_loop()
        self._queue: asyncio.Queue = asyncio.Queue()
        self._thread = threading.Thread(target=self._loop.run_forever, daemon=True)
        self._thread.start()
        # single worker task — all coroutines run inside it so cancel scopes
        # always enter/exit in the same task
        self._worker = asyncio.run_coroutine_threadsafe(self._run_worker(), self._loop)
        self._client = KnowledgeHubClient(base_url, api_key, **kwargs)
        self._run(lambda: self._client.connect())

    async def _run_worker(self) -> None:
        while True:
            item = await self._queue.get()
            if item is None:
                return
            factory, fut = item
            try:
                fut.set_result(await factory())
            except BaseException as exc:  # noqa: BLE001 — forward to caller
                fut.set_exception(exc)

    def _run(self, factory: Callable[[], Coroutine[Any, Any, Any]]) -> Any:
        fut: concurrent.futures.Future = concurrent.futures.Future()
        self._loop.call_soon_threadsafe(self._queue.put_nowait, (factory, fut))
        return fut.result()

    def close(self) -> None:
        try:
            self._run(lambda: self._client.aclose())
        finally:
            self._loop.call_soon_threadsafe(self._queue.put_nowait, None)
            self._worker.result(timeout=10)
            self._loop.call_soon_threadsafe(self._loop.stop)
            self._thread.join(timeout=5)

    def __enter__(self) -> "SyncKnowledgeHubClient":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()

    # ------------------------------------------------------------- passthrough

    def list_tools(self) -> list[dict[str, Any]]:
        return self._run(lambda: self._client.list_tools())

    def call_tool(self, name: str, arguments: Optional[dict[str, Any]] = None) -> ToolResult:
        return self._run(lambda: self._client.call_tool(name, arguments))

    def search(self, query: str, top_k: int = 10, **kwargs: Any) -> SearchResult:
        return self._run(lambda: self._client.search(query, top_k, **kwargs))

    def ask(self, question: str, top_k: int = 5, **kwargs: Any) -> AskAnswer:
        return self._run(lambda: self._client.ask(question, top_k, **kwargs))

    def agent_chat(self, prompt: str, **kwargs: Any) -> AgentResult:
        return self._run(lambda: self._client.agent_chat(prompt, **kwargs))

    def read_document(self, path: str) -> str:
        return self._run(lambda: self._client.read_document(path))

    def write_knowledge(self, title: str, content: str) -> ToolResult:
        return self._run(lambda: self._client.write_knowledge(title, content))

    def write_note(self, title: str, content: str, path: Optional[str] = None) -> ToolResult:
        return self._run(lambda: self._client.write_note(title, content, path))

    def set_chat_settings(self, **kwargs: Any) -> ToolResult:
        return self._run(lambda: self._client.set_chat_settings(**kwargs))

    def set_api_key_settings(self, provider: str, api_key: str) -> ToolResult:
        return self._run(lambda: self._client.set_api_key_settings(provider, api_key))
