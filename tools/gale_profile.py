"""Back up/restore one Gale profile metadata row; never read or export auth tables."""
import json
from contextlib import closing
from pathlib import Path
import sqlite3
import sys
import uuid
from datetime import datetime, timezone


def backup(database, profile, destination):
    with closing(sqlite3.connect(Path(database).resolve().as_uri() + '?mode=ro', uri=True)) as connection:
        connection.row_factory = sqlite3.Row
        rows = connection.execute('SELECT * FROM profiles WHERE name = ?', (profile,)).fetchall()
        if len(rows) != 1:
            raise ValueError('Expected exactly one Gale profile')
        Path(destination).write_text(json.dumps(dict(rows[0]), indent=2), encoding='utf-8')


def restore(database, snapshot):
    saved = json.loads(Path(snapshot).read_text(encoding='utf-8'))
    with closing(sqlite3.connect(database)) as connection, connection:
        connection.row_factory = sqlite3.Row
        current = connection.execute('SELECT * FROM profiles WHERE id = ?', (saved['id'],)).fetchone()
        if current is None or current['path'] != saved['path'] or current['game_slug'] != saved['game_slug']:
            raise ValueError('Profile identity changed; refusing metadata rollback')
        columns = {row[1] for row in connection.execute('PRAGMA table_info(profiles)')}
        if set(saved) != columns:
            raise ValueError('Gale schema changed; review rollback before writing')
        keys = [key for key in saved if key != 'id']
        # Column names come only from the actual schema, values are bound parameters.
        assignments = ','.join('"' + key + '"=?' for key in keys)
        connection.execute('UPDATE profiles SET ' + assignments + ' WHERE id=?', [saved[k] for k in keys] + [saved['id']])


def install(database, snapshot, package, metadata):
    saved = json.loads(Path(snapshot).read_text(encoding='utf-8'))
    release = json.loads(Path(metadata).read_text(encoding='utf-8-sig'))
    with closing(sqlite3.connect(database)) as connection, connection:
        connection.row_factory = sqlite3.Row
        current = connection.execute('SELECT * FROM profiles WHERE id=?', (saved['id'],)).fetchone()
        if current is None or dict(current) != saved:
            raise ValueError('Gale profile changed after backup; prepare a fresh session before installation')
        mods = json.loads(current['mods'])
        def replaced(mod):
            return mod.get('fullName', '').startswith(('zolantris-ValheimRAFT-', 'rewbensandwich-ValheimRAFT_Rewben-')) or mod.get('name') in ('RaftRebalance', 'ValheimRAFT_Rewben')
        kept = [mod for mod in mods if not replaced(mod)]
        if len(kept) == len(mods):
            raise ValueError('No upstream or fork RAFT profile entry found')
        profile = Path(current['path'])
        kept.append(dict(enabled=True, installTime=datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
                         name='ValheimRAFT_Rewben', icon=str(profile / 'BepInEx/plugins/ValheimRAFT_Rewben/icon.png'),
                         author='Rewben', description='ValheimRAFT fork with native portal fixes and rebalance policy.',
                         version=release['version'], dependencies=['ValheimModding-Jotunn-2.30.2'], uuid=str(uuid.uuid4()),
                         fileSize=Path(package).stat().st_size, readme='See bundled README.md and github.com/Rewben/ValheimMods.', changelog=None))
        connection.execute('UPDATE profiles SET mods=? WHERE id=?', (json.dumps(kept), saved['id']))


if __name__ == '__main__':
    mode, database, *args = sys.argv[1:]
    if mode == 'backup' and len(args) == 2:
        backup(database, *args)
    elif mode == 'restore' and len(args) == 1:
        restore(database, *args)
    elif mode == 'install' and len(args) == 3:
        install(database, *args)
    else:
        raise ValueError('Usage: gale_profile.py backup DB PROFILE OUTPUT | restore DB SNAPSHOT (close Gale before restore)')
