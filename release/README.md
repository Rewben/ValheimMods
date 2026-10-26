# ValheimRAFT Rewben

ValheimRAFT with native ship-portal synchronization fixes and integrated
RaftRebalance behavior. Based on upstream ValheimRAFT 5.1.1.

**Thunderstore package and runtime fork: 5.1.105.** Audits identify saved portal
copies at the same ship-relative position and print a short confirmed-cleanup
command. Cleanup runs only on explicit admin request, in small networked batches.
Update both the server and every client to 5.1.105 before connecting.

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
vehicle portals repair <audit-id> --dedupe --confirm
vehicle portals repair <audit-id> --remove <record-id>,<record-id> --confirm
```

An audit prints its total and at most ten saved portal records. The final line
repeats `AUDIT ID: A1B2C3` with the total portal count. IDs are six characters,
accept uppercase or lowercase, and expire after five minutes. They belong to the
requesting admin's current connection; reconnecting requires a new audit. Complete
reports are saved on the server under `BepInEx/config/PortalAudits/<id>.txt`.
The client prints only a small loaded-instance counter, not another full report.
Periodic diagnostics also print counters without formatting all record details.

Reports include saved records, live instances, positions, ship IDs and registry
anomalies, plus `duplicateGroups` and `duplicateRecords`. The full file records
every saved ID, its unrounded local position, and the ID selected to survive.

**Audit is read-only.** To remove its detected extra saved records, run the printed
`repair <id> --dedupe --confirm` command. It matches the same ship, portal prefab,
coordinate frame and local 3D position within 1 mm. Missing/invalid local positions
are skipped. It keeps one named portal where available, then chooses a stable ID.
World-position rounding and ship-center coordinates are not used for selection.

Cleanup yields between batches of at most 32 and waits for Valheim to acknowledge
deletions. Keep the admin connected until `CLEANUP <id> COMPLETE` appears. A changed
record, moved keeper, lost admin session or world change stops further work; run a
fresh audit before continuing. No automatic position-based deletion runs during
normal play. Basic repair without deletion flags still preserves saved records.
Repairs provide no resource refunds. TargetPortal's temporary pins are cleared
after repair; close and reopen the portal map to refresh it.

## Status and source

Runtime 5.1.102 previously completed dedicated-server startup and configuration
migration. Runtime 5.1.104 completed dedicated-server startup. Runtime 5.1.105
passes headless regression checks, including a 16,248-record confirmed cleanup
with asynchronous deletion acknowledgements. Dedicated-server startup and
multiplayer validation of 5.1.105 remain pending. Tests use inspected game contracts
and do not simulate Unity networking or establish that all reported disconnects
are resolved.

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
