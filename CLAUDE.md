# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**AnvilAndHammerAI** (`src/AnvilAndHammerAI`, C# / net472, single DLL) — a single-player **Mount & Blade II: Bannerlord** battle-AI mod that implements coordinated "anvil and hammer" cavalry tactics (spatial targeting, flank/rear charges, timed heavy-cav release, formation-level morale) which vanilla and RBM lack. It is built against game **v1.3.15** and is a **declared hard dependency on RBM (Realistic Battle Mod) v4.2.20** — it layers on top of RBM rather than replacing it.

The `bannerlord-modding` skill applies to all work here — use it.

## Build / deploy

There are no unit tests (it is a battle-AI game mod; see **Verification**). Build = compile **and** auto-deploy.

```
& "C:\Users\rangt\.dotnet\dotnet.exe" build src\AnvilAndHammerAI\AnvilAndHammerAI.csproj -c Release -v minimal
```

- `dotnet` is not on PATH — use the full path above. `build.ps1` wraps this command, but Windows PowerShell 5.1 misreads its UTF-8-without-BOM Chinese characters, so prefer invoking dotnet directly.
- `src/Directory.Build.props` anchors every game/RBM/Harmony reference HintPath to the local install via `BannerlordGameDir` (default `D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord`). If the game lives elsewhere, override once: `-p:BannerlordGameDir="<path>"`.
- A successful build's post-build target copies the DLL + `_Module/SubModule.xml` into `<game>\Modules\AnvilAndHammerAI`, so **building is deploying**.

## Architecture (master doc: `docs/adr/0011-cavalry-command-scheduler-and-auto-formation.md`)

The mod is a **weight-setter, not a state machine.** It never calls `SetControlledByAI`; it re-asserts formation behavior weights every 0.5s to *soft-suppress* native/RBM tactic selection. Native/RBM behaviors keep running underneath — the mod just keeps the behavior it wants on top each tick. Internalize this before touching `Formations/`.

Three-stage pipeline, all in `Formations/`:

