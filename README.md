# Anvil & Hammer — Cavalry & Morale Battle AI

> **Language:** English · [简体中文](README.zh-CN.md)

![Game](https://img.shields.io/badge/Bannerlord-v1.3.15-blue)
![Requires RBM](https://img.shields.io/badge/requires-RBM%20v4.2.20-red)
![Type](https://img.shields.io/badge/Singleplayer-Field%20Battles-green)

A single-player **Mount & Blade II: Bannerlord** battle-AI mod that gives armies two things both vanilla and RBM lack: **coordinated anvil-and-hammer cavalry tactics**, and **formation-level morale** where whole units break together.

It is built to run **on top of [RBM (Realistic Battle Mod)](https://www.nexusmods.com/mountandblade2bannerlord/mods/2210)**, not to replace it — RBM's combat and AI keep running underneath, and this mod layers its cavalry coordination and morale on top.

---

## Features

- **Coordinated cavalry, not a blind charge.** The infantry *anvil* pins the enemy line; your cavalry *hammer* is held back until the moment is right, then committed into the flank and rear of the enemy formation that is closest to breaking.
- **It chooses where to strike.** The mod reads the whole enemy line, finds the weakest or most exposed formation as the breakthrough point, and aims the encirclement there — instead of everyone charging the nearest blob.
- **Seven roles, one plan.** Every soldier is sorted into one of seven coordinated formations — main infantry, flanking infantry, archers, horse archers, left and right light cavalry, and heavy cavalry — each with its own job.
- **Focused volleys.** Each archer and horse-archer formation concentrates its fire on a *single* enemy formation — the breakthrough point — instead of every archer picking its own target, so arrows actually stack up where it matters.
- **Formation-level morale.** Units don't just bleed men one at a time; whole formations accumulate shock and **rout together** under sustained pressure — taking casualties, being surrounded, watching neighbours flee, standing under arrow fire, or facing a cavalry charge. Higher-tier troops hold out longer.
- **Rout in good order.** A broken formation falls back *together*, back to the enemy (and takes extra damage for turning tail), rather than instantly scattering — and can rally and rejoin the fight once the pressure lifts. Only a unit that has broken one time too many collapses for good and scatters.
- **Formations react to threats.** Caught off guard, a unit will brace into a shield wall, fall back, or counter-charge instead of standing still and dying.
- **Direction matters.** Hits to the flank and rear deal more damage, each role moves at its own tuned speed, ranged weapons hit a little softer, and heavy cavalry plows through enemies on the charge.
- **Morale at a glance.** Hold **Alt** and each formation's marker icon fills with its team colour in proportion to its remaining morale; the spent portion greys out, and a **routing** formation's icon **pulses** so a collapse is obvious at a glance. Flanking-infantry and heavy-cavalry icons are tinted **gold** to mark your hammer. An optional setting keeps the markers on screen permanently, no Alt required.
- **See your arrows fly.** Every arrow, bolt and javelin leaves a light-grey trail along its flight path, kept clearly visible from the zoomed-out / RTS-camera overview — where the vanilla trail vanishes. Shown in the free/RTS camera and hidden while you're controlling your own character.
- **Both sides — or just yours.** By default the enemy AI fights with the same brain, so battles stay symmetric. A setting can restrict the cavalry/command layer to your army only. (Morale always applies to both sides.)
- **Tune everything** from the in-game Mod Options (MCM) menu.

---

## Requirements

Install these first — all are hard dependencies:

| Dependency | Notes |
|---|---|
| **Mount & Blade II: Bannerlord** | built and tested against **v1.3.15** |
| **RBM — Realistic Battle Mod** | **v4.2.20** — hard dependency, this mod layers on top of it |
| **Harmony** (`Bannerlord.Harmony`) | runtime patching |
| **ButterLib** | shared utility library |
| **UIExtenderEx** | drives the morale display on formation markers |
| **MCM — Mod Configuration Menu** (`Bannerlord.MBOptionScreen`) | in-game settings |

### Load order

Dependencies must load **before** this mod so its patches stack on top of RBM:

```
Harmony → ButterLib → UIExtenderEx → MCM → RBM → Anvil & Hammer
```

---

## Installation

1. Install every dependency listed above.
2. Place the `AnvilAndHammerAI` folder into:
   `…\Mount & Blade II Bannerlord\Modules\`
3. Launch the game, open the launcher's **Mods** tab, enable **Anvil & Hammer - Cavalry & Morale Battle AI**, and confirm the load order above.
4. Start a battle. The mod self-activates on **single-player field battles** (Custom Battle and campaign open-field engagements).

---

## Compatibility & Saves

- **RBM is required.** This mod's patches are deliberately post-patches that multiply RBM's final values; without RBM it will not load correctly.
- **Save-safe.** The mod adds no custom save data — it only runs during battles. You can add or remove it mid-campaign without corrupting a save.
- **Scope.** Single-player field battles only. Sieges and other mission types are not targeted; the mod stays out of them.

---

## Configuration (MCM)

Open **Options → Mod Options → Anvil & Hammer** in-game. Settings are grouped:

- **General** — master on/off, and scope (drive the *whole battle* vs. *your army only*).
- **Battle Display** — show morale on the formation-marker icons; keep the markers always visible without holding Alt; show a trail on every arrow in flight.
- **Rout & Rally / Fracture on Repeated Routs** — how long a routed unit takes to rally and the morale it recovers to, and how each rout makes a unit more brittle until it breaks for good.
- **Morale** — rout threshold, how fast accumulated pressure fades, how strongly being under fire shakes morale, and which pressure sources are active (ranged fire, cavalry-charge shock).
- **Damage & Speed** — directional (flank/rear) damage multipliers, a ranged-weapon damage multiplier, cavalry-charge impact, and per-role movement-speed multipliers.

Settings apply live; no restart needed for most options (UI/localization changes need a game restart).

---

## How it works

> Full architecture & design doc: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — the code-grounded source of truth for how the mod works.

The mod is a **weight-setter, not a state machine.** It never seizes formations with `SetControlledByAI`; instead, every 0.5 s it re-asserts the formation behaviour weights it wants and **blocks native/RBM from overriding them** on the formations it actually drives (formations it doesn't drive — those you command yourself, or anything outside its scope — are left untouched). RBM keeps running underneath — this mod just keeps the behaviour it chose on top each tick.

Three-stage pipeline:

1. **Auto-formation** — every soldier is reassigned into one of the seven role-formations.
2. **Tactical brain** — a pure spatial pass that builds each enemy formation as an oriented shape, selects the breakthrough point, computes flank/rear approach points, and decides the gates: *anvil engaged?*, *release the hammer?*, *cover threats?* — producing a battle plan.
3. **Command scheduler** — every 0.5 s it re-applies behaviour weights per the plan, plus a threat-reaction layer (brace / fall back / counter-charge) and a back-off when the player issues manual orders.

Morale is a separate, foundational subsystem: each formation has a *shock pool* that integrates pressure from casualties, encirclement, neighbouring routs, ranged fire, and cavalry charges; when it crosses a tier-scaled threshold, the whole formation routs.

---

## Building from source

Requires the .NET SDK and a local Bannerlord install with the dependencies present.

```bash
dotnet build src/AnvilAndHammerAI/AnvilAndHammerAI.csproj -c Release -v minimal
```

- All game/RBM/Harmony references are anchored via `BannerlordGameDir` in [`src/Directory.Build.props`](src/Directory.Build.props) (default `D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord`). If your game is elsewhere, override it once:
  ```bash
  dotnet build … -p:BannerlordGameDir="<your game path>"
  ```
- A successful build's post-build step copies the DLL and `_Module/SubModule.xml` straight into `…\Modules\AnvilAndHammerAI`, so **building is deploying.**

There are no unit tests — this is a battle-AI mod. Verify changes by building, then in a Custom Battle or campaign field battle, and by reading the log at
`Documents\Mount and Blade II Bannerlord\AnvilAndHammerAI.log`.

---

## Credits

- **RBM — Realistic Battle Mod** team, whose combat/AI overhaul this mod builds on.
- **BUTR** ([Bannerlord Unofficial Tools & Resources](https://github.com/BUTR)) for Harmony, ButterLib, UIExtenderEx, and MCM.
- Tactical inspiration from the historical **anvil-and-hammer** doctrine (Alexander's Companions, Hannibal at Cannae, the Parthians at Carrhae).

---

## License

Released under the [MIT License](LICENSE).

This mod is a fan-made, non-commercial work and is **not affiliated with or endorsed by TaleWorlds Entertainment**. *Mount & Blade II: Bannerlord* is a trademark of TaleWorlds Entertainment. No game assets or decompiled engine code are distributed in this repository.
