# Repository workflow

- Read `DEVELOPMENT-STATUS.md` first, then only the files needed for the bounded task. Treat status as the single current checkpoint; replace obsolete status text instead of appending session-by-session logs.
- Keep each milestone's scope frozen. Put unrelated ideas and nonblocking discoveries in a short deferred list or backlog; do not expand the active task to absorb them.
- For each change, state a hypothesis, run the smallest useful check, and define a stopping condition before broadening the investigation.
- Match verification to impact. Seek one successful acceptance run for each material batch; repeat only when a failure or new concern justifies it. Record the latest result in the canonical status.
- Keep build/test/live logs out of conversational context except for concise summaries and relevant failures. Store private logs outside the repository when needed; avoid repeatedly dumping whole docs, catalogs, or manifests.
- Preserve privacy: keep company paths, exported records, business record identifiers, raw XML, and credentials out of Git and routine output. Use aggregate counts and sanitized errors in notes.
- Live QuickBooks COM work requires explicit task scope. Do not start unsolicited overnight work, overlap COM sessions, or assume a cancellation can safely interrupt an in-flight SDK call.
- Do not claim unavailable datasets are empty or that the export is a point-in-time company backup. Keep future loader work separate from extraction.
