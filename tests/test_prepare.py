"""Synthetic tests only: no vendor software, network, clipboard or GUI."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch as mock_patch
import shutil

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from prepare import apply_bytes, build, relative_path
from ui_resources import update_tag


def patch(offset='0x2', before='6364', after='5859'):
    return dict(offset=offset, before=before, after=after)


class BytePatchTests(unittest.TestCase):
    def test_exact_patch_and_reverse(self):
        original = b'abcdef'
        self.assertEqual(apply_bytes(original, [patch()]), b'abXYef')
        self.assertEqual(apply_bytes(b'abXYef', [patch(before='5859', after='6364')]), original)
        self.assertEqual(original, b'abcdef')

    def test_wrong_source_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'input bytes differ'):
            apply_bytes(b'ab12ef', [patch()])

    def test_size_and_bounds_are_rejected(self):
        for row in [patch(after='58'), patch(offset='-1'), patch(offset='0x10'), patch(before='', after='')]:
            with self.subTest(row=row), self.assertRaises(ValueError):
                apply_bytes(b'abcdef', [row])

    def test_overlapping_edits_rejected_before_mutation(self):
        with self.assertRaisesRegex(ValueError, 'Overlapping'):
            apply_bytes(b'abcdef', [patch(), patch(offset='3', before='6465')])

    def test_unsorted_nonoverlapping_edits(self):
        self.assertEqual(apply_bytes(b'abcdef', [patch(offset='4', before='6566'), patch(offset='0', before='6162')]), b'XYcdXY')


class PathTests(unittest.TestCase):
    def test_normal_relative_path(self):
        self.assertEqual(relative_path('EN-US/language.dll'), Path('EN-US/language.dll'))

    def test_escape_and_alias_forms_rejected(self):
        for value in ['', '.', '/absolute', '../parent', 'a/../b', 'a//b', 'a/./b',
                      'C:/Windows/a', 'C:a', 'a\\b', '//host/share']:
            with self.subTest(path=value), self.assertRaises(ValueError):
                relative_path(value)

    def test_windows_filename_aliases_rejected(self):
        for value in ['sample.dat.', 'sample.dat ', 'folder./sample.dat', 'NUL', 'NUL.txt',
                      'con', 'conin$', 'COM1.exe', 'LPT9.txt', 'COM¹.txt', 'a?b', 'a\x00b']:
            with self.subTest(path=value), self.assertRaisesRegex(ValueError, 'Windows-aliased'):
                relative_path(value)


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.miui = self.root / 'vendor'
        self.runtime = self.root / 'runtime'
        self.miui.mkdir()
        self.runtime.mkdir()
        (self.miui / 'sample.dat').write_bytes(b'abcdef')
        self.output = self.root / 'package'
        self.profile = self.root / 'profile.json'
        self.row = dict(path='sample.dat', source='miui', bytes=6,
                        sourceSha256=hashlib.sha256(b'abcdef').hexdigest(),
                        outputSha256=hashlib.sha256(b'abXYef').hexdigest(),
                        uiTransform=False, patches=[patch()])

    def write_profile(self, rows=None):
        self.profile.write_text(json.dumps(dict(schema=1, name='synthetic', files=rows if rows is not None else [self.row])), encoding='utf-8')

    def test_success_preserves_input(self):
        self.write_profile()
        result = build(self.profile, self.miui, self.runtime, self.output)
        self.assertFalse(result['vendorCodeExecuted'])
        self.assertEqual((self.output / 'app/sample.dat').read_bytes(), b'abXYef')
        self.assertEqual((self.miui / 'sample.dat').read_bytes(), b'abcdef')

    def test_wrong_input_hash_does_not_create_output(self):
        self.write_profile()
        (self.miui / 'sample.dat').write_bytes(b'012345')
        with self.assertRaisesRegex(ValueError, 'Source version/hash mismatch'):
            build(self.profile, self.miui, self.runtime, self.output)
        self.assertFalse(self.output.exists())
        self.assertFalse(list(self.root.glob('.package.building-*')))

    def test_existing_output_is_not_modified(self):
        self.write_profile()
        self.output.mkdir()
        marker = self.output / 'keep.txt'
        marker.write_text('keep', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Output already exists'):
            build(self.profile, self.miui, self.runtime, self.output)
        self.assertEqual(marker.read_text(encoding='utf-8'), 'keep')

    def test_nested_output_is_rejected(self):
        self.write_profile()
        with self.assertRaisesRegex(ValueError, 'must be separate'):
            build(self.profile, self.miui, self.runtime, self.miui / 'nested')

    def test_duplicate_case_aliases_rejected(self):
        self.write_profile([self.row, dict(self.row, path='SAMPLE.DAT')])
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            build(self.profile, self.miui, self.runtime, self.output)

    def test_output_hash_mismatch_never_publishes_package(self):
        self.row['outputSha256'] = '0' * 64
        self.write_profile()
        with self.assertRaisesRegex(ValueError, 'Output mismatch'):
            build(self.profile, self.miui, self.runtime, self.output)
        self.assertFalse(self.output.exists())
        self.assertEqual((self.miui / 'sample.dat').read_bytes(), b'abcdef')

    def test_unknown_media_transform_rejected_before_creation(self):
        self.row['mediaTransform'] = 'unreviewed-transform'
        self.write_profile()
        with self.assertRaisesRegex(ValueError, 'Unknown media transform'):
            build(self.profile, self.miui, self.runtime, self.output)
        self.assertFalse(list(self.root.glob('.package.building-*')))

    def test_media_transform_cannot_target_another_file(self):
        self.row['mediaTransform'] = 'background-video-v1'
        self.write_profile()
        with self.assertRaisesRegex(ValueError, 'Unexpected media transform target'):
            build(self.profile, self.miui, self.runtime, self.output)
        self.assertFalse(list(self.root.glob('.package.building-*')))

    def test_wrong_output_size_never_publishes_package(self):
        self.row['outputBytes'] = 7
        self.write_profile()
        with self.assertRaisesRegex(ValueError, 'Output size mismatch'):
            build(self.profile, self.miui, self.runtime, self.output)
        self.assertFalse(self.output.exists())

    def test_later_corruption_of_earlier_output_is_rejected(self):
        (self.miui / 'other.dat').write_bytes(b'abcdef')
        self.write_profile([self.row, dict(self.row, path='other.dat', patches=[],
                                          outputSha256=hashlib.sha256(b'abcdef').hexdigest())])
        copy = shutil.copyfile
        def changing_copy(source, target):
            result = copy(source, target)
            if target.name == 'other.dat':
                (target.parent / 'sample.dat').write_bytes(b'broken')
            return result
        with mock_patch('prepare.shutil.copyfile', side_effect=changing_copy):
            with self.assertRaisesRegex(ValueError, 'Output changed before publication'):
                build(self.profile, self.miui, self.runtime, self.output)
        self.assertFalse(self.output.exists())


class ResourceTests(unittest.TestCase):
    def test_single_control_update(self):
        text = '<Box><Button name="example" visible="true" /></Box>'
        self.assertEqual(update_tag(text, 'example', {'visible': 'false'}),
                         '<Box><Button name="example" visible="false" /></Box>')

    def test_ambiguous_or_missing_control_refused(self):
        for text in ['<Box/>', '<Box><Button name="x"/><Button name="x"/></Box>']:
            with self.assertRaisesRegex(RuntimeError, 'exactly one'):
                update_tag(text, 'x', {'visible': 'false'})


if __name__ == '__main__':
    unittest.main()
