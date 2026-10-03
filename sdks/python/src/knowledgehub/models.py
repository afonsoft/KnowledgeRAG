"""Typed models mirroring the hub's tool structuredContent payloads."""

from __future__ import annotations

from typing import Any, Optional

from pydantic import BaseModel, Field


class ToolResult(BaseModel):
    """Normalized result of an MCP tools/call."""

    text: str = ""
    structured: Optional[dict[str, Any]] = None
    is_error: bool = False


class Citation(BaseModel):
    index: int = 0
    source: str = ""
    title: str = ""
    uri: str = ""
    path: Optional[str] = None
    score: float = 0.0
    suspicion_flags: Optional[str] = Field(default=None, alias="suspicionFlags")
    components: Optional[list[str]] = None

    model_config = {"populate_by_name": True}


class SearchHit(BaseModel):
    chunk_text: str = Field(default="", alias="chunkText")
    document_title: str = Field(default="", alias="documentTitle")
    source_name: str = Field(default="", alias="sourceName")
    source_id: Optional[str] = Field(default=None, alias="sourceId")
    score: float = 0.0
    uri_reference: str = Field(default="", alias="uriReference")
    suspicion_flags: Optional[str] = Field(default=None, alias="suspicionFlags")
    section_path: Optional[str] = Field(default=None, alias="sectionPath")
    context: Optional[str] = None
    components: Optional[list[str]] = None
    is_relaxed: bool = Field(default=False, alias="isRelaxed")

    model_config = {"populate_by_name": True}


class SearchResult(BaseModel):
    results: list[SearchHit] = []
    grade: Optional[str] = None
    retried: bool = False
    total_matches: int = Field(default=0, alias="totalMatches")
    filter_relaxed: bool = Field(default=False, alias="filterRelaxed")
    truncated_by_tokens: bool = Field(default=False, alias="truncatedByTokens")
    warnings: Optional[list[str]] = None

    model_config = {"populate_by_name": True}


class AskAnswer(BaseModel):
    answer: Optional[str] = None
    citations: list[Citation] = []
    model: Optional[str] = None
    generated: bool = False
    insufficient_evidence: bool = Field(default=False, alias="insufficientEvidence")
    retrieval_grade: Optional[str] = Field(default=None, alias="retrievalGrade")
    retried: bool = False
    cached: bool = False
    truncated_by_tokens: bool = Field(default=False, alias="truncatedByTokens")

    model_config = {"populate_by_name": True}


class AgentStep(BaseModel):
    iteration: int = 0
    tool: str = ""
    args_summary: str = Field(default="", alias="argsSummary")
    is_error: bool = Field(default=False, alias="isError")
    elapsed_ms: float = Field(default=0.0, alias="elapsedMs")

    model_config = {"populate_by_name": True}


class AgentResult(BaseModel):
    answer: str = ""
    steps: list[AgentStep] = []
    tool_calls: list[str] = Field(default=[], alias="toolCalls")
    iterations: int = 0
    latency_ms: float = Field(default=0.0, alias="latencyMs")
    limit_reached: bool = Field(default=False, alias="limitReached")
    awaiting_approval_id: Optional[str] = Field(default=None, alias="awaitingApprovalId")
    pending_tool: Optional[str] = Field(default=None, alias="pendingTool")
    thread_id: Optional[str] = Field(default=None, alias="threadId")

    model_config = {"populate_by_name": True}


class StreamEvent(BaseModel):
    """One SSE frame from /api/ask/stream or /api/agent/stream."""

    type: str
    data: dict[str, Any] = {}


class KnowledgeHubError(Exception):
    """Raised when a tool call returns isError=true."""
