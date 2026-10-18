import json
import pathlib
import tempfile
import unittest
import zipfile
from unittest import mock

from report import atomic_json
from run import digest, restore_attempt


class AttemptRestoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.attempt = self.root / 'attempt-01'
        self.attempt.mkdir()
        atomic_json(self.attempt / 'started.json', {'status': 'started'})
        (self.attempt / 'publishes.ndjson').write_bytes(b'partial raw\n')
        self.destination = self.root / 'restored' / 'attempt-01'

    def seal(self):
        atomic_json(self.attempt / 'result.json', {'status': 'success'})
        atomic_json(self.attempt / 'sha256.json', {p.name: digest(p) for p in self.attempt.iterdir()})
        archive = self.root / 'attempt.zip'
        with zipfile.ZipFile(archive, 'w') as z:
            for p in self.attempt.iterdir():
                z.write(p, p.name)
        atomic_json(self.attempt / 'archive.json', {'archivePath': str(archive), 'sha256': digest(archive)})
        return archive

    def test_interrupted_attempt_is_preserved_but_never_declared_archived(self):
        original = {p.name: p.read_bytes() for p in self.attempt.iterdir()}
        outcome = restore_attempt(self.attempt, self.destination)
        self.assertEqual('interrupted-unsealed', outcome['status'])
        self.assertFalse(outcome['archiveVerified'])
        self.assertEqual(original, {p.name: p.read_bytes() for p in self.attempt.iterdir()})
        self.assertEqual(original, {p.name: p.read_bytes() for p in self.destination.iterdir()})
        self.assertFalse((self.destination / 'sha256.json').exists())

    def test_result_written_before_crash_remains_unsealed(self):
        atomic_json(self.attempt / 'result.json', {'status': 'success'})
        self.assertFalse(restore_attempt(self.attempt, self.destination)['archiveVerified'])

    def test_missing_archive_receipt_of_a_sealed_attempt_is_rejected(self):
        self.seal()
        (self.attempt / 'archive.json').unlink()
        with self.assertRaisesRegex(RuntimeError, 'Sealed attempt'):
            restore_attempt(self.attempt, self.destination)

    def test_verified_archive_is_restored(self):
        self.seal()
        outcome = restore_attempt(self.attempt, self.destination)
        self.assertTrue(outcome['archiveVerified'])
        self.assertEqual(b'partial raw\n', (self.destination / 'publishes.ndjson').read_bytes())

    def test_corrupted_archive_preserves_source_and_rejects_verification(self):
        archive = self.seal()
        archive.write_bytes(b'corrupted')
        with self.assertRaisesRegex(RuntimeError, 'Archive digest'):
            restore_attempt(self.attempt, self.destination)
        self.assertTrue((self.attempt / 'publishes.ndjson').exists())

    def test_modified_interruption_is_not_accepted_as_a_consistent_snapshot(self):
        import shutil
        original_copy = shutil.copytree
        def changed(source, destination):
            original_copy(source, destination)
            (source / 'publishes.ndjson').write_bytes(b'concurrent writer')
        with mock.patch('run.shutil.copytree', side_effect=changed):
            with self.assertRaisesRegex(RuntimeError, 'changed during snapshot'):
                restore_attempt(self.attempt, self.destination)
        self.assertTrue(self.attempt.exists())

    def test_missing_start_is_rejected(self):
        (self.attempt / 'started.json').unlink()
        with self.assertRaisesRegex(RuntimeError, 'started.json'):
            restore_attempt(self.attempt, self.destination)

    def test_archive_with_valid_receipt_but_wrong_sealed_content_is_rejected(self):
        archive = self.seal()
        with zipfile.ZipFile(archive, 'w') as z:
            for p in self.attempt.iterdir():
                if p.name != 'archive.json':
                    z.writestr(p.name, b'changed' if p.name == 'publishes.ndjson' else p.read_bytes())
        atomic_json(self.attempt / 'archive.json', {'archivePath': str(archive), 'sha256': digest(archive)})
        with self.assertRaisesRegex(RuntimeError, 'seal mismatch'):
            restore_attempt(self.attempt, self.destination)

    def test_archive_cannot_extract_outside_restore_directory(self):
        archive = self.seal()
        with zipfile.ZipFile(archive, 'a') as z:
            z.writestr('../outside.txt', b'escape')
        atomic_json(self.attempt / 'archive.json', {'archivePath': str(archive), 'sha256': digest(archive)})
        with self.assertRaisesRegex(RuntimeError, 'Unsafe archive'):
            restore_attempt(self.attempt, self.destination)
        self.assertFalse((self.destination.parent / 'outside.txt').exists())


if __name__ == '__main__':
    unittest.main()
