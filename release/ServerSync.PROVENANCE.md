# Bundled ServerSync

Upstream: [blaxxun-boop/ServerSync](https://github.com/blaxxun-boop/ServerSync).
Release: **v1.20**, source commit `c57c2aa54e07cdcc7630d6068699ea781622323e`.
The original release binary has SHA256
`e8b61f621cc396c529d9194f202e0b22c6591dd919363a4501a579f9f7252e0e`.
Its assembly/file version is 1.0.0.0; its upstream release version is 1.20.

For this public package, the staged unsigned DLL's CodeView PDB filename is changed
to `ServerSync.pdb` with zero padding. This removes an upstream build-machine path.
The tool refuses signed assemblies and verifies that no bytes outside the debug
filename change. Executable code, managed metadata, resource content, assembly
identity, file size and CodeView GUID/age remain unchanged. Original dependency
copies in the source tree are not modified. See `tools/release_archive.py` in the
fork source for the reproducible transformation and tests.

The license and attribution are retained in `ServerSync.LICENSE.txt`.
