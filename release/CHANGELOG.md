# Changelog

## Package and runtime 5.1.105

- Audit position-based saved-portal duplicates using ship, prefab, coordinate
  frame and unrounded local 3D position with a 1 mm tolerance. Keep one portal
  per group; missing or invalid local positions are excluded from cleanup.
- Print duplicate counts and the exact short-ID `--dedupe --confirm` command.
  Audit stays read-only; complete keep/remove selections remain in the report.
- Delete only after explicit admin confirmation, in batches of at most 32 with
  network deletion acknowledgements. Stop on changed records, keepers, sessions
  or worlds. Append repair selections/results to the audit file without refunds.
- Rebuild portal registries in one pass and detach deletion batches efficiently.
  Replace the large repair-ID broadcast with a compact registry refresh signal.
- Clear TargetPortal's temporary map pins and prune stale cached records after
  repair. Unrelated saved map pins are unaffected.
- Add production-code tests for large cleanup, position boundaries, invalid
  coordinates, different ships/types, confirmation, cancellation and map refresh.
- Server and clients must all update to runtime 5.1.105.

## Package and runtime 5.1.104

- Limit the server audit preview to ten portal records and show the total count
  and audit ID prominently at both the start and end of the response.
- Replace the client's unbounded full-record dump with a small loaded-instance
  counter. Preserve complete server reports in `PortalAudits/<id>.txt`.
- Use six-character, case-insensitive audit IDs with collision checks against
  active audits and saved report filenames. Keep admin/session ownership and
  five-minute expiry checks.
- Periodic portal diagnostics no longer format a full per-record report only to
  discard it and print the first line.
- Add regression coverage using the production audit commands with 16,248 saved
  records. Existing saved portals and deletion rules remain unchanged.
- Server and clients must all update to runtime 5.1.104.

## Package 5.1.103 / runtime 5.1.102

- Put runtime DLLs and their Assets hierarchy under an explicit `plugins/` folder
  so Gale/r2modman preserve sail textures and translations.
- Rebuild with virtual source paths and without debug symbols.
- Remove the bundled ServerSync binary's original machine-specific CodeView path;
  executable code, assembly identity and public attribution are retained.
- Ship public installation documentation instead of developer build/deployment notes.
- Validate allowed files, privacy patterns, checksums and asset placement before
  accepting the final ZIP. No changes to gameplay, saved identifiers or runtime
  network version are included in this packaging release.

## Runtime 5.1.102

- Fix the deployment check's false alarm on the container's informational log-filter
  declaration and return startup evidence over the deployment connection.

## Runtime 5.1.101

- Initial fork integrating portal synchronization corrections, TargetPortal pairing
  compatibility, admin portal tools and native RaftRebalance policy/migration/refunds.
