# knowledgehub-sdk (Python)

Client SDK for the [Knowledge MCP Hub](https://github.com/afonsoft/KnowledgeRAG) — wraps the MCP server (Streamable HTTP) with a typed facade and exposes the dynamic tool catalog to LLM frameworks.

## Install

```bash
pip install knowledgehub-sdk
# with LangChain/LangGraph adapter:
pip install "knowledgehub-sdk[langchain]"
```

## Usage

```python
import asyncio
from knowledgehub import KnowledgeHubClient

async def main():
    async with KnowledgeHubClient("http://localhost:5009", api_key="aft_...") as kh:
        # typed facade
        answer = await kh.ask("What is the retry policy?")
        print(answer.answer, answer.citations)

        hits = await kh.search("deployment steps", top_k=5)
        result = await kh.agent_chat("Summarize the onboarding doc and write a note")
        if result.awaiting_approval_id:
            print("paused for human approval:", result.pending_tool)

        # raw catalog + calls (dynamic per key scope)
        tools = await kh.list_tools()
        raw = await kh.call_tool("search_knowledge", {"query": "x", "topK": 3})

        # LangChain / LangGraph — plug the hub's tools into your agent
        lc_tools = await kh.as_langchain_tools()

asyncio.run(main())
```

Sync usage:

```python
from knowledgehub import SyncKnowledgeHubClient

with SyncKnowledgeHubClient("http://localhost:5009", api_key="aft_...") as kh:
    print(kh.ask("...").answer)
```

Streaming (token-by-token + tool events):

```python
async for ev in kh.stream_ask("..."):
    if ev.type == "token":
        print(ev.data.get("text"), end="")
async for ev in kh.stream_agent("..."):
    if ev.type == "tool_start":
        print("→", ev.data)
```

## Auth

Every call sends `Authorization: Bearer aft_*` — create a key in the hub UI (`/api-keys`) with the scopes you need. Write tools require write scope and may pause for HITL approval.
