# Rewben ValheimRAFT: package and runtime 5.1.106

Maintained at https://github.com/Rewben/ValheimMods on `rewben/main`, based on
`ValheimRAFT-v5.1.1`. This fork integrates portal synchronization corrections and
RaftRebalance behavior. It retains upstream plugin GUIDs, assembly names, prefab
names, and saved-world keys. All clients and the server need the same fork version.

5.1.106 rebuilds TargetPortal's cached set after confirmed cleanup, handling the
mutable network IDs/hash codes of pooled ZDOs before clearing temporary pins.
Matching 5.1.106 is required on clients and server; startup does not delete portals.

5.1.105 adds manual position-based saved-record cleanup. Audit matches ship,
prefab, coordinate frame and local 3D position within 1 mm, records the selected
keepers/removals and prints a `--dedupe --confirm` command. Confirmed cleanup uses
batches of at most 32 and waits for game deletion acknowledgements. Registry
rebuilds are linear; TargetPortal temporary map pins are refreshed afterward.
No automatic position-based deletion runs during play. Matching runtime 5.1.105
is required on server and clients. Dedicated-server/gameplay validation is pending.

5.1.104 caps audit previews at ten records and prints the total and a six-character
case-insensitive audit ID at the beginning and end. The client prints only loaded
instance counters; complete records stay in the server's audit file. Periodic
diagnostics skip per-record formatting. All clients and the server need 5.1.104.
Dedicated-server and gameplay validation for this release remain pending.

Package 5.1.103 was a packaging/privacy correction. It kept runtime version 5.1.102,
places assemblies and Assets under an explicit plugins directory for Gale/r2modman,
and uses virtual compiler source paths without debug symbols. The staged unsigned
ServerSync dependency has only its CodeView PDB filename sanitized. No changes to
portal/balance behavior or network compatibility are included. The public ZIP uses
the user-facing files in `release/`; this developer guide is not bundled.

5.1.102 corrects the deployment health check that prematurely stopped the first
5.1.101 maintenance attempt: the container's informational log-filter declaration
was mistaken for a plugin failure. Full server/profile rollback was verified.
The portal and rebalance implementation is unchanged; gameplay acceptance remains
pending. Deployment evidence is now returned over the same SSH connection.

## Build and package (Windows PowerShell 7)

Install .NET SDK 8.0.425 or a newer 8.0 patch. Supply your own legally installed
Valheim managed assemblies and BepInEx core. No Unity editor, private `libs`
submodule, game launch, or plugin installation is needed.

```powershell
git clone --filter=blob:none --branch rewben/main https://github.com/Rewben/ValheimMods.git
Set-Location ValheimMods
git remote add upstream https://github.com/zolantris/ValheimMods.git
.\tools\Build-Fork.ps1 -ManagedPath 'E:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed' -ProfileBepInEx 'C:\path\to\profile\BepInEx'
.\tools\Package-Fork.ps1 -ManagedPath 'E:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed' -ProfileBepInEx 'C:\path\to\profile\BepInEx' -Python 'C:\path\to\python.exe'
```

Public reference dependencies and hashes are pinned in `tools/reference-lock.json`:
Jotunn 2.30.2, PlanBuild 0.20.0, PlantEasily 2.3.0, Gizmo 1.16.0. Cached references
live in ignored `.references/`. A matching installed reference can be reused;
otherwise the build downloads the pinned public package. Game/reference DLLs are
never added to Git or the ZIP. `Package-Fork.ps1` requires committed source, runs
the headless regression suite, builds, and writes the ZIP, SHA256, and metadata
under `dist/`. Startup logs and metadata identify the source commit and upstream
base. The ZIP DLL allowlist contains only RAFT's six runtime assemblies under
`plugins/`. Packaging validates the Assets hierarchy, CRCs, metadata hashes, and
private-path/credential patterns. Optional `-PrivacyValuesFile` supplies a local
JSON array of additional private strings to reject; it is never bundled. The
archive audit report and checksum stay beside the finished ZIP.

Python 3 is needed for the deployment fixture checks during packaging.
CI runs headless production-code contract tests with inspected game API doubles,
Harmony instruction checks, and deployment/rollback fixture tests. These checks
do not simulate Unity physics or establish multiplayer/gameplay acceptance.
Release binaries are built locally with game references. Legacy upstream build
workflows are archived under `.github/upstream-workflows/`; they are not active
fork release workflows.

