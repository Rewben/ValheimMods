import importlib.util
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('gale_profile', Path(__file__).parents[2] / 'tools' / 'gale_profile.py')
gale = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gale)


class GaleProfileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.base = Path(self.temp.name)
        self.database = self.base / 'gale.sqlite3'
        self.snapshot = self.base / 'profile.json'
        with closing(sqlite3.connect(self.database)) as c, c:
            c.execute('CREATE TABLE profiles (id INT, name TEXT, path TEXT, game_slug TEXT, mods JSON)')
            c.execute("INSERT INTO profiles VALUES (1,'mine','/mine','valheim','[1]')")
            c.execute("INSERT INTO profiles VALUES (2,'other','/other','valheim','[2]')")

    def tearDown(self):
        self.temp.cleanup()

    def test_backup_and_restore_only_selected_profile(self):
        gale.backup(self.database, 'mine', self.snapshot)
        self.assertEqual(json.loads(self.snapshot.read_text())['mods'], '[1]')
        with closing(sqlite3.connect(self.database)) as c, c:
            c.execute("UPDATE profiles SET mods='[]' WHERE id=1")
        gale.restore(self.database, self.snapshot)
        with closing(sqlite3.connect(self.database)) as c, c:
            self.assertEqual(c.execute('SELECT mods FROM profiles ORDER BY id').fetchall(), [('[1]',), ('[2]',)])

    def test_identity_change_prevents_restore(self):
        gale.backup(self.database, 'mine', self.snapshot)
        with closing(sqlite3.connect(self.database)) as c, c:
            c.execute("UPDATE profiles SET path='/recycled' WHERE id=1")
        with self.assertRaisesRegex(ValueError, 'identity'):
            gale.restore(self.database, self.snapshot)

    def test_schema_change_prevents_restore(self):
        gale.backup(self.database, 'mine', self.snapshot)
        with closing(sqlite3.connect(self.database)) as c, c:
            c.execute('ALTER TABLE profiles ADD COLUMN changed TEXT')
        with self.assertRaisesRegex(ValueError, 'schema'):
            gale.restore(self.database, self.snapshot)

    def test_ambiguous_profile_prevents_backup(self):
        with closing(sqlite3.connect(self.database)) as c, c:
            c.execute("INSERT INTO profiles VALUES (3,'mine','/duplicate','valheim','[]')")
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            gale.backup(self.database, 'mine', self.snapshot)

    def test_install_replaces_only_upstream_and_companion_entries(self):
        mods = [dict(fullName='zolantris-ValheimRAFT-5.1.1'), dict(name='RaftRebalance'), dict(fullName='Azumatt-AzuCraftyBoxes-1.8.28')]
        with closing(sqlite3.connect(self.database)) as c, c:
            c.execute('UPDATE profiles SET mods=? WHERE id=1', (json.dumps(mods),))
        gale.backup(self.database, 'mine', self.snapshot)
        package = self.base / 'release.zip'
        package.write_bytes(b'fixture')
        metadata = self.base / 'metadata.json'
        metadata.write_text(json.dumps(dict(version='5.1.101')))
        gale.install(self.database, self.snapshot, package, metadata)
        with closing(sqlite3.connect(self.database)) as c:
            installed = json.loads(c.execute('SELECT mods FROM profiles WHERE id=1').fetchone()[0])
            other = c.execute('SELECT mods FROM profiles WHERE id=2').fetchone()[0]
        self.assertEqual(installed[0], mods[2])
        self.assertEqual(installed[1]['name'], 'ValheimRAFT_Rewben')
        self.assertNotIn('zolantris-ValheimRAFT-5.0.9', installed[1]['dependencies'])
        self.assertEqual(other, '[2]')

    def test_install_rejects_profile_edits_after_snapshot(self):
        gale.backup(self.database, 'mine', self.snapshot)
        metadata = self.base / 'metadata.json'
        metadata.write_text('{}')
        with closing(sqlite3.connect(self.database)) as c, c:
            c.execute("UPDATE profiles SET mods='[3]' WHERE id=1")
        with self.assertRaisesRegex(ValueError, 'changed after backup'):
            gale.install(self.database, self.snapshot, self.base / 'unused.zip', metadata)

    def test_install_replaces_existing_thunderstore_fork_without_changing_other_mods(self):
        mods = [dict(fullName='rewbensandwich-ValheimRAFT_Rewben-5.1.103', backend='Thunderstore'),
                dict(name='ValheimRAFT_Rewben', version='5.1.102'),
                dict(fullName='Azumatt-AzuCraftyBoxes-1.8.28')]
        with closing(sqlite3.connect(self.database)) as c, c:
            c.execute('UPDATE profiles SET mods=? WHERE id=1', (json.dumps(mods),))
        gale.backup(self.database, 'mine', self.snapshot)
        package = self.base / 'release.zip'
        package.write_bytes(b'fixture')
        metadata = self.base / 'metadata.json'
        metadata.write_text(json.dumps(dict(version='5.1.104')))
        gale.install(self.database, self.snapshot, package, metadata)
        with closing(sqlite3.connect(self.database)) as c:
            installed = json.loads(c.execute('SELECT mods FROM profiles WHERE id=1').fetchone()[0])
        self.assertEqual(installed[0], mods[2])
        self.assertEqual(len(installed), 2)
        self.assertEqual(installed[1]['name'], 'ValheimRAFT_Rewben')
        self.assertEqual(installed[1]['version'], '5.1.104')

