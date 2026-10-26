import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile
import hashlib

spec = importlib.util.spec_from_file_location('release_archive', Path(__file__).parents[2] / 'tools' / 'release_archive.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def pe_fixture(signed=False):
    data = bytearray(1024)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3c, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<H', data, 0x86, 1)
    struct.pack_into('<H', data, 0x94, 224)
    optional = 0x98
    struct.pack_into('<H', data, optional, 0x10b)
    directories = optional + 96
    struct.pack_into('<II', data, directories + 6 * 8, 0x2070, 28)
    struct.pack_into('<II', data, directories + 14 * 8, 0x2020, 72)
    section = optional + 224
    struct.pack_into('<IIII', data, section + 8, 512, 0x2000, 512, 512)
    struct.pack_into('<I', data, 512 + 0x20 + 16, 9 if signed else 1)
    record = b'RSDS' + b'g' * 16 + b'\x01\0\0\0' + b'C:\\Users\\fixture-user\\project\\ServerSync.pdb\0'
    struct.pack_into('<IIII', data, 512 + 0x70 + 12, 2, len(record), 0x2100, 512 + 0x100)
    data[512 + 0x100:512 + 0x100 + len(record)] = record
    return bytes(data)


def make_zip(path, extra=None, flat_assets=False):
    files = {name: b'public fixture' for name in release.PUBLIC_ROOT - {'manifest.json', 'release-metadata.json'}}
    files.update({'plugins/' + name: b'fixture assembly' for name in release.RUNTIME_DLLS})
    for group in ('Logos', 'Patterns', 'Sails', 'Translations'):
        prefix = 'Assets/' if flat_assets else 'plugins/Assets/'
        files[prefix + group + '/fixture.json'] = b'{}'
    files['manifest.json'] = json.dumps({'name':'ValheimRAFT_Rewben', 'version_number':'5.1.103',
                                        'dependencies':['ValheimModding-Jotunn-2.30.2']}).encode()
    if extra:
        files.update(extra)
    metadata = {'packageVersion':'5.1.103', 'runtimeVersion':'5.1.102',
                'files':{name:hashlib.sha256(data).hexdigest() for name, data in files.items()}}
    files['release-metadata.json'] = json.dumps(metadata).encode()
    with zipfile.ZipFile(path, 'w') as archive:
        for name, data in files.items():
            archive.writestr(name, data)


class ReleaseArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).parent)
        self.base = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def test_package_and_runtime_versions_can_differ_for_packaging_only_release(self):
        path = self.base / 'release.zip'
        make_zip(path)
        result = release.validate(path)
        self.assertEqual(result['package_version'], '5.1.103')
        self.assertEqual(result['runtime_version'], '5.1.102')

    def test_old_flat_assets_layout_is_rejected(self):
        path = self.base / 'release.zip'
        make_zip(path, flat_assets=True)
        with self.assertRaisesRegex(ValueError, 'Unexpected public archive entry'):
            release.validate(path)

    def test_private_path_in_utf16_assembly_string_is_rejected(self):
        path = self.base / 'release.zip'
        make_zip(path, {'plugins/ValheimRAFT.dll': b'x' + 'C:\\Users\\fixture-user\\project\\code.cs'.encode('utf-16le')})
        with self.assertRaisesRegex(ValueError, 'Privacy scan failed'):
            release.validate(path)

    def test_configs_saves_and_debug_symbols_are_not_allowed(self):
        for name in ('BepInEx/config/private.cfg', 'character.fch', 'plugins/ValheimRAFT.pdb', 'data.sqlite3'):
            with self.subTest(name=name):
                path = self.base / 'release.zip'
                make_zip(path, {name:b'private fixture'})
                with self.assertRaisesRegex(ValueError, 'Unexpected public archive entry'):
                    release.validate(path)

    def test_public_source_url_and_virtual_build_paths_are_allowed(self):
        self.assertEqual(release.privacy_findings(b'https://github.com/Rewben/ValheimMods\0/_/ValheimMods/src/file.cs'), [])

    def test_compressed_binary_fragments_are_not_interpreted_as_file_paths(self):
        self.assertEqual(release.privacy_findings(b'\xffT:/\x11\xff\x01\0Z:\\\x10\xf0'), [])

    def test_debug_redaction_changes_only_the_filename(self):
        path = self.base / 'ServerSync.dll'
        original = pe_fixture()
        path.write_bytes(original)
        result = release.redact_codeview(path)
        after = path.read_bytes()
        left = 512 + 0x100 + 24
        right = 512 + 0x100 + len(b'RSDS' + b'g'*16 + b'\x01\0\0\0' + b'C:\\Users\\fixture-user\\project\\ServerSync.pdb\0')
        self.assertEqual(original[:left], after[:left])
        self.assertEqual(original[right:], after[right:])
        self.assertEqual(len(after), len(original))
        self.assertEqual(after[left:right].split(b'\0')[0], b'ServerSync.pdb')
        self.assertEqual(result['codeview_records'], 1)
        self.assertFalse(release.privacy_findings(after))

    def test_signed_assembly_is_not_modified(self):
        path = self.base / 'ServerSync.dll'
        original = pe_fixture(signed=True)
        path.write_bytes(original)
        with self.assertRaisesRegex(ValueError, 'strong-name-signed'):
            release.redact_codeview(path)
        self.assertEqual(path.read_bytes(), original)

    def test_private_key_and_access_token_are_rejected(self):
        self.assertIn('private_key', release.privacy_findings(b'-----BEGIN OPENSSH PRIVATE KEY-----'))
        self.assertIn('credential', release.privacy_findings(b'ghp_' + b'a'*36))

    def test_extra_private_strings_are_checked_without_embedding_them_in_the_tool(self):
        self.assertEqual(release.privacy_findings(b'fixture-private-host'), [])
        self.assertIn('configured_private_value', release.privacy_findings(b'fixture-private-host', ['fixture-private-host']))
