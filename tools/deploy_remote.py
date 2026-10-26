#!/usr/bin/env python3
"""Run via SSH as root on the Docker host. No third-party Python dependencies."""
import fcntl
import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time

ID_RE = re.compile(r"\d{8}T\d{6}Z-[a-f0-9]{8}\Z")

def hash_file(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()

def run(*args):
    return subprocess.check_output(args, text=True).strip()

def inspect(container, root):
    info = json.loads(run('docker', 'inspect', container))[0]
    mounts = {m['Destination']: m for m in info['Mounts']}
    for destination, expected in (('/config', root / 'config'), ('/opt/valheim', root / 'data')):
        mount = mounts.get(destination, {})
        if mount.get('Type') != 'bind' or Path(mount.get('Source', '/')).resolve() != expected:
            raise RuntimeError(f'{container}: {destination} must bind-mount {expected}')
    env = dict(e.split('=', 1) for e in info['Config']['Env'] if '=' in e)
    if env.get('BEPINEX', '').lower() != 'true':
        raise RuntimeError('Enable BEPINEX=true in the Portainer stack first.')
    return info

def unpack(archive, destination, deployment):
    """Validate paths, types, file bounds, and the complete checksum inventory."""
    seen = set()
    total = 0
    with tarfile.open(archive, 'r:gz') as bundle:
        for member in bundle:
            name = member.name
            while name.startswith('./'):
                name = name[2:]
            if name in ('', '.') and member.isdir():
                continue
            path = PurePosixPath(name)
            if path.is_absolute() or '..' in path.parts or '\\' in name or ':' in name:
                raise ValueError(f'Unsafe archive path: {name}')
            if path.parts[0] not in ('plugins', 'patchers', 'config', 'manifest.json'):
                raise ValueError(f'Unexpected archive path: {name}')
            if name in seen or not (member.isdir() or member.isfile()):
                raise ValueError(f'Duplicate or unsupported archive entry: {name}')
            seen.add(name)
            total += member.size
            if total > 4 * 1024**3 or len(seen) > 50000:
                raise ValueError('Archive exceeds deployment limits.')
            target = destination / path
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.extractfile(member) as source, target.open('wb') as output:
                    shutil.copyfileobj(source, output)
                target.chmod(0o644)
    manifest = json.loads((destination / 'manifest.json').read_text(encoding='utf-8-sig'))
    if manifest['deployment'] != deployment:
        raise ValueError('Deployment ID mismatch.')
    # Exclude only the root manifest (a plugin file with that name must still be hashed).
    actual = {p.relative_to(destination).as_posix(): hash_file(p)
              for p in destination.rglob('*') if p.is_file() and p != destination / 'manifest.json'}
    if actual != manifest['files']:
        raise ValueError('Archive inventory/checksum mismatch.')
    for directory in ('plugins', 'patchers', 'config'):
        if not (destination / directory).is_dir():
            raise ValueError(f'Missing {directory} directory.')
    if not list((destination / 'plugins').rglob('*.dll')):
        raise ValueError('No plugins found.')
    return manifest

def stop(container):
    info = json.loads(run('docker', 'inspect', container))[0]
    was_running = info['State']['Running']
    if was_running:
        print('Stopping Valheim (up to 120 seconds)...', flush=True)
        run('docker', 'stop', '--timeout', '120', container)
        state = json.loads(run('docker', 'inspect', container))[0]['State']
        if state['Running'] or state.get('OOMKilled') or state['ExitCode'] == 137:
            raise RuntimeError('Shutdown was not clean; deployment aborted. Review server logs.')
    return was_running

def check_fork_health(text, expected):
    if not expected:
        return 'Game server connected' in text
    if '[Info   :RaftRebalance]' in text or re.search(r'Loading \[RaftRebalance ', text):
        raise RuntimeError('Companion loaded alongside the integrated fork.')
    version = expected['version']
    if 'Rewben RAFT ' + version not in text:
        return False
    if expected['commit'] not in text:
        return False
    if re.search(r'(Could not load|Failed to load|Error loading|HarmonyException|MissingMethodException|TypeLoadException)', text, re.I):
        raise RuntimeError('Plugin startup error; inspect startup.log before gameplay.')
    game = re.findall(r'Valheim version:\s*(?:l-)?([0-9.]+)', text)
    if game and game[-1] != expected['gameVersion']:
        raise RuntimeError(f'Game updated to {game[-1]}; recheck client/reference compatibility before gameplay.')
    return 'Game server connected' in text and 'Rewben RAFT migration schema=1' in text

def start_and_check(container, expected=None, record=None):
    since = str(int(time.time()))
    run('docker', 'start', container)
    print('Waiting up to 8 minutes for Game server connected...', flush=True)
    deadline = time.monotonic() + 480
    while time.monotonic() < deadline:
        state = json.loads(run('docker', 'inspect', container))[0]['State']
        if not state['Running'] or state.get('Restarting'):
            break
        logs = subprocess.run(['docker', 'logs', '--since', since, container],
                              capture_output=True, text=True)
        text = logs.stdout + logs.stderr
        if record:
            (record / 'startup.log').write_text(text)
        try:
            healthy = check_fork_health(text, expected)
        except Exception:
            stop(container)
            raise
        if healthy:
            print('Server connected. Test a modded client before inviting everyone.', flush=True)
            return
        time.sleep(5)
    stop(container)
    raise RuntimeError('Readiness not confirmed; server stopped. Review docker logs and the saved backup.')

def tree_inventory(path):
    if any(p.is_symlink() for p in path.rglob('*')):
        raise RuntimeError(f'Links are not supported in mirrored files: {path}')
    return {p.relative_to(path).as_posix(): hash_file(p)
            for p in path.rglob('*') if p.is_file()}

def replace_mod_trees(root, source, record, runtime_source=None):
    """Replace persistent and active loadables together; undo every swap on failure."""
    persistent = root / 'config' / 'bepinex'
    runtime = root / 'data' / 'bepinex' / 'BepInEx'
    if runtime.resolve() != runtime:
        raise RuntimeError('Runtime BepInEx path cannot pass through symlinks.')
    runtime.mkdir(parents=True, exist_ok=True)
    targets = [(persistent, source, record / 'previous-bepinex')]
    for folder in ('plugins', 'patchers'):
        origin = (runtime_source or source) / folder
        targets.append((runtime / folder, origin, record / 'previous-runtime' / folder))
    # The container normally links active config to /config/bepinex. Preserve that link.
    active_config = runtime / 'config'
    if active_config.is_symlink():
        if os.readlink(active_config) != '/config/bepinex' and active_config.resolve() != persistent.resolve():
            raise RuntimeError('Active config link does not point to the persistent mod configuration.')
    else:
        config_stage = record / 'config-only'
        config_origin = runtime_source / 'config' if runtime_source and (runtime_source / 'config').is_dir() else source
        shutil.copytree(config_origin, config_stage, ignore=shutil.ignore_patterns('plugins', 'patchers'))
        targets.append((active_config, config_stage, record / 'previous-runtime' / 'config'))
    staged = []
    for index, (target, origin, backup) in enumerate(targets):
        if target.is_symlink() or (target.exists() and not target.is_dir()):
            raise RuntimeError(f'Mirror target must be a real directory: {target}')
        stage = record / f'mirror-stage-{index}'
        if origin.is_dir():
            shutil.copytree(origin, stage)
        else:
            stage.mkdir()
        expected = tree_inventory(stage)
        staged.append((target, stage, backup, expected))
    changed = []
    try:
        for target, stage, backup, expected in staged:
            target.parent.mkdir(parents=True, exist_ok=True)
            backup.parent.mkdir(parents=True, exist_ok=True)
            existed = target.exists()
            if existed:
                target.rename(backup)
            changed.append((target, backup, existed))
            stage.rename(target)
            if tree_inventory(target) != expected:
                raise RuntimeError(f'Mirror verification failed: {target}')
    except Exception:
        for target, backup, existed in reversed(changed):
            if target.exists():
                shutil.rmtree(target)
            if existed:
                backup.rename(target)
        raise
    print('Verified persistent configuration and exact runtime plugin/patcher mirrors.', flush=True)

def add_missing_configs(source, destination):
    """Existing server paths always win, including file/directory collisions."""
    for entry in source.iterdir():
        target = destination / entry.name
        if not target.exists():
            if entry.is_dir():
                shutil.copytree(entry, target)
            else:
                shutil.copy2(entry, target)
        elif entry.is_dir() and target.is_dir():
            add_missing_configs(entry, target)


def prepare_modtree(source, current, destination, mode):
    if mode not in ('preserve', 'replace'):
        raise ValueError('Invalid configuration mode.')
    for folder in ('plugins', 'patchers'):
        if (source / 'config' / folder).exists():
            raise RuntimeError(f'Config folder contains reserved name {folder}.')
    if mode == 'preserve' and current.is_dir():
        tree_inventory(current)  # Reject symlinks before copying server files.
        shutil.copytree(current, destination,
                        ignore=lambda directory, names: [n for n in names if n in ('plugins', 'patchers')]
                        if Path(directory) == current else [])
        add_missing_configs(source / 'config', destination)
    else:
        shutil.copytree(source / 'config', destination)
    for folder in ('plugins', 'patchers'):
        shutil.copytree(source / folder, destination / folder)

def enable_fork_diagnostics(path):
    lines = path.read_text(encoding='utf-8-sig').splitlines(keepends=True) if path.exists() else []
    start = next((i for i, line in enumerate(lines) if line.strip() == '[Rebalance]'), None)
    if start is None:
        lines.extend(['\n[Rebalance]\n', 'PortalDiagnostics = true\n'])
    else:
        end = next((i for i in range(start + 1, len(lines)) if lines[i].lstrip().startswith('[')), len(lines))
        found = [i for i in range(start + 1, end) if re.match(r'^\s*PortalDiagnostics\s*=', lines[i])]
        if len(found) > 1:
            raise ValueError('Duplicate PortalDiagnostics settings')
        if found:
            lines[found[0]] = 'PortalDiagnostics = true\n'
        else:
            lines.insert(end, 'PortalDiagnostics = true\n')
    path.write_text(''.join(lines), encoding='utf-8')


def deploy(root, container, deployment, archive):
    with tempfile.TemporaryDirectory(prefix='relicheim-validated-') as temporary:
        source = Path(temporary)
        manifest = unpack(archive, source, deployment)
        expected = manifest.get('fork')
        if expected:
            companions = [p for p in (source / 'plugins').rglob('*.dll') if p.stem.lower() == 'raftrebalance']
            if companions:
                raise RuntimeError('Staged inventory still contains RaftRebalance.')
        mode = manifest.get('configMode', 'preserve')
        if mode not in ('preserve', 'replace'):
            raise ValueError('Invalid configuration mode.')
        record = root / 'deployments' / deployment
        record.mkdir(parents=True, exist_ok=False)
        modtree = root / 'config' / 'bepinex'
        if modtree.is_symlink():
            raise RuntimeError('Persistent mod tree must not be a symlink.')
        newtree = record / 'new-bepinex'
        (record / 'manifest.json').write_text(json.dumps(manifest, indent=2))
        was_running = stop(container)
        # Back up every config file, including worlds, lists, and existing mods, after shutdown.
        try:
            run('tar', '-czf', str(record / 'config-before.tar.gz'), '-C', str(root), 'config')
            run('tar', '-tzf', str(record / 'config-before.tar.gz'))
            verify_backup(record / 'config-before.tar.gz')
            (record / 'config-before.tar.gz.sha256').write_text(hash_file(record / 'config-before.tar.gz'))
            runtime = root / 'data' / 'bepinex' / 'BepInEx'
            prior = {name: tree_inventory(runtime / name) for name in ('plugins', 'patchers', 'config')
                     if (runtime / name).is_dir() and not (runtime / name).is_symlink()}
            (record / 'previous-runtime-inventory.json').write_text(json.dumps(prior))
            # Read server settings only AFTER shutdown, including changes saved on exit.
            prepare_modtree(source, modtree, newtree, mode)
            if expected and expected.get('diagnostics'):
                enable_fork_diagnostics(newtree / 'zolantris.ValheimRAFT.cfg')
        except Exception:
            if was_running:
                run('docker', 'start', container)
            raise
        print(f'Backup: {record / "config-before.tar.gz"}', flush=True)
        print(f'Configuration mode: {mode}. Existing files win in preserve mode.', flush=True)
        try:
            replace_mod_trees(root, newtree, record)
        except Exception:
            if was_running:
                run('docker', 'start', container)
            raise
        (record / 'installed').touch()
        print(f'Installed {manifest.get("label", deployment)}. Deployment ID: {deployment}', flush=True)
        start_and_check(container, expected, record)

def verify_backup(archive):
    # Read through EOF, including the gzip checksum, rather than only listing tar headers.
    with gzip.open(archive, 'rb') as stream:
        while stream.read(1024 * 1024):
            pass

def restore_config_snapshot(archive, destination):
    verify_backup(archive)
    with tarfile.open(archive, 'r:gz') as bundle:
        seen = set()
        for member in bundle:
            path = PurePosixPath(member.name)
            if path.is_absolute() or '..' in path.parts or not path.parts or path.parts[0] != 'config' or '\\' in member.name:
                raise ValueError('Unsafe backup path: ' + member.name)
            if member.name in seen or not (member.isdir() or member.isfile()):
                raise ValueError('Unsupported backup entry: ' + member.name)
            seen.add(member.name)
            target = destination.joinpath(*path.parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.extractfile(member) as source, target.open('wb') as output:
                    shutil.copyfileobj(source, output)
                target.chmod(member.mode & 0o777)
                os.utime(target, (member.mtime, member.mtime))
            if os.name == 'posix':
                os.chown(target, member.uid, member.gid)
                target.chmod(member.mode & 0o777)

def rollback_full(root, container, deployment):
    record = root / 'deployments' / deployment
    archive = record / 'config-before.tar.gz'
    if not (record / 'installed').exists() or not archive.is_file():
        raise RuntimeError('No complete installed deployment snapshot.')
    expected = (record / 'config-before.tar.gz.sha256').read_text().strip()
    if hash_file(archive) != expected:
        raise RuntimeError('Snapshot checksum mismatch; rollback aborted.')
    failed = root / 'deployments' / ('full-rollback-' + str(time.time_ns()))
    failed.mkdir()
    restore_config_snapshot(archive, failed / 'restored')
    runtime = root / 'data' / 'bepinex' / 'BepInEx'
    prior = json.loads((record / 'previous-runtime-inventory.json').read_text())
    for name, inventory in prior.items():
        saved = record / 'previous-runtime' / name
        if tree_inventory(saved) != inventory:
            raise RuntimeError('Previous runtime checksum mismatch: ' + name)
    swaps = [(root / 'config', failed / 'restored' / 'config', failed / 'failed-config')]
    for name in ('plugins', 'patchers', 'config'):
        active = runtime / name
        saved = record / 'previous-runtime' / name
        if name == 'config' and active.is_symlink():
            if os.readlink(active) != '/config/bepinex':
                raise RuntimeError('Unexpected runtime config symlink.')
            continue
        if not saved.is_dir():
            if name == 'config':
                saved = failed / 'restored' / 'config' / 'bepinex'
            else:
                # An absent previous tree represents an empty installation.
                saved = failed / ('empty-' + name)
                saved.mkdir()
        stage = failed / ('stage-' + name)
        shutil.copytree(saved, stage)
        swaps.append((active, stage, failed / ('failed-runtime-' + name)))
    for active, stage, backup in swaps:
        if active.is_symlink() or active.resolve() != active:
            raise RuntimeError('Rollback target must not traverse a symlink: ' + str(active))
        tree_inventory(stage)
    stop(container)
    changed = []
    try:
        for active, stage, backup in swaps:
            existed = active.exists()
            if existed:
                active.rename(backup)
            changed.append((active, backup, existed))
            stage.rename(active)
    except Exception:
        for active, backup, existed in reversed(changed):
            if active.exists():
                shutil.rmtree(active)
            if existed:
                backup.rename(active)
        raise
    print('Restored stopped-server world/chunks, configuration, persistent and runtime mods together. Test-session activity was rolled back.', flush=True)
    start_and_check(container)

def rollback(root, container, deployment):
    record = root / 'deployments' / deployment
    previous = record / 'previous-bepinex'
    modtree = root / 'config' / 'bepinex'
    if not previous.is_dir() or previous.is_symlink() or not (record / 'installed').exists():
        raise RuntimeError('No previous mod tree available for this deployment.')
    failed = root / 'deployments' / ('rollback-' + str(time.time_ns()))
    failed.mkdir()
    was_running = stop(container)
    try:
        run('tar', '-czf', str(failed / 'config-before.tar.gz'), '-C', str(root), 'config')
        run('tar', '-tzf', str(failed / 'config-before.tar.gz'))
        saved_runtime = record / 'previous-runtime'
        replace_mod_trees(root, previous, failed,
                          runtime_source=saved_runtime if saved_runtime.is_dir() else None)
    except Exception:
        if was_running:
            run('docker', 'start', container)
        raise
    print('Previous mods restored. World was not rolled back. Full backups remain in deployments/.', flush=True)
    start_and_check(container)

def main():
    if os.geteuid() != 0:
        raise RuntimeError('Run through SSH as root (or arrange root execution explicitly).')
    if len(sys.argv) not in (5, 6):
        raise ValueError('Usage: deploy_remote.py deploy|rollback ROOT CONTAINER DEPLOYMENT [ARCHIVE]')
    mode, raw_root, container, deployment = sys.argv[1:5]
    if not re.fullmatch(r'/srv/games/valheim/[a-zA-Z0-9_-]+', raw_root) or not ID_RE.fullmatch(deployment):
        raise ValueError('Invalid root/deployment ID.')
    if not re.fullmatch(r'[a-zA-Z0-9_-]+', container) or mode not in ('deploy', 'rollback', 'rollback-full'):
        raise ValueError('Invalid container/mode.')
    if (mode == 'deploy' and len(sys.argv) != 6) or (mode in ('rollback', 'rollback-full') and len(sys.argv) != 5):
        raise ValueError('Wrong number of arguments for mode.')
    root = Path(raw_root)
    if root.resolve() != root:
        raise ValueError('Server root cannot pass through symlinks.')
    inspect(container, root)
    root.mkdir(exist_ok=True)
    with (root / '.deployment.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if mode == 'deploy':
            deploy(root, container, deployment, sys.argv[5])
        elif mode == 'rollback-full':
            rollback_full(root, container, deployment)
        else:
            rollback(root, container, deployment)

if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'DEPLOYMENT FAILED: {error}', file=sys.stderr)
        sys.exit(1)
