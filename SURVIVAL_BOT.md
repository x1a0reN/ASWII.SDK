# ASWII Survival Bot Fork

This branch is intentionally limited to the survival automation runtime.

## Runtime loop

1. Wait for the deserter penalty to reach zero.
2. Match `RoomInfo.GameType.kGameTypeChiji` and retry after 600 seconds.
3. Freeze the maximum participant set observed during the first five seconds.
4. Stay in cover while more than half of the initial participants are alive.
5. Switch to strict-line-of-sight ranged attack when the alive count reaches the top-half threshold.
6. After one kill or assist, navigate to a cliff and jump; use `Suicide(uid)` only as a timeout fallback.
7. Flip the server-provided number of reward cards and return to matching.

Press `F8` to stop or restart the loop.

## GM signal

The live assembly has no public "someone is watching me" flag. The fork observes
`ChannelConnection.ParseCharacterInfo` without consuming the packet and treats a
remote character record with `team >= 2` as the GM/viewer candidate. It leaves the
match immediately. Three consecutive GM-exit rounds stop the loop.

This protocol signal is grounded in the current deobfuscated assembly, but still
requires a real GM observation session to validate the server-side packet behavior.
