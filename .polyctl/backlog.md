# bizigo-loganalyzer — pre-authorised work queue

This file lists, in order, the work that may start **without waiting for the architect's
decision**. Each item's acceptance boundary was written in advance.

**Rule:** If the open slice is stuck on an architectural decision, DO NOT STOP. Park the slice
with `polyctl slice block "…"` (it moves to `blocked`, the reason its `needs:`), leave a
`DECISION REQUIRED` message in `agent-mail/` — or ask a question with options with `polyctl_ask` —
and move to the first **open** item here.

---

## 1. <title of the first slice>
<what is to be done, in one paragraph>
**Acceptance boundary:** <measurable: which tests pass, what does not change, what is forbidden>

---

## Outside the queue — never on your own
- Marking a task or epic box complete
- `git push`, opening a PR, force-pushing, skipping hooks
- Moving on to new work from outside the queue
- Changing an architectural contract (write `DECISION REQUIRED` and move to the next item)
