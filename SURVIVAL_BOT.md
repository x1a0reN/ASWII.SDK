# ASWII Survival Bot Fork

This branch is intentionally limited to the survival automation runtime.

## Runtime loop

1. Wait for the deserter penalty to reach zero.
2. Match `RoomInfo.GameType.kGameTypeChiji`; the timeout is configurable from 5 to 15 minutes.
3. Read the initial room roster, wait for `game_state == kAlive`, then lock the maximum observed participant count.
4. Stay in cover while more than half of the initial participants are alive.
5. Switch to strict-line-of-sight ranged attack when the alive count reaches the top-half threshold.
6. After one kill or assist, navigate to a cliff and jump; use `Suicide(uid)` only as a timeout fallback.
7. Flip the server-provided number of reward cards and return to matching.

The original physics-grid 2.5D route planner is retained as the fallback. Maps that
do not ship a native navigation prefab, including `level33`, build an owned RAIN
NavMesh from the active terrain colliders after the scene reaches `Level.State.kReady`.
The route order is direct physics, validated RAIN path, then the 2.5D grid. Runtime
RAIN graphs are generated once per map and cached both in memory and on disk.
Map exit only unregisters the in-memory graph; returning to the same map registers
it immediately. A later game process loads the validated graph from
`Application.persistentDataPath/ASWDEBUG/NavMeshCache`. The wrapper verifies the
map resource fingerprint, RAIN module identity, generator settings, graph format,
size limits, and SHA-256 before deserialization; any mismatch falls back to a fresh
build. Plugin shutdown releases memory graphs without deleting the disk cache. The
first build uses RAIN's automatic half-CPU worker count and a `0.25` cell size so
the pre-round wait is used for maximum generation throughput and path detail.

`Map Bake` is a separate scene-only cache generation mode. It can be enabled before
or after entering a manually opened map and never starts matching, movement, target
selection, aiming, firing, rank handling, suicide, cards, or rematching. If the map
does not already have a compatible maximum-detail cache, it builds the complete
RAIN graph with a `0.10` cell size, `0.10` maximum vertex error, `2` metre maximum
segments, and all available CPU workers. Collider discovery and graph generation
have no fixed timeout in this mode. The finished graph is serialized to the normal
disk-cache directory; later normal bot modes prefer this maximum-detail cache and
fall back to the runtime profile only when it is absent. Enabling the mode on a map
that already has a compatible cache validates and registers that cache instead of
rebuilding it.

Forced hunt does not select a standoff or interception point. It follows the live
enemy position directly in open space, falls back to global routing only when
blocked or crossing levels, and keeps pursuit movement active while ranged fire is
running. Opportunity attacks retain their short combat strafe behavior. Survival
combat reuses its role detection and tactics for heavy, medic/guard, and
assault/sniper loadouts.

Press `Delete` to show or hide the project-style configuration panel. Its navigation
card shows build phase, progress, elapsed time, colliders, graph nodes, bounds,
worker count, cache source/status/size, and the active route provider. Press `F8`
to stop or restart the loop. Settings are persisted with namespaced `PlayerPrefs`.
While any bot mode is active, the remaining route is rendered in world space as
a terrain-snapped red/orange guide line with waypoint markers. The next point is
yellow and the final destination is green; world geometry occludes the guide.
The renderer tries compatible built-in shaders first, then reuses a compatible
shader from a loaded scene material, so stripped legacy shader names do not disable
the guide or produce per-frame log spam.

`Open Room Test` is a separate direct-combat mode for manually created rooms. It
never starts matching or runs rank, suicide, reward-card, or rematch behavior. It
waits for a ready `Level`, local player, camera, and either the normal in-game
connection state or a live opponent in the loaded room, then enables the existing
role-aware target selection, routing, aiming, and firing strategy.

## Second-client network route

At bootstrap, clients using the same executable are ordered by process start time.
The first client remains direct; only the second client starts a restricted SSH
tunnel. Direct `Socket.Connect(string, int)` calls use authenticated SOCKS5,
`HttpWebRequest` uses the authenticated HTTP proxy, and `WWW(string)` downloads are
rewritten through an in-process loopback relay. Explicit `Dns.GetHostEntry` and
`Dns.GetHostAddresses` calls use the server-side DNS relay. Launcher IPC to loopback
is excluded.

The private runtime configuration is read from
`Application.persistentDataPath/Config/proxy.local.ini`. Keep it outside Git; use
`Config/proxy.example.ini` as the schema. The proxy password supports an environment
variable or a current-user DPAPI value. If the second client's tunnel fails, routing
fails closed and the survival loop stops instead of falling back to the local IP.

## Authentication scope

The fork does not compile or start the old `Verify/EyAuthManager` network
authorization, heartbeat, token, or expiry workflow. Packet hooks that remain are
gameplay lifecycle signals used for matching, ranks, cards, and GM/viewer detection.

## GM signal

The live assembly has no public "someone is watching me" flag. The fork observes
`ChannelConnection.ParseCharacterInfo` without consuming the packet and treats a
remote character record with `team >= 2` as the GM/viewer candidate. It leaves the
match immediately. Three consecutive GM-exit rounds stop the loop.

This protocol signal is grounded in the current deobfuscated assembly, but still
requires a real GM observation session to validate the server-side packet behavior.
