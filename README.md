# BodyFlux

A Dalamud plugin for FINAL FANTASY XIV that smoothly animates your character's body between two Customize+ profiles, with real-time sync to paired partners.

## Features

### Profile Morphing
- Animates bone transforms (translation, rotation, scale) from your currently active Customize+ profile toward a chosen destination profile.
- All bones present in either profile are interpolated; bones absent from a profile default to game values.
- Adjustable **Growth Speed** controls how fast the morph completes.
- Progress bar shows morph completion in real time.
- **Reset** restores your character to the original profile at any time.

### Destination Template Search
- The destination profile selector includes a live search box — type any part of a profile name to filter the list instantly.

### Peer Sync (Network)
- Connect with a partner over the internet using a shared **Pair Key**.
- Use the **Generate** button to create a random key in `XXXX-XXXX` format, then share it with your partner.
- Both players must have BodyFlux installed and enter the same Pair Key.
- While synced, your partner sees your morph animating on your character in real time via Customize+ IPC.
- Pressing **Reset** restores your character's original appearance on your partner's screen as well.
- A **Connected users** list shows everyone currently in the same Pair Key session.

## Requirements

- [Customize+](https://github.com/XIV-Tools/CustomizePlus) must be installed and active.
- A Customize+ profile must be set as active on your character before starting a morph.

## Usage

Use `/bodyflux` in chat to open the control window.
