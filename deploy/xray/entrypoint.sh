#!/bin/sh
set -eu

readonly SECRET_PATH=/run/secrets/xray-outbound.json
readonly BASE_CONFIG=/etc/xray/base.json
readonly CHAIN=WEB10_XRAY

fail() {
    printf '%s\n' "xray gateway startup failed: $1" >&2
    exit 1
}

[ -r "$SECRET_PATH" ] || fail "outbound secret is not readable at $SECRET_PATH"

if ! jq empty "$SECRET_PATH" >/dev/null 2>&1; then
    fail "outbound secret is not valid JSON"
fi

if ! jq -e '
    type == "object"
    and keys == ["outbounds"]
    and (.outbounds | type == "array" and length > 0)
    and ([.outbounds[] | select(type == "object" and .tag == "proxy")] | length == 1)
    and all(
        .outbounds[];
        type == "object"
        and (.protocol | type == "string")
        and ((.protocol | ascii_downcase) != "direct")
        and ((.protocol | ascii_downcase) != "freedom")
    )
' "$SECRET_PATH" >/dev/null 2>&1; then
    fail "outbound secret must contain only a non-empty outbounds array, exactly one proxy tag, and no direct/freedom protocol"
fi

if ! /usr/local/bin/xray run -c "$BASE_CONFIG" -c "$SECRET_PATH" -test; then
    fail "merged Xray configuration is invalid"
fi

remove_ipv4_chain() {
    while iptables -t nat -C OUTPUT -j "$CHAIN" >/dev/null 2>&1; do
        iptables -t nat -D OUTPUT -j "$CHAIN"
    done
    iptables -t nat -F "$CHAIN" >/dev/null 2>&1 || true
    iptables -t nat -X "$CHAIN" >/dev/null 2>&1 || true
}

setup_ipv4_redirect() {
    iptables -t nat -L OUTPUT >/dev/null 2>&1 \
        || fail "IPv4 nat OUTPUT table is unavailable"

    remove_ipv4_chain
    iptables -t nat -N "$CHAIN"

    for destination in \
        0.0.0.0/8 \
        10.0.0.0/8 \
        100.64.0.0/10 \
        127.0.0.0/8 \
        169.254.0.0/16 \
        172.16.0.0/12 \
        192.0.0.0/24 \
        192.0.2.0/24 \
        192.168.0.0/16 \
        198.18.0.0/15 \
        198.51.100.0/24 \
        203.0.113.0/24 \
        224.0.0.0/4 \
        240.0.0.0/4
    do
        iptables -t nat -A "$CHAIN" -d "$destination" -j RETURN
    done

    iptables -t nat -A "$CHAIN" -p tcp -m owner ! --uid-owner 0 \
        -j REDIRECT --to-ports 12345
    iptables -t nat -A OUTPUT -p tcp -j "$CHAIN"
}

remove_ipv6_chain() {
    while ip6tables -t nat -C OUTPUT -j "$CHAIN" >/dev/null 2>&1; do
        ip6tables -t nat -D OUTPUT -j "$CHAIN"
    done
    ip6tables -t nat -F "$CHAIN" >/dev/null 2>&1 || true
    ip6tables -t nat -X "$CHAIN" >/dev/null 2>&1 || true
}

setup_ipv6_redirect() {
    ip6tables -t nat -L OUTPUT >/dev/null 2>&1 \
        || fail "IPv6 default route exists but IPv6 nat OUTPUT is unavailable"

    remove_ipv6_chain
    ip6tables -t nat -N "$CHAIN"

    for destination in \
        ::/128 \
        ::1/128 \
        ::ffff:0:0/96 \
        64:ff9b:1::/48 \
        100::/64 \
        2001:db8::/32 \
        fc00::/7 \
        fe80::/10 \
        ff00::/8
    do
        ip6tables -t nat -A "$CHAIN" -d "$destination" -j RETURN
    done

    ip6tables -t nat -A "$CHAIN" -p tcp -m owner ! --uid-owner 0 \
        -j REDIRECT --to-ports 12346
    ip6tables -t nat -A OUTPUT -p tcp -j "$CHAIN"
}

setup_ipv4_redirect

if [ -n "$(ip -6 route show default)" ]; then
    setup_ipv6_redirect
elif ip6tables -t nat -L OUTPUT >/dev/null 2>&1; then
    remove_ipv6_chain
fi

exec /usr/local/bin/xray run -c "$BASE_CONFIG" -c "$SECRET_PATH"
