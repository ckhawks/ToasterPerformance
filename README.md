# ToasterPerformance

Harmony patches that reduce GC pressure and per-tick CPU cost on Puck clients and
dedicated servers. Same DLL on both — every patch self-gates at runtime, so the
right behavior happens automatically.

## Measured impact (60-second server captures, vs. vanilla)

| Metric | Vanilla | Patched | |
|---|---:|---:|---:|
| Total managed garbage / 60s | 44.4 MB | 2.76 MB | **16× less** |
| `EventManager.TriggerEvent` calls / 60s | 32,316 | 366 | **88× fewer** |
| `Server_GatherSynchronizedObjectData` time / 60s | 307 ms | 0.6 ms | **520× faster** |
| `PuckManager.GetPucks` time | 9.2 ms | 0.6 ms | **16× faster** |
| Server GC.Collect / 60s | 16 | 9 | 1.8× fewer |
| Player-join hitch | ~250 ms | ~40 ms | 6× smaller |

Steady-state CPU is unchanged because the server had headroom. The wins are in
**fewer GC pauses and smaller worst-case frames**, which compound over a long match.

## What it patches

- **Sync-object dispatch** — O(1) dictionary replaces O(N²) `List.Find` per tick on
  the client. Server-side gather inlines encoding and reuses buffers.
- **Manager caches** — `PlayerManager.GetPlayers`, `PuckManager.GetPucks`, replay
  variants. Fresh lists swapped on rebuild (enumeration-safe).
- **Collision recorder** — `NetworkObjectCollisions` getter cached per instance.
  `GetPlayerPuck` rewritten as direct `NativeArray` index access.
- **Spectator + minimap** — `SpectatorManager.Update`'s per-frame `ToList()`
  cached. `UIMinimap.Update`'s per-player `Query("Body")` cached on add.
- **Camera.main caching** — `UIPlayerUsernames` no longer hits tag lookup per
  player per frame.
- **Client physics gates** — `Puck.FixedUpdate` swaps `Physics.CheckSphere` for
  a cheap `y < threshold` check. `Hover.FixedUpdate` runs every other tick on
  clients. `StickPositioner.FixedUpdate` skipped on clients (server stays
  authoritative).
- **Listener-less event skips** — `OnPlayerBodySpeedChanged/StaminaChanged/PingChanged`
  early-out when nothing listens. On dedicated servers, a scanning driver strips
  the no-op HUD listeners so the early-out kicks in.

## Compatibility

- **Same DLL on client and dedi.** Drop into both `Puck/Plugins/` and
  `Puck Dedicated Server/Plugins/`.
- **Plays nicely with other mods.** Patches use conservative prefixes/postfixes,
  re-check listener state at every fire, and don't mutate vanilla data structures
  in place.
- **Other mods that legitimately subscribe to optimized events are respected** —
  the fire-site early-outs only when *no* listener exists.

## Safety

Every patch falls into one of three patterns:

1. **Cache + invalidate on canonical mutator.** Failure mode is stale data;
   mitigated by self-heal checks (`HasListener` re-checks the events dict
   every fire) and contract-preserving rebuilds (the cached `GetPlayers`
   keeps the vanilla "filter dead entries" semantics).
2. **Skip work with no observable effect.** Verified for each case.
3. **Replace expensive primitive with cheap equivalent.** `Physics.CheckSphere` →
   `transform.position.y < threshold` on a flat ice surface; `LastOrDefault`
   LINQ → direct array index.

The mod does **not** modify any vanilla physics, RPC, input, rendering, save,
or networking code.

### Known limitations

- Reflection is used to access several private fields by name. If a future
  Puck build renames a field, the affected patch fails to load and logs a
  warning; the rest continues to function.
- `Camera.main` cache lazily re-fetches on `null` but assumes the active camera
  is destroyed when swapped. If a mod just toggles `enabled`, the username
  overlay may briefly use a stale reference.
- The Hover throttle introduces a one-fixed-tick (~20 ms) delay in client-side
  `IsGrounded`. Toggle off via `ThrottleHoverOnClient = false` and rebuild
  if you see hover wobble.

## Install

1. Place `ToasterPerformance.dll` (or whatever you build it as) into:
   - `Puck/Plugins/ToasterPerfPatches/` (client)
   - `Puck Dedicated Server/Plugins/ToasterPerfPatches/` (server)
2. Restart the game.
3. Confirm `[ToasterPerfPatches] All patched!` in `Logs/Puck.log`.

## Companion tool

A separate diagnostic profiler ([ToasterProfiler]) provides `/profile dump`,
windowed captures, and auto-dump on >25 ms main-thread spikes. Not required
for PerfPatches to work.

[ToasterProfiler]: ../ToasterProfiler

## License

[Add your license here.]
