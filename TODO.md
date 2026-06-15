# BodyFlux — Feature Backlog

Items chosen for future implementation. Completed items are summarised under **Implemented** at the bottom.

The primary backlog is currently clear — every committed feature is implemented. Candidates for what to tackle next live under **Secondary / Future Considerations**.

---

## Secondary / Future Considerations

No commitment — noted for potential exploration after the primary backlog is complete.

- **Export / Import Presets via clipboard** — serialize the preset slots to JSON and copy to clipboard; paste to import. Enables sharing configs between characters and users without manual file editing.

- **Hotkeys** — bind Apply / Pause / Reset / Reverse to in-game keybinds via Dalamud's `IGamepadActionManager` or key-intercept hooks. Particularly useful for hands-free operation during GPose.

- **Custom Easing Curve** — an in-UI bezier / spline editor in the Player Advanced sub-tab. `EasingHelper` is already designed to be extensible; this adds a `Custom` enum value backed by user-defined control points.

- **Event Triggers** — fire a morph automatically on a game event (zone change, duty start, emote, etc.) via Dalamud event services. Scope and safety (preventing runaway loops) would need careful design.

- **Hold at endpoints** — in Loop modes, pause for a configurable duration at 0% and 100% before reversing. Enables natural breathing/pulse effects with Loop Infinite. Likely implemented as `HoldDuration` in `MorphController` with a countdown timer at each boundary.

- **Stop at target %** — stop the morph at a configurable progress value (e.g. 50%) instead of always 100%. Useful for permanent partial morphs without needing an intermediate Customize+ profile. One `float TargetProgress` field in `MorphController`, checked in `Tick()` alongside the existing boundary logic.

---

## Implemented

- **Targeted player morph (remote)** ✓ — Morphing other Lightless-synced players, with correct
  scaling restore on Reset. The blocker was that a peer's scaling is applied by Lightless as a
  *temporary* C+ profile, unreadable locally (confirmed: C+ filters `IsTemporary` on every read,
  and `Profile.OnUpdate` only yields `(index, GUID)` — no JSON; Lightless exposes no IPC for it).
  - **Solution (proactive base-profile sharing)**: each peer reads its own *permanent* C+ profile
    (only it can read it locally — same path as self-morph) and broadcasts it as a `base` field in
    every `hello`. Peers cache it (`NetworkSync._peerBaseProfile`). A targeted morph uses the
    cached base as origin; Reset re-applies it via `SetTempProfile`. Because we re-apply the exact
    scaling Lightless holds, Lightless re-syncing no longer causes a vanilla snap.
  - Re-broadcast on change is driven by subscribing to C+ `Profile.OnUpdate` for slot 0
    (`CustomizePlusIpc.ProfileUpdated` → `Plugin.RefreshLocalBaseProfile`).
  - UI shows whether the selected target's base scaling has been received yet.
  - The network target is captured at morph start (`_morphTargetName`) and used for both broadcast
    and Reset frames, so a stale UI selection — or a self-only Sequence — never retargets peers' frames.