## Installation and configuration migration

Quit Valheim and Gale. Back up the whole Gale profile, including Gale's profile metadata.
Remove upstream ValheimRAFT and RaftRebalance from that profile, then import the
local ZIP in Gale. Keep Jotunn and other unrelated mods. Disable upstream RAFT
updates for this profile; install future fork releases explicitly. Retain the
legacy `vhrf.RaftRebalance.cfg` for the first startup. Never install both
implementations. Startup rejects a companion DLL even if its file was renamed.

The first fork startup backs up `zolantris.ValheimRAFT.cfg` to `.before-rewben`
and migrates before other RAFT settings bind. Existing integrated settings win;
otherwise legacy companion overrides win, followed by existing RAFT settings,
then fork defaults. `[Rebalance Migration] Schema = 1` prevents stale legacy
files from overriding later edits. Unrelated configuration stays in the same
file. Migration completion is logged. The new settings use Jotunn's synchronized,
admin-only configuration tags. Migrated recipe controls live in `[Rebalance Recipes]`,
separate from the original `[RecipeConfig]` fallback values.

`[Rebalance]` contains `MaxHullClearance` (1.5 m), `BlockCannonHarvesting`,
`EnforceRaftLocks`, `LegacyRefunds`, `HaulStaminaPerMeter` (10 by default, retaining
an existing RAFT value), and optional `PortalDiagnostics`. Recipes retain their
prefab names under `[Rebalance Recipes]`. Locks disable flight, ballast, terrain ramming, land
vehicles, and public administrative commands. Ordinary creative editing remains
available, with non-admin lift clamped to 0–5 m; an existing lift of 0 stays 0.
Hull rebuilds and per-ship edits reapply the clearance cap. Rams hit creatures;
cannons retain damage to creatures, spawners, and player buildings while blocking
harvesting. Hauling initializes at startup/synchronization and stops at exhaustion.

All ten supplied recipe overrides are retained. The turret override remains
available for its prefab; upstream 5.1.1 keeps turret registration behind DEBUG,
so this fork does not expose that unfinished feature. Eight repriced build pieces
record their refundable costs in `vhrf_paid_v1`. Existing markers are honored;
unmarked legacy pieces use the companion's original fallback costs when enabled.
Recipe changes cannot raise a marked piece's refund.

## Portal behavior and admin tools

Position and rotation updates preserve portal ZDO IDs. Portals belong only in
their dedicated portal-sector registry, once each. Tracking is unique by valid
ID and pruned; resets clear stale registry references before ZDO pooling.
The scene guard only reuses an existing instance of the same ship-portal record.
Different overlapping portals keep their separate IDs. Server work uses seconds,
honors the configured interval, and skips unloaded vehicles without ending its
worker. When TargetPortal manages a portal, vanilla pairing is bypassed. The
pairing hook checks the current game's `GetPortalList` call and fails visibly if
that contract changes.

Admin F5 console commands:

```text
vehicle portals audit [ship-id]
vehicle portals repair <audit-id>
vehicle portals repair <audit-id> --dedupe --confirm
vehicle portals repair <audit-id> --remove <ZDO-id>,<ZDO-id> --confirm
```

Audits list record IDs, instance IDs/positions, ship IDs, and registry anomalies.
The console returns at most ten records, with the total and six-character audit ID
at both ends. IDs accept either letter case. Complete server reports are saved under
`BepInEx/config/PortalAudits/`. Audits belong to the requesting server admin and
expire after five minutes or a world change. Repair without deletion flags corrects
registrations and removes extra live instances sharing one record. It does not
delete distinct saved records. Saved deletion requires explicit IDs or the audited
position-duplicate selection via `--dedupe`, plus `--confirm`. Matching uses ship,
prefab, coordinate frame and unrounded local coordinates within 1 mm; missing local
positions are skipped. No background position deletion runs. The full audit maps
each selected extra ID to the keeper. All selections are validated before mutation
and again per batch; changed records or admin/world changes stop further batches.
The job yields between batches of at most 32 and waits for network acknowledgement.
Repair creates no resource refunds. Keep the canonical instance and its record.

