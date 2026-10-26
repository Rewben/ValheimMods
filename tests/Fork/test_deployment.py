import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import types
import unittest
from unittest.mock import patch

# The Linux-only advisory lock is not exercised on Windows.
if sys.platform == 'win32':
    sys.modules['fcntl'] = types.SimpleNamespace()
spec = importlib.util.spec_from_file_location('deployment', Path(__file__).parents[2] / 'tools' / 'deploy_remote.py')
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)
ID = '20261012T230000Z-1234abcd'

def archive(path, bad=None, wrong_hash=False, config_mode=None):
    files = {'plugins/NewMod/NewMod.dll': b'MZfixture', 'config/balance.cfg': b'new settings'}
    manifest = {'deployment': ID, 'label': 'Test', 'files': {
        name: hashlib.sha256(data).hexdigest() for name, data in files.items()}}
    if config_mode is not None:
        manifest['configMode'] = config_mode
    if wrong_hash:
        manifest['files']['config/balance.cfg'] = '0' * 64
    with tarfile.open(path, 'w:gz') as bundle:
        for folder in ('plugins','patchers','config'):
            info = tarfile.TarInfo(folder)
            info.type = tarfile.DIRTYPE
            bundle.addfile(info)
        for name, data in {**files, 'manifest.json': json.dumps(manifest).encode()}.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            bundle.addfile(info, io.BytesIO(data))
        if bad:
            bundle.addfile(bad)

