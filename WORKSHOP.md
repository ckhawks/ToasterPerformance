[h1]ToasterPerformance[/h1]

[b]Same DLL, client and dedicated server.[/b] Every patch self-gates at runtime, so the right behavior happens automatically. Designed to plug into a vanilla install with no config — drop in, restart, done.

[hr][/hr]

[h2]What you'll actually notice[/h2]

[list]
[*][b]Fewer micro-stutters[/b] — the small hitches you feel as choppy puck physics or jittery other players come from the garbage collector pausing the game. This mod cuts the rate at which garbage piles up by [b]roughly 16x on the server[/b], so the GC fires less and pauses are shorter.
[*][b]Smaller player-join hitches[/b] — the freeze when someone connects mid-match drops from around [b]250 ms to ~40 ms[/b].
[*][b]Smoother dedicated servers[/b] — at high tickrate the server eats a lot of its own budget on housekeeping. We cut the biggest housekeeping costs (sync-object encoding, player/puck list rebuilds, collision recorder lookups) by 10-500x each.
[*][b]No new features.[/b] No new commands, no new UI, no menu options. The mod is invisible if it's working.
[/list]

[h2]Measured impact[/h2]

60-second server captures, 12 players, vanilla vs patched:

[table noborder=1]
[tr][th]Metric[/th][th]Vanilla[/th][th]Patched[/th][th]Change[/th][/tr]
[tr][td]Managed garbage per minute[/td][td]44.4 MB[/td][td]2.76 MB[/td][td][b]16x less[/b][/td][/tr]
[tr][td]EventManager.TriggerEvent calls/min[/td][td]32,316[/td][td]366[/td][td][b]88x fewer[/b][/td][/tr]
[tr][td]Server_GatherSynchronizedObjectData/min[/td][td]307 ms[/td][td]0.6 ms[/td][td][b]520x faster[/b][/td][/tr]
[tr][td]PuckManager.GetPucks total time[/td][td]9.2 ms[/td][td]0.6 ms[/td][td][b]16x faster[/b][/td][/tr]
[tr][td]Server GC.Collect per minute[/td][td]16[/td][td]9[/td][td]1.8x fewer[/b][/td][/tr]
[tr][td]Player-join hitch[/td][td]~250 ms[/td][td]~40 ms[/td][td][b]6x smaller[/b][/td][/tr]
[/table]

[i]Steady-state CPU is unchanged because the server already had headroom — the wins are in fewer GC pauses and smaller worst-case frames, which compound over a long match.[/i]

[hr][/hr]

[h2]Under the hood (for the curious)[/h2]

[list]
[*][b]Sync-object dispatch[/b] — replaces the per-tick O(N²) [code]List.Find[/code] in the client's NetCode hot path with an O(1) dictionary. Server-side gather inlines encoding and reuses buffers across frames.
[*][b]Manager caches[/b] — [code]PlayerManager.GetPlayers[/code], [code]PuckManager.GetPucks[/code], and the replay variants stop allocating a fresh filtered list per call. Fresh list references swapped on rebuild (enumeration-safe during cascading despawns).
[*][b]Collision recorder[/b] — [code]NetworkObjectCollisions[/code] getter cached per instance. [code]GetPlayerPuck[/code] rewritten as a direct [code]NativeArray[/code] index access instead of a LINQ scan.
[*][b]Spectator + minimap[/b] — [code]SpectatorManager.Update[/code]'s per-frame [code]ToList()[/code] cached. [code]UIMinimap[/code]'s per-player [code]Query("Body")[/code] cached on player add.
[*][b]Camera.main caching[/b] — [code]UIPlayerUsernames[/code] stops doing a tag lookup three times per player per frame.
[*][b]Client physics gates[/b] — [code]Puck.FixedUpdate[/code] swaps [code]Physics.CheckSphere[/code] for a cheap y-position check on clients (server stays authoritative). [code]Hover.FixedUpdate[/code] runs every other tick. [code]StickPositioner.FixedUpdate[/code] is skipped on clients.
[*][b]Listener-less event skips[/b] — events like [code]OnPlayerBodySpeedChanged[/code] / [code]StaminaChanged[/code] / [code]PingChanged[/code] early-out at the fire site when no listener is registered. On dedicated servers, a scanning driver strips the no-op HUD listeners so the early-out kicks in.
[/list]

[hr][/hr]

[h2]Compatibility[/h2]

[list]
[*][b]Same DLL on client and dedi.[/b] Drop into both [code]Puck\Plugins\[/code] and [code]Puck Dedicated Server\Plugins\[/code].
[*][b]Plays nicely with other mods.[/b] Patches use conservative prefixes/postfixes, re-check listener state at every fire, and don't mutate vanilla data structures in place.
[*][b]Mods that legitimately subscribe to optimized events are respected[/b] — the fire-site early-outs only trigger when no listener exists.
[/list]

[h2]Safety[/h2]

Every patch falls into one of three patterns:

[olist]
[*][b]Cache + invalidate on canonical mutator.[/b] Failure mode is stale data; mitigated by self-heal checks ([code]HasListener[/code] re-checks the events dict every fire) and contract-preserving rebuilds (the cached [code]GetPlayers[/code] keeps the vanilla "filter dead entries" semantics).
[*][b]Skip work with no observable effect.[/b] Verified per case.
[*][b]Replace an expensive primitive with a cheap equivalent.[/b] [code]Physics.CheckSphere[/code] → [code]transform.position.y < threshold[/code] on a flat ice surface; [code]LastOrDefault[/code] LINQ → direct array index.
[/olist]

The mod does [b]not[/b] modify any vanilla physics, RPC, input, rendering, save, or networking code.

[h2]Known limitations[/h2]

[list]
[*]Reflection is used to access several private fields by name. If a future Puck build renames a field, the affected patch fails to load and logs a warning — the rest continues to function.
[*][code]Camera.main[/code] cache lazily re-fetches on null, but assumes the active camera is destroyed when swapped. If a mod just toggles [code]enabled[/code], the username overlay may briefly use a stale reference.
[*]The Hover throttle introduces a one-fixed-tick (~20 ms) delay in client-side [code]IsGrounded[/code]. Toggle off via [code]ThrottleHoverOnClient = false[/code] and rebuild if you see hover wobble.
[/list]

[hr][/hr]

[h2]Install[/h2]

[olist]
[*]Subscribe (or place [code]ToasterPerformance.dll[/code] into [code]Puck\Plugins\ToasterPerformance\[/code] for clients and [code]Puck Dedicated Server\Plugins\ToasterPerformance\[/code] for servers).
[*]Restart the game.
[*]Confirm [code][ToasterPerformance] All patched![/code] appears in [code]Logs\Puck.log[/code].
[/olist]

[h2]Companion tool[/h2]

A separate diagnostic profiler ([b]ToasterProfiler[/b]) provides [code]/profile dump[/code], windowed captures, and auto-dump on >25 ms main-thread spikes. Not required for ToasterPerformance to work.

[h2]Source[/h2]

[url=https://github.com/ckhawks/ToasterPerformance]github.com/ckhawks/ToasterPerformance[/url]
