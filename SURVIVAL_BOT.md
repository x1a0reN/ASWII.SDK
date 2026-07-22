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

All route generation is RAIN-only. Direct-physics pursuit, pending probe movement,
and the physics-grid 2.5D fallback are disabled. Maps that do not ship a native
navigation prefab, including `level33`, build an owned RAIN graph from active terrain
colliders after the scene reaches `Level.State.kReady`. RAIN paths are sanitized,
surface-smoothed, moved away from tight wall corners, and physically vetoed before
they reach the follower; physics checks never generate an alternate route.
`level33` uses a process-resident long-run graph with a `0.20` cell size, `0.16`
maximum vertex error, and `3` metre maximum segments. The legacy `0.10` maximum-
detail files remain untouched, but gameplay never loads them because their 625,403-
node expanded graph leaves insufficient address space for a second Unity scene load
in the 32-bit client. The first successful long-run load deserializes,
initializes, and registers the graph, then releases the redundant serialized payload.
Leaving a round unregisters the graph before the next `GameLoading`, clears every
scene-owned player/target reference, detaches its mount transform, and destroys the
old hidden host. The already materialized managed graph remains process-resident.
Re-entering physical scene `level33` waits for `Level.State.kReady`, creates a fresh
scene-local mount, and re-registers that same graph without disk I/O, deserialization,
graph initialization, or rebuilding. Keeping the graph registered or retaining its
Unity mount across a second `GameLoading` is forbidden because live testing proved it
causes a native `0xc0000005` before the resume hook runs. Any request for another
physical map fails closed instead of loading a second runtime graph. A later game
process loads the validated graph from
`Application.persistentDataPath/ASWDEBUG/NavMeshCache`. The wrapper verifies the
map resource fingerprint, RAIN module identity, generator settings, graph format,
size limits, and SHA-256 before deserialization; any mismatch falls back to a fresh
build. Process shutdown releases the resident graph without deleting the disk cache.
The long-run base and derived artifacts are stored separately as
`level33.runtime.rainnav` and `level33.runtime.rainmeta`. A first cache miss is allowed
to build for up to 15 minutes in the private level33 test scene. Before every later
scene load, the client requires at least 1.4 GiB total free address space and a
1.25 GiB largest free region. This admits the measured `0.20` resident graph while
still rejecting the old `0.10` graph's second-load footprint. The address-space
probe is sampled rather than executed every rendered frame; otherwise it remains
in the lobby instead of risking a native Unity allocation crash.

`Map Bake` is a separate scene-only cache generation mode. It can be enabled before
or after entering a manually opened map and never starts matching, movement, target
selection, aiming, firing, rank handling, suicide, cards, or rematching. For maps
other than `level33`, if there is no compatible maximum-detail cache it builds the complete
RAIN graph with a `0.10` cell size, `0.10` maximum vertex error, `2` metre maximum
segments, and all available CPU workers. Collider discovery and graph generation
have no fixed timeout in this mode. The finished graph is serialized to the normal
disk-cache directory. `level33` is always forced to its isolated long-run profile;
there is no fallback to the legacy maximum-detail file. Enabling the mode on a map
that already has a compatible cache validates and registers that cache instead of
rebuilding it.
After the base graph is ready, a versioned companion cache (`.rainmeta`) is built
without overwriting `.rainnav`. It records connected components, boundary/ledge
samples, surface clearance, eight-direction cover masks, dead-space/headroom-safe
spawn samples, and validated directed Jump/Drop Off-Mesh Links. Candidates come only
from spatially indexed boundary edges and validate the complete arc and landing.
Compatible links are injected for the current profession and removed when the graph
is deactivated. Link paths retain raw takeoff/landing anchors so funnel smoothing
cannot erase the jump. Map Bake returns only after both cache files are saved.
Once contour generation has started, normal match completion and `Level.Exit` no
longer cancel it. Generation continues in the background after leaving the map and
the UI keeps showing its live progress until the graph is written to disk. Closing
the game process still loses an unfinished RAIN build because RAIN exposes graph
serialization only after contour generation completes; this is background
continuation, not a cross-process checkpoint.

The UI also exposes a map-resource selector and `Direct Load and Bake` action. Map
names are discovered from the installed `FileInfo.xml`. The action uses the native
`GameLoadingState -> Level.Initialize -> Level.LoadMap -> Fight` transition without
creating or joining a server match. Native navigation loading stays disabled so the
selected ASWDEBUG graph profile is built. AFK auto-leave is disabled for this private
scene, and local `SyncPlayerData` packets are suppressed until the scene exits, so
the scene can remain open until disk serialization completes. The selector enumerates
every game mode and map from the game's channel `level_list`, displaying
`mode + localized LevelInfo.show_name`; internal `levelXX` resource keys are not shown
in the UI. Each option is uniquely identified by its exact `LevelInfo.id`, logical key,
and `game_type`, and the native level Lua remains responsible for resolving
the final scene through `SetMesh`. The `Level.LoadMap` hook records that resolved scene
without rewriting it, keeping the Chinese name, camera data, map parameters, and scene
geometry aligned. After a non-empty disk cache is confirmed, the
loader automatically changes back to `Lobby`, allowing the
native `FightState.OnExit -> Level.OnExit` path to destroy the temporary map scene.

Forced hunt does not select a standoff or interception point. It continuously sends
the live enemy position to RAIN and keeps routed pursuit active while ranged fire is
running. Opportunity attacks retain their short combat strafe behavior. Survival
combat reuses its role detection and tactics for heavy, medic/guard, and
assault/sniper loadouts.

Press `Delete` to show or hide the project-style configuration panel. Its navigation
card shows build phase, progress, elapsed time, colliders, graph nodes, bounds,
worker count, base/derived cache status and size, component/boundary/surface counts,
Jump/Drop link counts, safe samples, and the active RAIN route provider. Press `F8`
to stop or restart the loop. Settings are persisted with namespaced `PlayerPrefs`.
While any bot mode is active, the remaining route is rendered in world space as
a terrain-snapped red/orange guide line with waypoint markers. The next point is
yellow and the final destination is green; world geometry occludes the guide.
The renderer tries compatible built-in shaders first, then reuses a compatible
shader from a loaded scene material, so stripped legacy shader names do not disable
the guide or produce per-frame log spam.

`level33 Navigation Patrol` is a navigation-only cache test. It directly loads the
survival level that resolves to physical `level33`, constrains the player and four
stationary Bots to the level Lua `map_center` / `map_size` interior, and rejects points
inside the game's `DeadSpace` volumes. The player walks to each Bot in sequence using
only RAIN and its validated Off-Mesh Links, then advances to the next one.
Target combat selection, role tactics, aiming, scoping, skills, and firing are not run.

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
