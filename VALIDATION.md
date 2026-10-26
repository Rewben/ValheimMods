# Fork validation record

Candidate: 5.1.101. Base: ValheimRAFT-v5.1.1. Reference game: local Valheim 1.0.20.

## Local checks

- Full Release source build: passed with .NET SDK 8.0.427 and pinned references.
- Production portal registry/policy/Harmony contract regression suite: 48 passed.
- Deployment/configuration/backup/full chunked-world rollback and Gale metadata fixtures: 25 passed.
- Game/reference DLLs excluded from Git and release DLL allowlist.
- GitHub Actions: [48 C# and 25 Python checks passed](https://github.com/Rewben/ValheimMods/actions/runs/40265698433) on the first published implementation commit.

These tests use game API doubles and filesystem fixtures. They do not execute
Unity, connect real clients, or establish that the reported crash is resolved.

## Main-server acceptance (not yet performed)

- Maintenance logout and stopped-server/profile backups: pending.
- Installation, migration, plugin health and matching clients: pending.
- Admin/non-admin behavior and all rebalance checks: pending.
- Sector movement/rotation, ten unload/reloads, reconnects and portal travel: pending.
- Bed respawn and three save/reload cycles: pending.
- Thirty-minute diagnostic session with stable IDs/instances and no invalid pairing: pending.
- Saved-record deletion: none authorized by an audit selection; none performed.

Add deployment ID, backup checksums, startup log, baseline/final audits, timings,
client observations, and any rollback here when they exist. Do not mark checks
passed based solely on compilation or a server-connected message.
