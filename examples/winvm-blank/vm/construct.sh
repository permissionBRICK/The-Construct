# Construct backend helpers, sourced by config.sh.
# Keep network discovery lazy: configuration/help work before the VM exists.
vm_inventory() { construct vm list --json; }
vm_exists() {
    local inventory
    inventory=$(vm_inventory) || return "$?"
    jq -e --arg name "$VM_NAME" 'any(.[]; .name == $name)' <<<"$inventory" >/dev/null
}
vm_running() {
    local inventory
    inventory=$(vm_inventory) || return "$?"
    jq -e --arg name "$VM_NAME" 'any(.[]; .name == $name and (.state | ascii_downcase) == "running")' <<<"$inventory" >/dev/null
}
guest_address() {
    if [ -n "${GUEST_HOST:-}" ]; then printf '%s\n' "$GUEST_HOST"; return; fi
    local addresses
    addresses=$(construct vm addresses "$VM_NAME" --json) || return "$?"
    jq -er '[.addresses[].address | select(test("^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$") and (startswith("169.254.") | not))][0] // empty' <<<"$addresses"
}
guest_ssh() {
    local address
    address=$(guest_address) || return "$?"
    SSHPASS="$GUEST_PASS" sshpass -e ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
        -o ConnectTimeout=10 -o LogLevel=ERROR -p 22 "$GUEST_USER@$address" "$@"
}
guest_scp() {
    local address arg args=()
    address=$(guest_address) || return "$?"
    # Existing operation scripts use this logical endpoint. Rewrite only the
    # remote operand, never local file paths or arbitrary argument contents.
    for arg in "$@"; do
        if [[ "$arg" == "$GUEST_USER@127.0.0.1:"* ]]; then
            arg="$GUEST_USER@$address:${arg#*:}"
        fi
        args+=("$arg")
    done
    SSHPASS="$GUEST_PASS" sshpass -e scp -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
        -o ConnectTimeout=10 -o LogLevel=ERROR -P 22 "${args[@]}"
}