- **Reverse Morph** ✓ — button in Player and Brio tabs; flips `_direction` mid-morph or while paused.
- **Easing Curves** ✓ — `EasingHelper` + `EasingMode` enum (Linear/EaseIn/EaseOut/EaseInOut). Player tab has Simple/Advanced sub-tabs; easing is per-tab (Player vs Brio). Player footer (progress + controls) always visible.
- **Speed independent per tab** ✓ — `GrowthSpeed` / `BrioGrowthSpeed` saved separately.
- **Quick-Access Profile Presets** ✓ — 20 numbered slots in the Presets tab; `/bodyflux preset 1–20 [speed]` applies from chat. Saves profileId, speed, mode, easing. Slot count is centralised in `Configuration.PresetSlots`.
- **Morph History** ✓ — last 5 morphs shown as a collapsible "Recent Morphs" section, for both the Player tab and the Brio tab (separate lists: `RecentMorphs` / `BrioRecentMorphs`). Deduped by profile, one-click re-apply. A spaced separator divides the controls from the history list.
- **Morph Sequences** ✓ — named A→B→C chains where each step morphs from the previous step's destination, advancing automatically on completion. Implemented for **both** the Player tab and the Brio tab (separate lists: `Sequences` / `BrioSequences`; models `MorphSequence` + `MorphSequenceStep`). Each step has its own speed and easing and always runs in Simple mode. The engine (`StartSequence` / `StartBrioSequence` / `StartSequenceStep` / `AdvanceSequence` in `Plugin.cs`) resolves all step profiles up front (fail-fast). Brio sequences resolve the first-step origin like `StartBrioMorph` (MCDF → permanent → identity) and restore via `SetTempProfile` on Stop when the origin was an MCDF. UI: a **Sequences** sub-tab in each of Player and Brio, with one collapsible header per sequence (New/Delete/rename, add/remove/reorder steps) and a shared playback footer (Play/Pause/Resume/Stop + "step X / N"). The Brio sub-tab uses the actor + MCDF selected in the **Single** tab. (The later multi-actor Brio work moved sequence state onto `MorphSession`, so sequences now run per-session and concurrently rather than sharing one controller.)
- **Brio Presets** ✓ — the preset system also covers Brio morphs (20 slots, `BrioPresets`). Each Brio preset captures not just profile/speed/mode/easing but also the **target actor** (name + index) and the loaded **MCDF origin** (JSON + label), so Apply re-targets the original actor with the same starting scaling. The actor must be present in the current GPose scene; Apply is disabled with a tooltip otherwise. `MorphPreset` gained optional trailing fields (`OriginMcdfJson`, `OriginMcdfLabel`, `TargetActorName`, `TargetActorIndex`) — backward-compatible with old configs.
- **Brio actor morphs** ✓ — Brio tab in `MainWindow.cs`, enabled only in GPose (`IClientState.IsGPosing`).
  - **Origin problem & solution**: Customize+ exposes *no* IPC to read a clone's temporary profile
    (verified by decompiling C+ 2.2.0.2 — `GetList`, `GetByUniqueId` and `GetActiveProfileIdOnCharacter`
    all filter `IsTemporary`; `GetTemporaryProfileOnCharacter` does not exist). Brio holds the data
    internally but its public API (`Brio.API`) exposes only poses, not Customize+ data.
  - **Resolution**: read the clone's original scaling straight from the **MCDF file** the user applied.
    MCDF = LZ4-legacy compressed; after decompression: `"MCDF"` + version + int32 headerLen + header JSON,
    whose `CustomizePlusData` field is base64 of the C+ profile JSON (`{"Bones":{...}}`). See
    `BodyFlux/Io/McdfReader.cs`. Uses NuGet `K4os.Compression.LZ4` + `.Legacy` (1.3.8).
  - **UI**: "Load MCDF…" picker (`FileDialogManager`), pre-filled from Brio's `LastMCDFPath`
    (`BodyFlux/Io/BrioConfigReader.cs`). Origin priority: loaded MCDF → permanent C+ profile → identity.
  - **Reset**: when origin came from MCDF, restores via `SetTempProfile` (not `DeleteTempProfile`),
    fixing the vanilla-snap bug.
- **Multiple simultaneous Brio actor morphs** ✓ — several GPose actors morph independently and in
  parallel, each with its own origin (MCDF), destination, speed, mode and easing.
  - **Architecture**: a new `MorphSession` (`BodyFlux/Morph/MorphSession.cs`) bundles one morph's
    full state — its `MorphController`, captured origin, network target, speed override and sequence
    state. `Plugin.cs` keeps two pools: a single `_player` session (self or a targeted peer; owns
    Sync) and a `Dictionary<ushort, MorphSession> _brio` keyed by actor index. `MorphController` is
    reused unchanged. Player and Brio now run concurrently.
  - **Tick loop**: `OnFrameworkUpdate` ticks the player session plus every Brio session via a shared
    `TickSession` helper; only the player session broadcasts to peers. Player morphs use
    `GrowthSpeed`, Brio sessions `BrioGrowthSpeed` — keyed off the *pool*, not the target index,
    which also fixed targeted-peer morphs that had wrongly run at the Brio speed.
  - **Reset** split into `ResetGrowth()` (player), `ResetBrioActor(index)` and `ResetAllBrio()`
    (GPose exit). The sequence engine (`StartSequenceStep`/`AdvanceSequence`) now acts on a given
    session, so Brio sequences are per-actor too.
  - **UI**: the Brio **Single** tab (renamed from Morph) keeps the actor/MCDF/destination config and
    Apply, plus an "Active Morphs" list with per-row Pause/Resume/Reverse/Reset. A new **Multi** tab
    (last Brio sub-tab) lets you add several actors, give each its own full config, then **Apply All**
    with group controls (Pause/Resume/Reverse/Reset All). `StartBrioMorphFor(...)` is the
    explicit-config engine entry behind both single Apply and Apply All. The Player tab's morph
    sub-tab was also renamed **Single**. Multi-tab config is in-memory (not persisted).
