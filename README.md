<div align="center">

<img src="https://raw.githubusercontent.com/miragiumxiv/BodyFlux/master/BodyFlux/images/icon.png" width="160" alt="BodyFlux icon" />

# BodyFlux

[![Latest release](https://img.shields.io/github/v/release/miragiumxiv/BodyFlux?label=release&color=blue)](https://github.com/miragiumxiv/BodyFlux/releases/latest)
[![Total downloads](https://img.shields.io/github/downloads/miragiumxiv/BodyFlux/total?label=downloads&color=green)](https://github.com/miragiumxiv/BodyFlux/releases)
[![License](https://img.shields.io/github/license/miragiumxiv/BodyFlux?label=license&color=orange)](https://github.com/miragiumxiv/BodyFlux/blob/master/LICENSE.md)

*A Dalamud plugin for FINAL FANTASY XIV that smoothly animates your character's body between Customize+ profiles — in the world, in GPose, and synced with your partners in real time.*

</div>

---

## About

BodyFlux turns the static body scaling of [Customize+](https://github.com/Aether-Tools/CustomizePlus) into a smooth, animated transition. Instead of snapping from one profile to another, BodyFlux interpolates every bone — translation, rotation and scale — from your current appearance toward a destination profile over a duration you control.

You can morph your own character anywhere in the world, drive multiple GPose actors at once through [Brio](https://github.com/Etheirys/Brio), chain several profiles into automatic A→B→C sequences, save your favourite configurations as presets, and trigger everything from chat commands or custom hotkeys. With Group Sync enabled, paired players see each other's morphs animate live, and can even morph each other's characters with consent.

## Features

### Profile Morphing
- Animates bone transforms (translation, rotation, scale) from your currently active Customize+ profile toward a chosen destination profile.
- All bones present in either profile are interpolated; bones absent from a profile default to game values.
- **Growth Speed** slider controls how fast the morph completes, with a live "time to complete" readout.
- A live search box on the destination selector filters your profile list as you type.
- Real-time progress bar and bone count; **Pause**, **Resume**, **Reverse** and **Reset** at any time.

### Morph Modes & Easing
- **Simple** — morph to the destination once.
- **Loop (Single)** — morph to the destination, then reverse back to the start once.
- **Loop (Infinite)** — continuously ping-pong between start and destination until Reset.
- Easing curves: **Linear**, **Ease In**, **Ease Out**, **Ease In-Out**.

### Presets
- Save up to **20** morph configurations (profile, speed, mode, easing) for instant recall.
- Apply, save and clear each slot from the UI, from chat, or via a bound hotkey.

### Sequences
- Chain several morphs into an automatic **A→B→C** sequence; each step starts from the previous step's result and has its own speed and easing.
- Add, remove, reorder and edit steps directly in the UI, with per-sequence playback controls and a reset button.

### GPose / Brio Support
- Morph any GPose actor (Brio clones included) — select an actor, pick a destination, and Apply.
- **Multiple actors** can morph simultaneously, each with its own controls in the Active Morphs list.
- **Origin scaling (MCDF):** load the MCDF that was applied to a clone so the morph starts from its real appearance and restores it on Reset.
- Dedicated Brio **Presets**, **Sequences** and a **Multi** tab to configure several actors (each with its own destination, MCDF, speed, mode and easing) and **Apply All** at once.
- All actors reset automatically when you exit GPose.

### Group Sync (Network)
- Generate a **Sync Key** (`XXXX-XXXX`) and share it with your partner(s) to sync together.
- While synced, partners see your morph animate on your character in real time; Reset restores your original appearance on their screen too.
- Optional **"Allow others to morph my character"** consent toggle lets paired players apply their own profiles to your character.
- A **Connected users** list shows everyone in the same Sync Key session.
- Privacy-first: character names never reach the relay (client-side hashing), and lone connections auto-disconnect to save bandwidth.

### Keybinds
- Bind hotkeys for **Apply Single**, **Apply Multi**, **Pause**, **Resume**, **Reset** and **Reverse**.
- Bind individual preset slots to keys.
- Bindings are context-sensitive: outside GPose they drive your own morph, in GPose they drive all Brio actors. Keys are ignored while typing in a text field.

## Requirements

| Plugin | Required? |
| --- | --- |
| [**Customize+**](https://github.com/Aether-Tools/CustomizePlus) | **Required** — a Customize+ profile must be active on your character before morphing. |
| [**Brio**](https://github.com/Etheirys/Brio) | Optional — only needed for morphing GPose actors / clones. |

## Chat Commands

| Command | Description |
| --- | --- |
| `/bodyflux` | Open / close the control window. |
| `/bodyflux preset <1-20> [speed]` | Apply a preset slot, optionally overriding the speed (`0.01`–`1.0`). |
| `/bodyflux sequence <name> [speed]` | Play a sequence by name, optionally overriding the speed. |
| `/bodyflux pause` | Pause the active morph. |
| `/bodyflux resume` | Resume a paused morph. |
| `/bodyflux reverse` | Reverse the active morph. |
| `/bodyflux reset` | Reset the active morph and restore the original profile. |

## Installation

Add the custom plugin repository in Dalamud (`/xlsettings` → **Experimental** → **Custom Plugin Repositories**) using the URL below, then install **BodyFlux** from the plugin installer:

```
https://raw.githubusercontent.com/miragiumxiv/BodyFlux/master/pluginmaster.json
```

## Credits

Plugin icon designed by **BubblySugars** — [@BubblySugars](https://x.com/BubblySugars).
</content>
</invoke>
