# Fork validation record

Candidate: 5.1.102. Base: ValheimRAFT-v5.1.1. Reference game: local Valheim 1.0.20.

## Local checks

- Full Release source build: passed with .NET SDK 8.0.427 and pinned references.
- Production portal registry/policy/Harmony contract regression suite: 48 passed.
- Deployment/configuration/backup/full chunked-world rollback and Gale metadata fixtures: 30 passed after the health-check correction.
- Binary transport and structured report capture: verified locally with a 1 MiB payload on success and failure, without SSH.
- Game/reference DLLs excluded from Git and release DLL allowlist.
- GitHub Actions: [48 C# and 25 Python checks passed](https://github.com/Rewben/ValheimMods/actions/runs/40265698433) on the first published implementation commit.

These tests use game API doubles and filesystem fixtures. They do not execute
Unity, connect real clients, or establish that the reported crash is resolved.

## Main-server attempt: October 25, 2026

The 5.1.101 candidate was installed during the authorized maintenance session.
The stopped-server backup and persistent/runtime mod swap completed. Startup
failed the deployment health check; the helper stopped the server and the client
wrapper restored the previous Gale profile and metadata. Full server recovery
subsequently restored the world/chunks, configuration, persistent mods, and runtime
mods; the previous server connected. The public 5.1.101 candidate is withdrawn.
Main-server acceptance has not passed.

Captured evidence identified a false alarm in the deployment checker: it matched
`could not load library` inside the Go log wrapper's informational declaration of
a filter rule. There was no captured plugin exception. RAFT 5.1.101 and migration
schema 1 had loaded, but startup was interrupted before server connection. This
does not establish gameplay correctness. 5.1.102 excludes only that specific
informational banner, still rejects actual errors, checks the game version without
case sensitivity, and returns startup evidence through the existing SSH session.

The first failure message omitted the matched log line. The deployment helper now
includes that line (with sensitive-value suppression) so a future startup failure
is actionable directly from the command output.

## Outstanding main-server acceptance

- Maintenance logout and stopped-server/profile backups: completed for the first attempt; repeat for 5.1.102.
- Installation, migration, plugin health and matching clients: pending.
- Admin/non-admin behavior and all rebalance checks: pending.
- Sector movement/rotation, ten unload/reloads, reconnects and portal travel: pending.
- Bed respawn and three save/reload cycles: pending.
- Thirty-minute diagnostic session with stable IDs/instances and no invalid pairing: pending.
- Saved-record deletion: none authorized by an audit selection; none performed.

Add deployment ID, backup checksums, startup log, baseline/final audits, timings,
client observations, and any rollback here when they exist. Do not mark checks
passed based solely on compilation or a server-connected message.
