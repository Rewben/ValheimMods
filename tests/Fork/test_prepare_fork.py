import hashlib
import json
from contextlib import closing
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
import zipfile


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 is required for staging fixtures')
class PrepareForkTests(unittest.TestCase):
    def test_remote_and_nested_local_upgrades_preserve_active_profile_and_custom_assets(self):
        for nested in (False, True):
            with self.subTest(nested=nested), tempfile.TemporaryDirectory(dir=Path(__file__).parent) as temp:
                root = Path(temp).resolve()
                tools = root / 'tools'
                tools.mkdir()
                for name in ('Prepare-Fork.ps1', 'gale_profile.py'):
                    shutil.copyfile(Path(__file__).parents[2] / 'tools' / name, tools / name)
                profile = root / 'profiles' / 'mine'
                plugins = profile / 'BepInEx' / 'plugins'
                old = plugins / ('ValheimRAFT_Rewben/plugins' if nested else 'rewbensandwich-ValheimRAFT_Rewben')
                (old / 'Assets/Logos').mkdir(parents=True)
                (old / 'ValheimRAFT.dll').write_bytes(b'old runtime')
                (old / 'Assets/Logos/custom.png').write_bytes(b'custom texture')
                (old / 'Assets/Logos/default.png').write_bytes(b'old default')
                (plugins / 'Unrelated').mkdir()
                (plugins / 'Unrelated/Other.dll').write_bytes(b'keep this')
                database = root / 'gale.sqlite3'
                with closing(sqlite3.connect(database)) as conn, conn:
                    conn.execute('CREATE TABLE profiles (id INT, name TEXT, path TEXT, game_slug TEXT, mods JSON)')
                    conn.execute('INSERT INTO profiles VALUES (1,?,?,?,?)', ('mine', str(profile), 'valheim', '[]'))
                package = root / 'release.zip'
                files = {'plugins/ValheimRAFT.dll': b'new runtime', 'plugins/Assets/Logos/default.png': b'new default'}
                with zipfile.ZipFile(package, 'w') as archive:
                    for name, content in files.items():
                        archive.writestr(name, content)
                Path(str(package) + '.sha256').write_text(hashlib.sha256(package.read_bytes()).hexdigest())
                Path(str(package) + '.metadata.json').write_text(json.dumps(dict(version='5.1.104', commit='fixture',
                    files={name: hashlib.sha256(content).hexdigest() for name, content in files.items()})))
                result = subprocess.run(['pwsh', '-NoProfile', '-File', str(tools / 'Prepare-Fork.ps1'),
                    '-Package', str(package), '-Profile', str(profile), '-Python', sys.executable,
                    '-GaleDatabase', str(database)], capture_output=True, text=True, timeout=45)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual((old / 'ValheimRAFT.dll').read_bytes(), b'old runtime')
                sessions = list((root / '.references').glob('maintenance-*'))
                self.assertEqual(len(sessions), 1)
                staged = sessions[0] / 'BepInEx/plugins'
                assemblies = list(staged.rglob('ValheimRAFT.dll'))
                self.assertEqual(len(assemblies), 1)
                self.assertEqual(assemblies[0].read_bytes(), b'new runtime')
                self.assertEqual((assemblies[0].parent / 'Assets/Logos/custom.png').read_bytes(), b'custom texture')
                self.assertEqual((assemblies[0].parent / 'Assets/Logos/default.png').read_bytes(), b'new default')
                self.assertEqual((staged / 'Unrelated/Other.dll').read_bytes(), b'keep this')
                with zipfile.ZipFile(sessions[0] / 'profile-before.zip') as archive:
                    self.assertIsNone(archive.testzip())