## Main-server maintenance and full rollback

Prepare and verify a complete staged BepInEx inventory before touching the active
installation. Use `tools/Push-Fork.ps1` with `-BepInExPath` pointing at that stage
and `-ForkMetadataPath` pointing at the ZIP metadata. It uses one SSH password
prompt, never captures a password, preserves existing server configuration, and
mirrors both persistent and runtime plugin/patcher trees. The helper runs on the
Linux host, not Windows Docker Desktop.
Pass `-ResultPath 'C:\path\to\deployment-result.json'` to save startup logs,
migration settings, backup checksum, and persistent/runtime DLL hashes locally
over that same connection, on either startup success or failure. SSH password
entry remains attached to its normal console and is not captured.

Log everyone out before maintenance. Stop gracefully with the existing 120-second
timeout. The tool backs up the entire stopped `/config`, including world chunk
directories, verifies gzip readability and checksum, and retains prior runtime
trees with checksum inventories. It then swaps prepared trees. Automatic vanilla
updates remain enabled. Startup checks fork version/commit, migration completion,
companion absence, plugin-loading errors, game-version changes, and connection.
Readiness is only the start of the gameplay validation below.

If validation fails, quit clients and use **full** rollback (Windows PowerShell):

```powershell
.\tools\Push-Fork.ps1 -Server '<SSH-host>' -Container '<container>' -ServerRoot '/srv/games/valheim/<server>' -RollbackDeployment '<deployment-id>' -FullRollback
```

This stops the server and restores the saved world/chunks, configuration,
persistent mods, and runtime mods together. Test-session activity is discarded.
Restore the matching local profile backup and Gale metadata with Gale closed.
The ordinary `-RollbackDeployment` option restores only mods/configuration and
is insufficient for this maintenance test. Backups are retained without automatic
deletion. If restoration itself fails, the helper leaves the server stopped.

## Required gameplay acceptance

Use one admin and one non-admin client on the main server. Record baseline and
final audits. Check ship rotation/movement across sector boundaries, ten area
unload/reload cycles, reconnects, portal travel both ways, bed respawning, and
three save/reload cycles. Run 30 minutes with `PortalDiagnostics = true` and check
stable record/live-instance counts for the same loaded ships, bounded tracking,
and no new invalid `0:0` pairings. Check creative/admin permissions, clearance,
hauling/exhaustion, creature-only rams, cannon targets, all available recipes,
and old/new marked refunds. Verify ships, inventories, and unrelated settings.
Do not infer duplicate saved portals from overlap or delete them automatically.

See `VALIDATION.md` for recorded evidence and outstanding checks. A release
candidate is not gameplay-certified until those observations are recorded.

## Future upstream updates

Fetch `upstream`, create `rewben/update-<release>` from `rewben/main`, and merge a
selected upstream release tag. Review overlapping portal/compatibility fixes and
keep the fork identity/version policy. Recheck game API/Harmony contracts, run
headless tests, build locally, and repeat maintenance/gameplay checks. Increment
the fork revision and version, commit, tag `rewben-v<version>`, and publish the
ZIP/checksums/source metadata. Merge the reviewed update branch to `rewben/main`.
Never use Gale's upstream RAFT update button to update this fork.

## Attribution

ValheimRAFT/ValheimMods by zolantris and contributors, with earlier ValheimRAFT
contributors retained in upstream documentation. Upstream GPL-3.0 licensing is
retained in `LICENSE`; ServerSync's license is included separately. Rewben fork
changes use the same project license. RaftRebalance 1.2.0 (`vhrf`, supplied by the
user) contributed the balance policy, recipes, migration keys, and paid-cost
compatibility requirements. This is a typed implementation within RAFT; the
companion binary and its Harmony patches against RAFT are not redistributed.

The optional `Prepare-Fork.ps1` / `Install-ForkClient.ps1` tools prepare a verified
profile snapshot and staged inventory, then install the staged plugin tree with
Gale closed. The installer updates only that profile's mod list, replacing upstream
RAFT and RaftRebalance with a local fork entry. It does not export Gale auth data.
`Restore-ForkProfile.ps1` restores the profile ZIP and its exact metadata row with
Gale closed; schema/identity changes are rejected. Other profiles are preserved.
