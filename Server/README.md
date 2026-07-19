# Server-side relay

The second client reaches these services only through restricted SSH local
forwarding. Keep all three listeners on `127.0.0.1`:

- `39080/tcp`: authenticated SOCKS5 (Dante)
- `39081/tcp`: authenticated HTTP proxy (Tinyproxy)
- `39082/tcp`: `dns_relay.py`

Install `dns_relay.py` at `/opt/aswrelay/dns_relay.py` and the unit at
`/etc/systemd/system/asw-dns-relay.service`. Add this server-local resolver alias:

```text
127.0.0.1 asw-dns-relay.internal
```

The tunnel account must have no password login, no terminal, no command execution,
and `PermitOpen` limited to the three loopback ports above. The game configuration,
SSH private key, pinned host key, and proxy password stay outside Git.