1. **AutoFormationMissionLogic** — reassigns every soldier into one of **7 role-formations**, each on a distinct `FormationClass` slot: 主步兵=`Infantry`, 包抄步兵=`HeavyInfantry`, 弓=`Ranged`, 骑射=`HorseArcher`, 左轻骑=`LightCavalry`, 右轻骑=`Cavalry`, 重骑=`HeavyCavalry` (mapping in `TroopClassifier`). It disables RBM's per-tick formation re-sort (`RbmResortPatch`) so the 7 formations hold.
2. **TacticalBrain** — pure function ("spatial brain"). Builds each enemy formation as an oriented entity, selects the weakest/most-open one as the schwerpunkt (突破口), computes open-arc flank points, and the gates: `BattleJoined` (anvil locked onto enemy infantry — the precondition for ALL flanking/rear moves), `ReleaseHammer` (target's shock pool near its rout threshold → release heavy cav), and per-formation cav-cover threats. Output = a `BattlePlan`.
3. **CommandSchedulerMissionLogic** — every 0.5s, for each role: `ResetBehaviorWeights` then `SetBehaviorWeight` per the plan. Also hosts the **threat-reaction override layer** (`ThreatReactionBehavior`: brace/shield-wall, fall-back, counter-charge when a formation is unexpectedly threatened) and the **player-command backoff**.

### Subsystems
- `Morale/` — **formation-level shock-pool morale**, the foundation. Pressure sources (casualty / cascade / encirclement / ranged / charge-shock, all `IMoralePressure`) integrate into a per-formation pool that ratchets toward rout. Both-sides, and the **only subsystem that ignores `ScopeFilter`**.
- `Detection/` — read-only sensors feeding brain + morale: `FormationScanner` (one fused snapshot per formation per tick), `RangedThreatSensor`, `ThreatAssessor`, `FormationGeometry`, and `FormationStrength` (tier-weighted count = the single "size/strength" metric used everywhere — never compare raw head counts).
- `Combat/` — Harmony patches for damage & speed (below).
- `Safety/BattleEndSafetyMissionLogic` — stops temporarily-routing player troops from triggering a premature defeat.
- `Settings/` (`AnvilSettings` MCM + `ScopeFilter`), `Logging/` (`Log` file + `Telemetry` counters), `Diagnostics/` (flushes telemetry every 5s).

### Damage & speed model (`Combat/`)
- **`DamageSystem`** — single Postfix on `AgentApplyDamageModel.CalculateDamage`; multiplies the **final** `__result` exactly once per hit (one direction/state factor × an optional charge factor). Hit direction is judged **formation-level** (victim formation's facing) for all attackers, with a per-agent fallback when the formation is missing/stale; flank/rear bonuses are stronger for the 包抄步兵/轻骑/重骑 roles.
- **`FormationSpeedPatch`** — per-role movement speed; reflection-patches `UpdateAgentStats` on `SandboxAgentStatCalculateModel` (campaign) + `CustomBattleAgentStatCalculateModel`, multiplying `MaxSpeedMultiplier` (foot) / `MountSpeed` (mount, via the rider's role).
- **`ChargePlowThroughPatch`** — forces knockdown on heavy-cav charge hits so they plow through; this is the moddable proxy for "lower charge deceleration" (the physical mount slowdown is native C++ and has no settable property).

## Hard conventions (violating these breaks RBM coexistence or crashes load)

- **Never reference the RBM or SandBox assemblies in the csproj.** Resolve their types at runtime via `AccessTools.TypeByName` and patch reflectively (see `RbmResortPatch`, `FormationSpeedPatch`); log-and-skip if absent. (RBM is a hard dependency in `SubModule.xml`, but code paths stay reflection-tolerant.)
- **Patch lazily, never at load.** Harmony patches install on the first field battle via `SubModule.EnsurePatched` (attribute patches through `PatchAll`; reflection patches applied by hand there). Patching at load triggers a `MovementOrder` static-cctor NRE that permanently poisons the type.
- **Shared-target patches layer on top of RBM, never replace it.** Use `[HarmonyAfter("com.rbmcombat" / "com.rbmai")]` + `[HarmonyPriority(Priority.Last)]` and only *multiply the final value* (e.g. `CalculateDamage`'s `__result`) — never prefix-override RBM's inner `ComputeBlowDamage` / `ComputeBlowMagnitudeFromHorseCharge`, and never recompute armor.
- **`SubModule.xml` load order** lists Harmony/ButterLib/MCM/RBM as `LoadBeforeThis` precisely so the post-patches above stack on RBM.
- **A formation's role is `Formation.FormationIndex`** (the assigned slot) — `Formation` has no `FormationClass` property (that name belongs to an unrelated network-message type). For a mounted attacker the role lives on the rider: `mount.RiderAgent.Formation.FormationIndex`.
- **`ScopeFilter.Applies(team/agent)`** gates everything except morale: with "only player army" enabled, only the player team is driven and enemies fall back to vanilla/RBM.
- **Player-facing MCM strings** use plain in-game vocabulary only (morale, formation, charge, rout, shield wall, tier) — no internal codenames, mechanism jargon, or telemetry tags. Keep MCM JSON property names stable so existing configs/saves still load; control group order with `GroupOrder`.

## Verification

No tests. Verify changes in order: (1) the build above (compile + deploy), (2) in-game — CustomBattle or a campaign field battle (every subsystem self-gates to `IsFieldBattle`), (3) the log at `Documents\Mount and Blade II Bannerlord\AnvilAndHammerAI.log`. Telemetry flushes every 5s as `[tele B/C/D/E/F/R]` plus `[sched]`/`[autoform]`/`[speed]` heartbeats; the guiding principle is **a counter at 0 means that subsystem did not fire this 5s window** — read the log to find which layer isn't working.

## Reverse-engineering sources (`tools/dump/`)

When you need an exact engine or RBM signature, **grep these instead of guessing** — they are ground truth. The decompiled engine file is enormous, so grep it, never read it whole.
- `_twdecomp/{MountAndBlade,SandBox,CampaignSystem}/*.decompiled.cs` — decompiled TaleWorlds assemblies.
- `rbm_src/{HorseChanges,DamageRework,AgentAi}.cs` and `_rbmsrc/*.cs` — RBM source.
- `apidoc/*.html`, `reflect_*.txt` — API docs and output of `tools/ReflectDump` (a net8 reflection dumper).

Do not commit or distribute decompiled TaleWorlds code; keep reverse-behavior work local and interoperability-oriented.
