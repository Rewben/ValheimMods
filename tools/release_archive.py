"""Validate the public ZIP and redact unsigned PE CodeView build paths only."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct
import zipfile

RUNTIME_DLLS = ('ValheimRAFT.dll', 'ValheimVehicles.dll', 'ZdoWatcher.dll',
                'DynamicLocations.dll', 'Zolantris.Shared.dll', 'ServerSync.dll')
PUBLIC_ROOT = {'manifest.json', 'README.md', 'CHANGELOG.md', 'icon.png', 'LICENSE',
               'ServerSync.LICENSE.txt', 'ServerSync.PROVENANCE.md', 'release-metadata.json'}
PATTERNS = {
    'absolute_windows_path': re.compile(r'(?i)(?<![a-z0-9])[a-z]:[\\/](?:(?:Users|Documents and Settings|AIDev)[\\/]|(?:[a-z0-9_. ()-]{1,100}[\\/])+[a-z0-9_. ()-]{1,100})'),
    'home_directory': re.compile(r'/(?:home|Users)/[^/\x00\s]+/|/root/'),
    'private_key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    'credential': re.compile(r'ghp_[a-zA-Z0-9]{30,}|github_pat_[a-zA-Z0-9_]{30,}|AKIA[A-Z0-9]{16}'),
    'telemetry_dsn': re.compile(r'https://[a-fA-F0-9]{20,}@[^\x00\s]*sentry\.io'),
}


def privacy_findings(data, private_values=()):
    # .NET user strings can start at either byte alignment. Also scan PE/PNG/JSON
    # ASCII strings without loading assemblies or executing anything in the ZIP.
    texts = [data.decode('utf-8', errors='replace')]
    for offset in (0, 1):
        part = data[offset:]
        texts.append(part[:len(part) // 2 * 2].decode('utf-16le', errors='replace'))
    findings = [name for name, pattern in PATTERNS.items() if any(pattern.search(text) for text in texts)]
    if any(value.casefold() in text.casefold() for value in private_values if len(value) >= 4 for text in texts):
        findings.append('configured_private_value')
    return sorted(findings)


def redact_codeview(path):
    """Keep every executable/metadata byte intact; replace only the PDB filename.

    Refuse signed files. The original dependency is never modified: the packager
    calls this on its staged copy. CodeView GUID/age and all PE offsets stay intact.
    """
    path = Path(path)
    original = path.read_bytes()
    data = bytearray(original)
    if data[:2] != b'MZ':
        raise ValueError('Not a PE file')
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('Invalid PE header')
    optional = pe + 24
    magic = struct.unpack_from('<H', data, optional)[0]
    if magic not in (0x10b, 0x20b):
        raise ValueError('Unsupported PE optional header')
    directories = optional + (96 if magic == 0x10b else 112)
    if struct.unpack_from('<II', data, directories + 4 * 8) != (0, 0):
        raise ValueError('Refusing to modify an Authenticode-signed PE')
    section_count = struct.unpack_from('<H', data, pe + 6)[0]
    section_table = optional + struct.unpack_from('<H', data, pe + 20)[0]

    def offset(rva):
        for index in range(section_count):
            section = section_table + index * 40
            virtual_size, address, raw_size, raw = struct.unpack_from('<IIII', data, section + 8)
            if address <= rva < address + min(virtual_size or raw_size, raw_size):
                return raw + rva - address
        raise ValueError('PE address is not backed by a file section')

    cor_rva, cor_size = struct.unpack_from('<II', data, directories + 14 * 8)
    if cor_rva and cor_size:
        flags = struct.unpack_from('<I', data, offset(cor_rva) + 16)[0]
        if flags & 8:
            raise ValueError('Refusing to modify a strong-name-signed assembly')
    debug_rva, debug_size = struct.unpack_from('<II', data, directories + 6 * 8)
    changed_ranges = []
    if debug_rva and debug_size:
        if debug_size % 28:
            raise ValueError('Invalid PE debug directory size')
        start = offset(debug_rva)
        for entry in range(start, start + debug_size, 28):
            kind, size, address, pointer = struct.unpack_from('<IIII', data, entry + 12)
            if kind != 2:
                continue
            if size < 25 or pointer + size > len(data) or data[pointer:pointer + 4] != b'RSDS':
                raise ValueError('Unsupported CodeView record')
            filename = (path.stem + '.pdb').encode('ascii') + b'\0'
            left, right = pointer + 24, pointer + size
            if len(filename) > right - left:
                raise ValueError('Neutral PDB filename does not fit')
            data[left:right] = filename.ljust(right - left, b'\0')
            changed_ranges.append((left, right))
    if len(original) != len(data):
        raise ValueError('Redaction changed PE size')
    if any(a != b and not any(lo <= i < hi for lo, hi in changed_ranges)
           for i, (a, b) in enumerate(zip(original, data))):
        raise ValueError('Redaction changed bytes outside CodeView filename')
    path.write_bytes(data)
    return {'file': path.name, 'codeview_records': len(changed_ranges),
            'executable_bytes_unchanged': True,
            'before_sha256': hashlib.sha256(original).hexdigest(),
            'after_sha256': hashlib.sha256(data).hexdigest()}


def validate(path, private_values=()):
    findings = []
    files = {}
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate ZIP paths')
        for entry in archive.infolist():
            name = entry.filename
            parsed = PurePosixPath(name)
            if parsed.is_absolute() or '..' in parsed.parts or '\\' in name or ':' in name:
                raise ValueError('Unsafe ZIP path: ' + name)
            allowed = (name in PUBLIC_ROOT or name in {'plugins/' + dll for dll in RUNTIME_DLLS}
                       or name.startswith('plugins/Assets/'))
            if not allowed or entry.is_dir():
                raise ValueError('Unexpected public archive entry: ' + name)
            if name.startswith('plugins/Assets/') and parsed.suffix.lower() not in ('.png', '.jpg', '.jpeg', '.json'):
                raise ValueError('Unexpected asset type: ' + name)
            data = archive.read(entry)  # Full read checks each entry's ZIP CRC.
            files[name] = hashlib.sha256(data).hexdigest()
            flags = privacy_findings(data, private_values)
            if flags:
                findings.append({'file': name, 'patterns': flags})
        required = {'manifest.json', 'README.md', 'icon.png', 'LICENSE', 'ServerSync.LICENSE.txt',
                    'ServerSync.PROVENANCE.md', 'release-metadata.json'} | {'plugins/' + name for name in RUNTIME_DLLS}
        if not required.issubset(files):
            raise ValueError('Missing required archive entries: ' + ', '.join(sorted(required - files.keys())))
        for group in ('Logos', 'Patterns', 'Sails', 'Translations'):
            if not any(name.startswith('plugins/Assets/' + group + '/') for name in files):
                raise ValueError('Missing asset directory: ' + group)
        manifest = json.loads(archive.read('manifest.json'))
        if manifest['name'] != 'ValheimRAFT_Rewben' or not re.fullmatch(r'\d+\.\d+\.\d+', manifest['version_number']):
            raise ValueError('Invalid Thunderstore manifest identity')
        if manifest['dependencies'] != ['ValheimModding-Jotunn-2.30.2']:
            raise ValueError('Unexpected package dependencies')
        metadata = json.loads(archive.read('release-metadata.json'))
        actual = {name: digest for name, digest in files.items() if name != 'release-metadata.json'}
        if metadata['files'] != actual or metadata['packageVersion'] != manifest['version_number']:
            raise ValueError('Release metadata/checksum mismatch')
    if findings:
        raise ValueError('Privacy scan failed: ' + json.dumps(findings))
    return {'package_version': manifest['version_number'], 'runtime_version': metadata['runtimeVersion'],
            'files': len(files), 'runtime_dlls': len(RUNTIME_DLLS), 'privacy_scan': 'passed',
            'layout': 'explicit plugins directory; Assets hierarchy preserved',
            'zip_sha256': hashlib.sha256(Path(path).read_bytes()).hexdigest()}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['validate', 'redact-codeview'])
    parser.add_argument('path')
    parser.add_argument('--report')
    parser.add_argument('--private-values-file', help='Local JSON array of extra private strings; never copied into the release')
    args = parser.parse_args()
    private_values = json.loads(Path(args.private_values_file).read_text(encoding='utf-8-sig')) if args.private_values_file else []
    result = validate(args.path, private_values) if args.mode == 'validate' else redact_codeview(args.path)
    text = json.dumps(result, indent=2)
    if args.report:
        Path(args.report).write_text(text + '\n', encoding='utf-8')
    print(text)
