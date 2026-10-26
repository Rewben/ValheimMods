# ValheimRAFT Rewben

ValheimRAFT with native ship-portal synchronization fixes and integrated
RaftRebalance behavior. Based on upstream ValheimRAFT 5.1.1.

**Thunderstore package 5.1.103 contains runtime fork 5.1.102.** This release fixes
mod-manager asset placement and removes machine-specific build/debug paths.
Gameplay behavior and network compatibility remain at runtime 5.1.102. It works
with a server already running the Rewben 5.1.102 runtime.

## Installation

1. Close Valheim and back up your profile and world before replacing mods.
2. Remove upstream ValheimRAFT and the separate RaftRebalance plugin. When moving
   from a local ZIP installation to Thunderstore, remove that local fork copy too.
3. Install **rewbensandwich-ValheimRAFT_Rewben** through Gale or another compatible
   mod manager. Keep Jotunn installed. Manual installations place the contents of
   this ZIP's `plugins` directory in a dedicated BepInEx plugins subdirectory.
4. All players and the server must use the same runtime fork version. Keep the
   existing RAFT configuration and legacy `vhrf.RaftRebalance.cfg` for migration.

The first fork startup migrates settings once, preserving unrelated settings.
Rebalance controls are under `[Rebalance]` and `[Rebalance Recipes]` in RAFT's
configuration. `[Rebalance Migration] Schema = 1` records completion. Remove
RaftRebalance's DLL; keeping its old configuration file is safe after migration.

## Changes

- Ship portals keep their network IDs while their position and rotation update.
  Registry membership and tracking are deduplicated; duplicate live instances of
  the same record are guarded. TargetPortal-managed portals bypass vanilla pairing.
- Native rebalance policy caps hull clearance at 1.5 meters by default, disables
  flight/ballast under the policy, restricts administrative commands, and allows
  ordinary creative editing with at most 5 meters of lift.
- Rams target creatures. Cannon harvesting restrictions preserve damage to
  creatures, spawners and player buildings. Hauling uses 10 stamina per meter by
  default and stops at exhaustion.
- Supplied recipe overrides and `vhrf_paid_v1` refund markers are supported.

## Portal auditing and repair

Server-admin console commands:

```text
vehicle portals audit [ship-id]
vehicle portals repair <audit-id>
vehicle portals repair <audit-id> --remove <record-id>,<record-id> --confirm
```

An audit reports saved records, live instances, positions, ship IDs and registry
anomalies. Audits expire after five minutes. Repair without explicit removal IDs
does not delete distinct saved portal records. Position overlap alone is never
treated as permission to delete a portal. Repairs provide no resource refunds.

## Status and source

Runtime 5.1.102 completed dedicated-server startup and configuration migration.
Headless regression checks pass. Multiplayer gameplay/stability acceptance is
still pending; this is not a claim that every reported crash is resolved.

Source, maintenance, full snapshot rollback and upstream update instructions:
[Rewben/ValheimMods](https://github.com/Rewben/ValheimMods/tree/rewben/main).

## Licensing and attribution

ValheimRAFT/ValheimMods is by zolantris and contributors, retaining earlier
ValheimRAFT contributions by Sarcen and others. This fork retains GPL-3.0 licensing
and attribution. See `LICENSE`. RaftRebalance 1.2.0 (`vhrf`) contributed the balance
policy, recipes, migration keys and paid-cost compatibility requirements.
ServerSync v1.20 is bundled under its included license; only its unsigned PE debug
filename has been sanitized. See `ServerSync.PROVENANCE.md`.

The archive contains runtime mod assemblies, mod assets and public documentation.
It does not include player saves, profile backups, server configs, logs, private
deployment scripts, credentials, game reference assemblies or debug-symbol files.