class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.base = Path(self.temp.name)
        self.bundle = self.base / 'pack.tar.gz'

    def tearDown(self):
        self.temp.cleanup()

    def test_fork_health_requires_identity_migration_and_connection(self):
        expected = {'version': '5.1.101', 'commit': 'abc123', 'gameVersion': '1.0.20'}
        good = 'Valheim version: l-1.0.20\nRewben RAFT 5.1.101; commit abc123\nRewben RAFT migration schema=1\nGame server connected'
        self.assertTrue(deployment.check_fork_health(good, expected))
        self.assertFalse(deployment.check_fork_health('Game server connected', expected))
        self.assertFalse(deployment.check_fork_health(good.replace('schema=1','schema=0'), expected))
        for bad in ('Loading [RaftRebalance 1.2.0]', 'MissingMethodException: test'):
            with self.assertRaises(RuntimeError):
                deployment.check_fork_health(good + '\n' + bad, expected)
        with self.assertRaisesRegex(RuntimeError, 'updated'):
            deployment.check_fork_health(good.replace('1.0.20','1.0.21'), expected)

    def test_startup_failure_reports_the_actual_error_without_sensitive_values(self):
        expected = {'version': '5.1.101', 'commit': 'abc123', 'gameVersion': '1.0.20'}
        identity = 'Rewben RAFT 5.1.101; commit abc123\n'
        with self.assertRaisesRegex(RuntimeError, 'MissingMethodException: Example.Missing'):
            deployment.check_fork_health(identity + 'MissingMethodException: Example.Missing', expected)
        try:
            deployment.check_fork_health(identity + 'Could not load password=fixture-sensitive-value', expected)
        except RuntimeError as error:
            self.assertNotIn('fixture-sensitive-value', str(error))
        else:
            self.fail('Startup failure was not rejected')

    def test_full_rollback_restores_chunked_world_and_runtime_together(self):
        archive(self.bundle)
        root = self.base / 'server'
        current = root / 'config' / 'bepinex'
        runtime = root / 'data' / 'bepinex' / 'BepInEx'
        for path in (current / 'plugins', runtime / 'plugins', runtime / 'patchers', runtime / 'config'):
            path.mkdir(parents=True)
        (current / 'plugins' / 'Old.dll').write_bytes(b'old persistent')
        (runtime / 'plugins' / 'Old.dll').write_bytes(b'old runtime')
        (runtime / 'config' / 'old.cfg').write_text('old runtime config')
        world = root / 'config' / 'worlds_local' / 'World.db' / 'chunks' / '1.chunk'
        world.parent.mkdir(parents=True)
        world.write_bytes(b'chunk before')
        with patch.object(Path, 'resolve', lambda p: p.absolute()), patch.object(deployment, 'stop', return_value=True), patch.object(deployment, 'start_and_check'):
            deployment.deploy(root, 'fixture', ID, self.bundle)
            world.write_bytes(b'test activity')
            extra = world.with_name('new.chunk')
            extra.write_bytes(b'test session chunk')
            deployment.rollback_full(root, 'fixture', ID)
        self.assertEqual(world.read_bytes(), b'chunk before')
        self.assertFalse(extra.exists())
        self.assertEqual((current / 'plugins' / 'Old.dll').read_bytes(), b'old persistent')
        self.assertEqual((runtime / 'plugins' / 'Old.dll').read_bytes(), b'old runtime')
        self.assertEqual((runtime / 'config' / 'old.cfg').read_text(), 'old runtime config')

    def test_snapshot_restore_rejects_escape(self):
        with tarfile.open(self.bundle, 'w:gz') as saved:
            saved.addfile(tarfile.TarInfo('config/../../escape'))
        with self.assertRaisesRegex(ValueError, 'Unsafe backup'):
            deployment.restore_config_snapshot(self.bundle, self.base / 'restored')

    def test_snapshot_restore_rejects_truncated_compression(self):
        with tarfile.open(self.bundle, 'w:gz') as saved:
            saved.addfile(tarfile.TarInfo('config'))
        self.bundle.write_bytes(self.bundle.read_bytes()[:-8])
        with self.assertRaises(EOFError):
            deployment.verify_backup(self.bundle)

    def test_diagnostics_edit_is_selective_and_repeat_safe(self):
        cfg = self.base / 'raft.cfg'
        cfg.write_text('[Water]\nHeight = 2\n[Rebalance]\nMaxHullClearance = 1.1\nPortalDiagnostics = false\n[Unrelated]\nOther = keep\n')
        deployment.enable_fork_diagnostics(cfg)
        first = cfg.read_text()
        deployment.enable_fork_diagnostics(cfg)
        self.assertEqual(cfg.read_text(), first)
        self.assertIn('MaxHullClearance = 1.1\n', first)
        self.assertIn('[Unrelated]\nOther = keep\n', first)
        self.assertEqual(first.count('PortalDiagnostics = true'), 1)

    def test_diagnostics_missing_section_is_added(self):
        cfg = self.base / 'raft.cfg'
        cfg.write_text('[Water]\nHeight = 2\n')
        deployment.enable_fork_diagnostics(cfg)
        self.assertTrue(cfg.read_text().startswith('[Water]\nHeight = 2\n'))
        self.assertIn('[Rebalance]\nPortalDiagnostics = true', cfg.read_text())

    def test_inventory_corruption_rejected(self):
        archive(self.bundle, wrong_hash=True)
        with self.assertRaisesRegex(ValueError, 'checksum'):
            deployment.unpack(self.bundle, self.base / 'out', ID)

    def test_path_traversal_rejected(self):
        archive(self.bundle, tarfile.TarInfo('../escape'))
        with self.assertRaisesRegex(ValueError, 'Unsafe'):
            deployment.unpack(self.bundle, self.base / 'out', ID)
        self.assertFalse((self.base / 'escape').exists())

    def test_symlink_rejected(self):
        link = tarfile.TarInfo('plugins/link')
        link.type = tarfile.SYMTYPE
        link.linkname = '/etc/passwd'
        archive(self.bundle, link)
        with self.assertRaisesRegex(ValueError, 'unsupported'):
            deployment.unpack(self.bundle, self.base / 'out', ID)

    def test_wrong_container_mount_rejected(self):
        other = self.base / 'other-server'
        other.mkdir()
        info = {'Mounts': [{'Destination': '/config', 'Type': 'bind', 'Source': str(other)}],
                'Config': {'Env': ['BEPINEX=true', 'UPDATE_CRON=']}}
        with patch.object(deployment, 'run', return_value=json.dumps([info])), patch.object(Path, 'resolve', lambda p: p.absolute()):
            with self.assertRaisesRegex(RuntimeError, 'must bind-mount'):
                deployment.inspect('fixture', self.base / 'expected-server')

    def test_forced_shutdown_aborts(self):
        states = [json.dumps([{'State': {'Running': True}}]), '',
                  json.dumps([{'State': {'Running': False, 'ExitCode': 137, 'OOMKilled': False}}])]
        with patch.object(deployment, 'run', side_effect=states):
            with self.assertRaisesRegex(RuntimeError, 'not clean'):
                deployment.stop('fixture')

    def test_inspect_allows_automatic_updates(self):
        root = self.base / 'server'
        mounts = [{'Destination': destination, 'Type': 'bind', 'Source': str(root / folder)}
                  for destination, folder in (('/config', 'config'), ('/opt/valheim', 'data'))]
        for update_env in ([], ['UPDATE_CRON='], ['UPDATE_CRON=*/15 * * * *'],
                           ['UPDATE_CRON=*/15 * * * *', 'UPDATE_INTERVAL=900']):
            with self.subTest(update_env=update_env):
                info = {'Mounts': mounts, 'Config': {'Env': ['BEPINEX=true'] + update_env}}
                with patch.object(deployment, 'run', return_value=json.dumps([info])), patch.object(Path, 'resolve', lambda p: p.absolute()):
                    self.assertEqual(deployment.inspect('fixture', root), info)

    def test_replace_and_rollback_preserve_world(self):
        archive(self.bundle, config_mode='replace')
        root = self.base / 'server'
        modtree = root / 'config' / 'bepinex'
        (modtree / 'plugins').mkdir(parents=True)
        (modtree / 'plugins' / 'Removed.dll').write_bytes(b'old mod')
        (modtree / 'balance.cfg').write_text('old settings')
        runtime = root / 'data' / 'bepinex' / 'BepInEx'
        for folder in ('plugins', 'patchers', 'core', 'config'):
            (runtime / folder).mkdir(parents=True)
        (runtime / 'plugins' / 'StaleRelicHeim.dll').write_bytes(b'stale runtime mod')
        (runtime / 'patchers' / 'StalePatcher.dll').write_bytes(b'stale patcher')
        (runtime / 'core' / 'BepInEx.dll').write_bytes(b'Linux loader')
        (runtime / 'config' / 'obsolete.cfg').write_text('stale configuration')
        world = root / 'config' / 'worlds_local' / 'RelicHeim01.db'
        world.parent.mkdir()
        world.write_bytes(b'world before')
        real_run = deployment.run
        def local_only(*args):
            if args[0] == 'docker':
                return ''
            return real_run(*args)
        with patch.object(Path, 'resolve', lambda p: p.absolute()), patch.object(deployment, 'stop', return_value=True), patch.object(deployment, 'start_and_check'), patch.object(deployment, 'run', side_effect=local_only):
            deployment.deploy(root, 'fixture', ID, self.bundle)
            self.assertFalse((modtree / 'plugins' / 'Removed.dll').exists())
            self.assertEqual((modtree / 'balance.cfg').read_text(), 'new settings')
            self.assertEqual(world.read_bytes(), b'world before')
            self.assertFalse((runtime / 'plugins' / 'StaleRelicHeim.dll').exists())
            self.assertFalse((runtime / 'patchers' / 'StalePatcher.dll').exists())
            self.assertEqual(deployment.tree_inventory(runtime / 'plugins'), deployment.tree_inventory(modtree / 'plugins'))
            self.assertEqual(list((runtime / 'patchers').iterdir()), [])
            self.assertFalse((runtime / 'config' / 'obsolete.cfg').exists())
            self.assertEqual((runtime / 'config' / 'balance.cfg').read_text(), 'new settings')
            self.assertEqual((runtime / 'core' / 'BepInEx.dll').read_bytes(), b'Linux loader')
            backup = root / 'deployments' / ID / 'config-before.tar.gz'
            with tarfile.open(backup) as saved:
                self.assertEqual(saved.extractfile('config/worlds_local/RelicHeim01.db').read(), b'world before')
            world.write_bytes(b'world after')
            deployment.rollback(root, 'fixture', ID)
            self.assertTrue((modtree / 'plugins' / 'Removed.dll').exists())
            self.assertEqual((modtree / 'balance.cfg').read_text(), 'old settings')
            self.assertEqual(world.read_bytes(), b'world after')
            self.assertEqual((runtime / 'plugins' / 'StaleRelicHeim.dll').read_bytes(), b'stale runtime mod')
            self.assertTrue((runtime / 'patchers' / 'StalePatcher.dll').exists())
            self.assertEqual((runtime / 'config' / 'obsolete.cfg').read_text(), 'stale configuration')

    def test_runtime_swap_failure_restores_both_trees(self):
        root = self.base / 'server'
        persistent = root / 'config' / 'bepinex'
        runtime = root / 'data' / 'bepinex' / 'BepInEx'
        source = self.base / 'source'
        record = root / 'deployments' / ID
        for folder in (persistent / 'plugins', runtime / 'plugins', source / 'plugins', source / 'patchers', record):
            folder.mkdir(parents=True)
        (persistent / 'plugins' / 'Old.dll').write_bytes(b'old persistent')
        (runtime / 'plugins' / 'Old.dll').write_bytes(b'old runtime')
        (source / 'plugins' / 'New.dll').write_bytes(b'new')
        original_rename = Path.rename
        def fail_runtime_stage(path, target):
            if path.name == 'mirror-stage-1':
                raise OSError('simulated runtime failure')
            return original_rename(path, target)
        with patch.object(Path, 'resolve', lambda p: p.absolute()), patch.object(Path, 'rename', fail_runtime_stage):
            with self.assertRaisesRegex(OSError, 'runtime failure'):
                deployment.replace_mod_trees(root, source, record)
        self.assertEqual((persistent / 'plugins' / 'Old.dll').read_bytes(), b'old persistent')
        self.assertEqual((runtime / 'plugins' / 'Old.dll').read_bytes(), b'old runtime')
        self.assertFalse((persistent / 'plugins' / 'New.dll').exists())

    def test_backup_failure_preserves_mods_and_restarts(self):
        archive(self.bundle)
        root = self.base / 'server'
        tree = root / 'config' / 'bepinex'
        tree.mkdir(parents=True)
        (tree / 'original.cfg').write_text('original')
        calls = []
        def fail_backup(*args):
            calls.append(args)
            if args[0] == 'tar':
                raise RuntimeError('disk full')
            return ''
        with patch.object(deployment, 'stop', return_value=True), patch.object(deployment, 'run', side_effect=fail_backup):
            with self.assertRaisesRegex(RuntimeError, 'disk full'):
                deployment.deploy(root, 'fixture', ID, self.bundle)
        self.assertEqual((tree / 'original.cfg').read_text(), 'original')
        self.assertIn(('docker', 'start', 'fixture'), calls)

    def test_container_absolute_config_symlink_is_preserved(self):
        root = self.base / 'server'
        source = self.base / 'source'
        record = root / 'deployments' / ID
        runtime = root / 'data' / 'bepinex' / 'BepInEx'
        for directory in (source / 'plugins', source / 'patchers', record):
            directory.mkdir(parents=True)
        (source / 'balance.cfg').write_text('settings')
        original_is_symlink = Path.is_symlink
        # Simulate Linux's /config/bepinex link without requiring Windows link privileges.
        with patch.object(Path, 'resolve', lambda p: p.absolute()), patch.object(
                Path, 'is_symlink', lambda p: p == runtime / 'config' or original_is_symlink(p)), patch.object(
                deployment.os, 'readlink', return_value='/config/bepinex'):
            deployment.replace_mod_trees(root, source, record)
        self.assertFalse((record / 'previous-runtime' / 'config').exists())
        self.assertEqual((root / 'config' / 'bepinex' / 'balance.cfg').read_text(), 'settings')

    def test_default_deploy_preserves_config_saved_during_shutdown(self):
        archive(self.bundle)  # No explicit mode must also default to preserve.
        root = self.base / 'server'
        current = root / 'config' / 'bepinex'
        (current / 'plugins').mkdir(parents=True)
        (current / 'plugins' / 'Removed.dll').write_bytes(b'old')
        (current / 'balance.cfg').write_text('before shutdown')
        (current / 'server-only.json').write_text('{"preserve":true}')
        def stop_and_save(container):
            (current / 'balance.cfg').write_text('authoritative server settings at shutdown')
            return True
        with patch.object(Path, 'resolve', lambda p: p.absolute()), patch.object(deployment, 'stop', side_effect=stop_and_save), patch.object(deployment, 'start_and_check'):
            deployment.deploy(root, 'fixture', ID, self.bundle)
        self.assertEqual((current / 'balance.cfg').read_text(), 'authoritative server settings at shutdown')
        self.assertEqual((current / 'server-only.json').read_text(), '{"preserve":true}')
        self.assertFalse((current / 'plugins' / 'Removed.dll').exists())
        self.assertTrue((current / 'plugins' / 'NewMod' / 'NewMod.dll').exists())
        active = root / 'data' / 'bepinex' / 'BepInEx'
        self.assertEqual((active / 'config' / 'balance.cfg').read_text(), (current / 'balance.cfg').read_text())
        self.assertEqual(deployment.tree_inventory(active / 'plugins'), deployment.tree_inventory(current / 'plugins'))

    def test_preserve_seeds_only_missing_nested_config_paths(self):
        source = self.base / 'source'
        current = self.base / 'current'
        incoming = source / 'config'
        for folder in (incoming / 'tables', incoming / 'collision', current / 'tables', source / 'plugins', source / 'patchers'):
            folder.mkdir(parents=True)
        (incoming / 'tables' / 'existing.yml').write_text('local default')
        (current / 'tables' / 'existing.yml').write_text('custom server')
        (incoming / 'tables' / 'new.json').write_text('new defaults')
        (current / 'collision').write_text('server file wins over incoming directory')
        (incoming / 'collision' / 'child.cfg').write_text('ignore')
        (incoming / 'new.cfg').write_text('new mod')
        (current / 'orphan.cfg').write_text('keep even if mod removed')
        output = self.base / 'out'
        deployment.prepare_modtree(source, current, output, 'preserve')
        self.assertEqual((output / 'tables' / 'existing.yml').read_text(), 'custom server')
        self.assertEqual((output / 'tables' / 'new.json').read_text(), 'new defaults')
        self.assertEqual((output / 'new.cfg').read_text(), 'new mod')
        self.assertEqual((output / 'collision').read_text(), 'server file wins over incoming directory')
        self.assertTrue((output / 'orphan.cfg').exists())

    def test_invalid_config_mode_rejected_before_stopping(self):
        archive(self.bundle, config_mode='invalid')
        with patch.object(deployment, 'stop') as stop:
            with self.assertRaisesRegex(ValueError, 'configuration mode'):
                deployment.deploy(self.base / 'server', 'fixture', ID, self.bundle)
            stop.assert_not_called()

if __name__ == '__main__':
    unittest.main()

