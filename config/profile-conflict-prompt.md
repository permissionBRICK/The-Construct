A project profile on this VM is in conflict: an edit made here and a change made
elsewhere (another VM or the user's device) touch the same part of the profile
`{{PROFILE}}`, and Construct could not merge them on its own.

Conflict: {{CONFLICT_ID}}
Fields in conflict: {{FIELDS}}
Details: {{CONFLICT_FILE}}

The details file is JSON with `base` (what both sides started from; null when
this VM had no base), `ours` (the edit made on this VM; null means it deleted
the profile), `theirs` (the profile as it is now; null means it was deleted) and
`merged` (every field that is not in conflict, already merged).

Resolve it:

1. Read the details file. Start from `merged` and decide only the fields listed
   above. Keep what both sides meant where you can: for a list, usually both
   sides' additions; for a value, the one that fits the project as it is now.
   Look at the project's repositories under the workspace if that helps.
2. Write the resolved profile to a file and send it:
   `construct project resolve {{CONFLICT_ID}} --file <path>`
   It is checked like any profile write. If the profile changed again
   meanwhile, the command says so and names a new conflict: resolve that one.
3. If you are not sure which side is right, ask the user in this conversation
   before resolving, and say briefly what each side changed. Do not guess on a
   change that removes repositories, setup commands or MCP servers.

Do not edit the files under /opt/construct/projects/.state, and do not delete
the profile to get rid of the conflict.
