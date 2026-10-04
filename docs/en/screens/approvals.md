# Approvals

**Route:** `/approvals`

![Approvals](../../screenshots/approvals.png)

## How it works

`Approvals.razor` is the human-in-the-loop queue for privileged tool calls:

- **Pending list** — tool calls the agent wants to run but that require
  approval (non-read-only catalog tools, or `approval` steps inside flows).
  Each row shows the tool, its arguments and who requested it
  (`RequestedBy` — `agent`, `flow`, …).
- **Approve** — the call executes; if it came from a flow's `approval` step,
  the suspended run resumes automatically
  (`ApprovedResumed` toast).
- **Deny** — with an optional override message explaining why; the agent /
  flow receives the denial and continues without that call.
- **Refresh** — the list is pull-based; nothing is lost if you close the page,
  approvals stay pending until resolved.
