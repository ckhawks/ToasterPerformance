# ToasterPerformance — testing checklist

Running list of things to verify in-game. Check items off as you go; flag failures with the date and a one-line repro so we can untangle them.

---

## Unit 1 — SyncObjects (#3 + #1 + #2 + #3a + #B)

### Smoke — does it load at all
- [ ] On client launch, log contains `[ToasterPerformance] Enabling 0.1.0...`
- [ ] Log contains `[ToasterPerformance] All patched!` (no `TypeInitializationException`, no `Failed to Enable`)
- [ ] No errors in console during the first server join

### Correctness — does the game still play
- [ ] Join a server with ≥2 other players. Skate around for 30s. No teleporting, no stuck players, no missing puck.
- [ ] Watch another player skate, sprint, slide. Their movement looks smooth (not jittery, not frozen).
- [ ] Shoot a puck. Puck visibly moves on your screen and registers contact with other players.
- [ ] Goal scored. Score updates, replay plays back.
- [ ] Disconnect and reconnect. Other players still visible, still moving.
- [ ] Open Network Smoothing in settings (if you have it on) — toggle it off and on mid-match. No crash, players still update.

### Perf — did anything actually get faster
- [ ] Frame time during a busy 6v6 puck cluster — note before/after with the in-game frame profiler if you have one, or by eyeballing the stutter cadence.
- [ ] Specifically watch for the multi-player stutter we were targeting. Did it reduce?
- [ ] GC spikes — if you have a profiler, look at allocations/sec during a match. Should drop visibly on the client side (fewer closure + List allocs per tick).

### Edge cases worth poking
- [ ] Host a listen-server match yourself (not dedicated). Both client and server patches run in one process — verify no double-patch crashes.
- [ ] Watch a replay. Replay players use a different code path (cloned NetworkObjects); make sure they animate.
- [ ] Spawn / despawn cycle: have a player leave and rejoin mid-match. Their `SynchronizedObject` should re-register in the dict (the `EnsureDictPopulated` self-heal covers this).
- [ ] Pause / unpause if available — accumulator behavior might surface here.

### Known risks to watch for
- **`Event_OnSynchronizeObjects` listeners**: my Prefix on `EventManager.TriggerEvent` only skips downstream work when *no listener* is registered. Any mod that subscribes (e.g. profilers, telemetry overlays) still gets dispatched normally — but the *call site* still allocates a Dictionary every tick. That's not regressed, just not yet fixed.
- **Smoothing-path snapshot list**: I pre-size a new `List<SynchronizedObjectSnapshot>` per tick. If you see weird interpolation glitches (players snapping or floating), that's the suspect.
- **Dict back-fill**: if you load the mod while already connected to a server, the dict starts empty and self-heals on the next tick via count-mismatch detection. There's a 1-tick window where lookups can miss. If you see a single frame of frozen-other-players on mod hot-reload, that's expected and harmless.

---

## Unit 2 — Manager caching (#4)

### Correctness
- [ ] Player list operations still work: scoreboard shows all players, team rosters correct, kick/mute/admin commands target the right player.
- [ ] Player joins mid-match → appears in scoreboard, on minimap, in vote-screen targets, in chat tab-complete.
- [ ] Player leaves mid-match → removed from all UIs within 1–2 frames.
- [ ] Replay players (replay viewer): they appear separately from live players. `GetReplayPlayers` should return only replay entries; `GetPlayers(false)` should not include them.
- [ ] Puck spawn / despawn around phase change → puck count UI updates, replay reconstructs all spawned pucks.
- [ ] Spectator camera follows live players (not replay players).

### Edge cases
- [ ] Quick reconnect (leave + rejoin within 5s) — does the player slot fill correctly?
- [ ] Two players with the same number/username — `GetPlayerByNumber` / `GetPlayerByUsername` still resolve.
- [ ] Disconnect during a goal celebration → `Server_DespawnPucks` still drains all pucks.

### Known risks
- **Cache mutation**: original `GetPlayers(true)` returned the underlying list — a caller that called `.Add()` or `.Clear()` on the return would mutate state. My cached return is a *different* list. If any caller mutates the return, it will desync. Watch for "X stopped appearing after Y" symptoms.
- **`IsReplay.Value` semantics**: my rebuild splits replay vs non-replay by `IsReplay.Value`. If `IsReplay` flips after Add (it shouldn't), the cache will be stale until next Add/Remove or until validation catches it.

---

## Unit 3 — Collision recorder + GetPlayerPuck (#5 + #6 + #G)

### Correctness
- [ ] Player picks up the puck, shoots it → goal scored / save / pass works as before.
- [ ] Stick-puck collision detection visually matches old behavior (no missed touches, no phantom contacts).
- [ ] `PuckManager.GetPlayerPuck(clientId)` returns the puck the player most recently touched — verify with a HUD mod or by watching whose stick the "possession" indicator follows.

### Edge cases
- [ ] Stick despawns (player disconnect mid-shot) — no NullRef in the GetPlayerPuck path.
- [ ] Multiple pucks (TRS phase or modded pucks) — `GetPlayerPuck` picks the last-touched one consistently.

### Known risks
- **OnBufferChanged**: my cache invalidation hooks the private `OnBufferChanged`. If that method's name is mangled in a future Puck build, the cache will return stale data with no error. Symptom: HUD shows the wrong puck for possession.

---

## Unit 4 — UI + LayerMask caching (#C + #F only)

**Note:** #D (Puck.FixedUpdate client gate) and #E (StickPositioner client gate) were **dropped from this unit**. #D would break goal-net cloth simulation (`NetSphereCollider.radius` is consumed by `GoalController` client-side). #E needs in-game verification of blade visuals before it's safe.

### Correctness
- [ ] Player username labels appear above other players' heads, fade with distance, hide when behind camera.
- [ ] After camera changes (replay camera, spectator switch, scene reload) — usernames still track. **This is the #1 thing to watch.** My cache assumes Camera.main is stable; if it goes null, I lazily re-fetch. If it's *swapped* for another camera object, the cache will hold the old reference until the old camera is destroyed.
- [ ] Stick-on-ice sound plays when stick first touches ice (`OnGrounded` path). LayerMask was cached on first call — verify the sound triggers.

### Known risks
- **Camera swap without nulling**: if Puck swaps the main camera (e.g. for spectator view) by activating a different Camera and keeping the old one alive, my cached reference stays pointed at the old (now inactive) camera. Usernames will look wrong. If you see this, I'll add a `Camera.main` poll every N frames as fallback.

---

## How to report a failure

Paste the offending log block (search the log for `[ToasterPerformance]` first, then include 10 lines above and below). If it's a visual bug, a video clip beats words. Note which Unit was the last one I shipped before you saw it — that narrows the suspect set immediately.
