"""Build a local candidate from exact user-supplied vendor files. No target execution."""
import argparse
import hashlib
import json
import re
from pathlib import Path, PurePosixPath
import shutil
import stat
import uuid

from ui_resources import patch_file

ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def relative_path(text):
    require(isinstance(text, str) and text and '\\' not in text and ':' not in text,
            'Expected a relative forward-slash path')
    path = PurePosixPath(text)
    require(path.parts and not path.is_absolute() and '..' not in path.parts and '.' not in path.parts
            and path.as_posix() == text, 'Unsafe or noncanonical profile path: ' + text)
    reserved = {'CON', 'PRN', 'AUX', 'NUL', 'CONIN$', 'CONOUT$', 'CLOCK$'}
    for part in path.parts:
        stem = part.split('.', 1)[0].upper()
        require(not part.endswith(('.', ' ')) and not re.search(r'[<>"|?*\x00-\x1f]', part)
                and stem not in reserved and not re.fullmatch(r'(COM|LPT)[1-9¹²³]', stem),
                'Windows-aliased or invalid profile path: ' + text)
    return Path(*path.parts)


def checked_source(root, relative):
    path = root / relative
    require(path.is_file() and path.resolve().is_relative_to(root), 'Missing/outside source: ' + str(relative))
    for part in [path, *path.parents]:
        attributes = getattr(part.stat(), 'st_file_attributes', 0)
        require(not part.is_symlink() and not attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT,
                'Linked input is unsupported')
        if part == root:
            break
    return path


def validate_patch_ranges(data, patches):
    plans = []
    for row in patches:
        offset = int(row['offset'], 0)
        before, after = bytes.fromhex(row['before']), bytes.fromhex(row['after'])
        require(offset >= 0 and len(before) == len(after) > 0 and offset + len(before) <= len(data),
                'Invalid patch range')
        require(data[offset:offset + len(before)] == before, 'Patch input bytes differ at ' + hex(offset))
        plans.append((offset, before, after))
    plans.sort(key=lambda x: x[0])
    require(all(x[0] + len(x[1]) <= y[0] for x, y in zip(plans, plans[1:])), 'Overlapping patches')
    return plans


def apply_bytes(data, patches):
    plans = validate_patch_ranges(data, patches)
    result = bytearray(data)
    for offset, _, after in plans:
        result[offset:offset + len(after)] = after
    return bytes(result)


def build(profile_path, miui_dir, runtime_dir, output):
    profile = json.loads(profile_path.read_text(encoding='utf-8-sig'))
    require(profile['schema'] == 1, 'Unsupported profile schema')
    inputs = {'miui': miui_dir.resolve(strict=True), 'runtime': runtime_dir.resolve(strict=True)}
    require(all(p.is_dir() for p in inputs.values()), 'Input directories are required')
    output = output.resolve()
    require(not output.exists(), 'Output already exists; choose a new directory')
    for source in inputs.values():
        require(not output.is_relative_to(source) and not source.is_relative_to(output),
                'The output and source directories must be separate')
    plans, names = [], set()
    # Validate the complete source set before creating any output.
    for row in profile['files']:
        relative = relative_path(row['path'])
        require(row['source'] in inputs, 'Unknown source group')
        require(row['path'].casefold() not in names, 'Duplicate profile path')
        names.add(row['path'].casefold())
        media_transform = row.get('mediaTransform')
        require(media_transform in (None, 'background-video-v1'), 'Unknown media transform')
        require(media_transform is None or row['path'] == 'MiScreenShare.exe',
                'Unexpected media transform target')
        source = checked_source(inputs[row['source']], relative)
        require(source.stat().st_size == row['bytes'] and sha256(source) == row['sourceSha256'],
                'Source version/hash mismatch: ' + row['path'])
        plans.append((row, relative, source))
    require(plans, 'Empty profile')
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name('.' + output.name + '.building-' + uuid.uuid4().hex)
    temporary.mkdir()
    try:
        for row, relative, source in plans:
            target = temporary / 'app' / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            require(sha256(target) == row['sourceSha256'], 'Source changed during copy: ' + row['path'])
            if row.get('uiTransform'):
                require(row['path'] == 'MiDrop.dll', 'Unknown UI transform target')
                patch_file(target)
            if row['patches']:
                target.write_bytes(apply_bytes(target.read_bytes(), row['patches']))
            if row.get('mediaTransform'):
                from background_video_transform import patch_file as patch_media
                patch_media(target)
            require(target.stat().st_size == row.get('outputBytes', row['bytes']),
                    'Output size mismatch: ' + row['path'])
            require(sha256(target) == row['outputSha256'], 'Output mismatch: ' + row['path'])
        # Recheck the complete assembled tree before publication, including files
        # written early in the build; per-file checks alone miss later aliases.
        for row, relative, _ in plans:
            require(sha256(temporary / 'app' / relative) == row['outputSha256'],
                    'Output changed before publication: ' + row['path'])
        report = dict(profile=profile['name'], files=len(plans), vendorCodeExecuted=False,
                      sourceFilesUntouched=True, phoneTested=False)
        (temporary / 'verification').mkdir()
        (temporary / 'verification/build-files.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        require(not output.exists(), 'Output appeared during the build; refusing to replace it')
        temporary.rename(output)
        return report
    except Exception:
        # Preserve our exact temporary directory for diagnosis; never recursively delete user files.
        print('Unfinished private build retained at: ' + str(temporary))
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--miui-dir', type=Path, required=True)
    parser.add_argument('--runtime-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--profile', type=Path, default=ROOT / 'profiles/miui-2.5.5.643.json')
    args = parser.parse_args()
    print(json.dumps(build(args.profile, args.miui_dir, args.runtime_dir, args.output)))


if __name__ == '__main__':
    main()
