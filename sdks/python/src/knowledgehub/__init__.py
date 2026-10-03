"""Client SDK for the Knowledge MCP Hub.

Wraps the MCP server (Streamable HTTP) with a typed facade for the core tools
and exposes the dynamic tool catalog for LLM frameworks (LangChain/LangGraph).
"""

from .client import KnowledgeHubClient
from .models import (
    AgentResult,
    AgentStep,
    AskAnswer,
    Citation,
    KnowledgeHubError,
    SearchHit,
    SearchResult,
    StreamEvent,
    ToolResult,
)
from .sync import SyncKnowledgeHubClient

__all__ = [
    "KnowledgeHubClient",
    "SyncKnowledgeHubClient",
    "KnowledgeHubError",
    "ToolResult",
    "SearchResult",
    "SearchHit",
    "AskAnswer",
    "Citation",
    "AgentResult",
    "AgentStep",
    "StreamEvent",
]

__version__ = "0.1.0"
