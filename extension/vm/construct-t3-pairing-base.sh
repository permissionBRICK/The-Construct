# Sourced into both Windows Desktop and VS Code pairing scripts. cfgget and
# T3CODE_PORT are supplied by the caller; only the chosen origin goes to stdout.
t3base() {
  local direct_host="$1" public scheme=http port="$T3CODE_PORT" link rc=0 authority
  public="$(cfgget T3CODE_PUBLIC_BASE_URL)"
  if [[ -z "$(cfgget CONSTRUCT_SERVICE_URL)" ]]; then
    if [[ -n "$public" ]]; then printf '%s' "$public"; else printf 'http://%s:%s' "$direct_host" "$port"; fi
    return 0
  fi

  # Use the effective TLS state, not the preference: a failed TLS setup leaves
  # the plain T3 listener running. The certificate includes localhost as a SAN.
  if [[ "$public" == https://* ]]; then
    scheme=https
    port="$(cfgget T3CODE_HTTPS_PORT)"; port="${port:-5178}"
  fi
  [[ "$port" =~ ^[0-9]{1,5}$ ]] && (( 10#$port >= 1 && 10#$port <= 65535 )) || {
    echo 'Invalid T3 listener port' >&2; return 1;
  }
  command -v construct >/dev/null 2>&1 || { echo 'Update Construct on the VM to enable client forwarding.' >&2; return 1; }
  link="$(construct expose "$port" --to host --reuse --label 'T3 Code')" || rc=$?
  if (( rc == 7 )); then
    echo 'Host forwarding refused; opening T3 Code on the user PC.' >&2
    link="$(construct expose "$port" --to client --reuse --wait 45 --label 'T3 Code')" || {
      echo 'T3 client forward is not ready. Keep the Construct client connected and retry Link.' >&2; return 1;
    }
    # Pairing runs on the client PC. Its actual allocated loopback port avoids
    # guessing the guest's port and keeps the existing TLS certificate valid.
    if [[ "$link" =~ ^http://(\[[0-9a-fA-F:.]+\]|[A-Za-z0-9._-]+):([0-9]{1,5})/?$ ]]; then
      authority="localhost:${BASH_REMATCH[2]}"
    else
      echo 'The client forward returned an invalid endpoint.' >&2; return 1
    fi
  elif (( rc != 0 )); then
    echo "Could not open the T3 host forward (exit $rc)." >&2; return 1
  else
    if [[ "$link" =~ ^http://(\[[0-9a-fA-F:.]+\]|[A-Za-z0-9._-]+):([0-9]{1,5})/?$ ]]; then
      authority="${BASH_REMATCH[1]}:${BASH_REMATCH[2]}"
    else
      echo 'The host forward returned an invalid endpoint.' >&2; return 1
    fi
  fi
  local selected_port="${authority##*:}"
  (( 10#$selected_port >= 1 && 10#$selected_port <= 65535 )) || { echo 'The forward returned an invalid port.' >&2; return 1; }
  printf '%s://%s' "$scheme" "$authority"
}
