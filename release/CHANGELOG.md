# Changelog

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
