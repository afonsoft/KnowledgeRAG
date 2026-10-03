"""Unit tests for wire-shape parsing (no server needed)."""

from knowledgehub.models import AgentResult, AskAnswer, SearchResult


def test_search_result_parses_structured_content():
    payload = {
        "results": [
            {
                "chunkText": "some chunk",
                "documentTitle": "Doc",
                "sourceName": "vault",
                "sourceId": "11111111-1111-1111-1111-111111111111",
                "score": 0.9,
                "uriReference": "obsidian://doc",
                "sectionPath": "A/B",
                "components": ["Entity"],
                "isRelaxed": False,
            }
        ],
        "grade": "sufficient",
        "retried": False,
        "totalMatches": 1,
        "truncatedByTokens": False,
        "filterRelaxed": False,
        "warnings": None,
    }
    result = SearchResult.model_validate(payload)
    assert result.results[0].chunk_text == "some chunk"
    assert result.results[0].components == ["Entity"]
    assert result.grade == "sufficient"
    assert result.total_matches == 1


def test_ask_answer_parses_citations():
    payload = {
        "answer": "The answer [1]",
        "citations": [
            {
                "index": 1,
                "source": "vault",
                "title": "Doc",
                "uri": "obsidian://doc",
                "path": "notes/doc.md",
                "score": 0.8,
            }
        ],
        "model": "gpt-x",
        "generated": True,
        "insufficientEvidence": False,
        "retrievalGrade": "sufficient",
        "retried": False,
        "cached": False,
        "truncatedByTokens": False,
    }
    answer = AskAnswer.model_validate(payload)
    assert answer.answer == "The answer [1]"
    assert answer.citations[0].path == "notes/doc.md"
    assert answer.generated is True


def test_agent_result_parses_approval_pause():
    payload = {
        "answer": "",
        "steps": [
            {
                "iteration": 1,
                "tool": "search_knowledge",
                "argsSummary": "q",
                "isError": False,
                "elapsedMs": 10,
            }
        ],
        "toolCalls": ["search_knowledge"],
        "iterations": 1,
        "latencyMs": 50,
        "limitReached": False,
        "awaitingApprovalId": "22222222-2222-2222-2222-222222222222",
        "pendingTool": "write_note",
        "threadId": None,
    }
    result = AgentResult.model_validate(payload)
    assert result.awaiting_approval_id == "22222222-2222-2222-2222-222222222222"
    assert result.pending_tool == "write_note"
    assert result.steps[0].tool == "search_knowledge"


def test_schema_to_pydantic_handles_union_types():
    """JSON Schema unions like ["string","null"] map to the non-null type."""
    from knowledgehub.client import _schema_to_pydantic

    model = _schema_to_pydantic(
        "tool_x",
        {
            "type": "object",
            "properties": {
                "query": {"type": "string"},
                "topK": {"type": "integer"},
                "mode": {"type": ["string", "null"]},
            },
            "required": ["query"],
        },
    )
    fields = model.model_fields
    assert fields["query"].is_required()
    assert not fields["topK"].is_required()
    assert not fields["mode"].is_required()


def test_stream_sse_line_parsing(tmp_path):
    """The _stream parser unwraps {"seq","data"} envelopes."""
    import asyncio
    import json

    import httpx2 as httpx
    from knowledgehub.client import KnowledgeHubClient

    body = (
        "event: meta\n"
        'data: {"seq":1,"data":{"grade":"weak"}}\n'
        "\n"
        ": keep-alive\n"
        "\n"
        "event: done\n"
        'data: {"seq":2,"data":{"answer":"ok"}}\n'
        "\n"
    )

    async def run():
        client = KnowledgeHubClient.__new__(KnowledgeHubClient)
        # bypass __init__; only _stream's http is needed
        transport = httpx.MockTransport(
            lambda request: httpx.Response(
                200,
                headers={"content-type": "text/event-stream"},
                content=body.encode(),
            )
        )
        client._http = httpx.AsyncClient(
            base_url="http://test", transport=transport
        )
        return [e async for e in client._stream("/api/ask/stream", {})]

    events = asyncio.run(run())
    assert [e.type for e in events] == ["meta", "done"]
    assert events[0].data["grade"] == "weak"
    assert events[1].data["answer"] == "ok"
