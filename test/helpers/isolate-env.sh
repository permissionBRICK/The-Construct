# shellcheck shell=bash
# Source this before a test reads its environment (test/run-local-checks.sh does it
# for every group; a suite run on its own sources it too):
#
#   . "${ROOT}/test/helpers/isolate-env.sh"
#
# A Construct VM exports its own settings into every shell: CONSTRUCT_* (the host
# service URL, the instance name, the service CA, the external host ...) and T3CODE_*
# (T3 Code's channel, build source, ports ...). The guest scripts let those win over
# the config file a test sandboxes, so a test that inherits them talks to the real host
# service, or runs with this VM's T3 settings instead of its fixture. Drop all of them;
# a test sets the ones it needs after sourcing this.
while IFS= read -r _isolate_env_name; do
  unset "${_isolate_env_name}"
done < <(compgen -e | grep -E '^(CONSTRUCT|T3CODE)_')
unset _isolate_env_name
