# Flows

**Route:** `/flows`

![Flows — canvas workspace](../../screenshots/flows.png)

## How it works

`Flows.razor` manages `AgentFlow` definitions — sequential step pipelines that
become MCP tools (`flow_<slug>`) and A2A skills.

### Editor (canvas-first)

Opening New/Edit lands on the **Canvas** workspace — a large dot-grid canvas
(START → steps → END) plus a side panel, in the n8n/Node-RED pattern:

- **Palette** (panel, nothing selected) — all 10 step types; **drag** a type
  onto a connector to insert at that spot, or **click** to append at the end.
- **Inspector** (panel, node selected) — the step's id, type, label and
  Config JSON (Nested steps JSON for `foreach`/`condition`), with
  Move up/down, **Duplicate** and Remove.
- **Canvas** — `#n` position badge and type icon per node, `+` on each
  connector inserts a step there, drag a node onto another connector to
  reorder, warning triangle when a step's JSON is invalid, step counter and
  zoom (50–150%) in the toolbar, and a dashed ghost node on empty flows.
- The **Lista** and **JSON** views remain available and stay in sync; the
  inputs card declares flow inputs and there's Validate + Save.

![Flows — inspector](../../screenshots/flows-inspector.png)

### Runs & triggers

- **Run card** — execute a saved flow with inputs; a failed run returns 422,
  a suspended `approval` step returns 202 and resumes from
  [/approvals](approvals.md).
- **Runs** — run history with status and per-step outputs.
- **Triggers** — schedule (interval seconds) or webhook (`fwt_*` secret
  tokens); enable/disable per trigger, last-fired timestamp.
