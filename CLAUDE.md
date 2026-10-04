# Project Workflow (applies to every session)

At the start of every coding session in this directory, follow all of the following:

1. **context-mode** — use the context-mode tools (`ctx_batch_execute`, `ctx_execute`, `ctx_execute_file`, `ctx_search`) for processing large outputs instead of dumping raw data into the conversation.
2. **ponytail in full mode** — run `/ponytail full`; keep it active for the whole session (laziest solution that works, stdlib/native first, minimal diff).
3. **frontend-design** — invoke the `frontend-design:frontend-design` skill for any UI/frontend work.
4. **superpowers** — consult the superpowers skills (brainstorming before new features, systematic-debugging for bugs, verification-before-completion before claiming done).
5. **ponytail code review** — run `ponytail:ponytail-review` on completed changes before finishing.
