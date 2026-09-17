# Company-pc client forwarding recovery, 2026-09-08

Christoph reported that the company workstation connects directly to its local
Hyper-V VM. Agent-provided localhost links initially sometimes work, then stop;
the matching agent-vm.mshome.net links keep working. Closing/reopening VS Code
may not restore the forward, while deleting and recreating the request does.

These are client forwards: the Construct extension owns their SSH listeners on
the workstation. For this local setup, `construct expose PORT --to host` returns
the direct VM address and needs no VS Code process. A server-managed host forward
on haus-pc is a different implementation, owned by constructd.

Two current recovery defects were reproduced in the forwarder tests: saved error
acks were treated as final and never reopened by a new window; an SSH restart
that failed before settling (or whose spawn threw) did not schedule another retry.
The fix retains durable request intent, retries both failure paths with capped
backoff, retains the selected local port in error acknowledgements, and retries
exhausted port selection at most once per minute. Explicit closes and disposal
still cancel retries. All 25 extension suites passed, including 727 forwarder checks.

Company-pc's relay did not respond during this session. The exact initial SSH
failure and the installed company client version remain unverified; no deployment
was made there. Next field verification should capture the failing tunnel's SSH
exit reason and exercise disconnect/reconnect using the same request ID, plus
confirm whether direct VM links should become that machine's default. The
company fork has its own upstream-sync workflow; inspect the installed source
before choosing its update path.
