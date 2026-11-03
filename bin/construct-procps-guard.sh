#!/bin/sh
# Installed by The Construct as /usr/local/bin/pkill, pgrep and pidwait
# (bin/provision.sh, setup_procps_guard).
#
# Coding agents run every tool command as `bash -c '<the whole command>'`, so
# `pkill -f <pattern>` also matches the shell that runs it. pkill then kills that
# shell, the rest of the command never runs, and the agent sees only "exit code
# 144". --ignore-ancestors (procps-ng >= 4.0.3) leaves the caller's own process
# chain alone, which also covers the agent CLI and the T3 server above it.
name="${0##*/}"
for real in "/usr/bin/${name}" "/bin/${name}"; do
  [ -x "${real}" ] && exec "${real}" --ignore-ancestors "$@"
done
printf '%s: %s not found in /usr/bin or /bin\n' "${name}" "${name}" >&2
exit 127
