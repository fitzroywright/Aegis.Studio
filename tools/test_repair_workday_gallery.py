import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location("repair_workday_gallery", Path(__file__).with_name("repair-workday-gallery.py"))
repair = importlib.util.module_from_spec(spec)
spec.loader.exec_module(repair)


class RecoveryTests(unittest.TestCase):
    def test_preview_backup_and_replay_preserve_bytes_and_only_target_legacy_uploads(self):
        with tempfile.TemporaryDirectory() as directory:
            database = Path(directory) / "api.sqlite"
            rows = []
            with sqlite3.connect(database) as connection:
                connection.execute("""CREATE TABLE workday_updates(
                    sequence INTEGER PRIMARY KEY,operation_id TEXT,user_key TEXT,kind TEXT,
                    payload TEXT,state TEXT,result TEXT,processed_at TEXT)""")
                for sequence, (user, state, modern) in enumerate([
                    ("demo.jason", "Acknowledged", False), ("demo.anna", "Acknowledged", False),
                    ("demo.jason", "Conflict", False), ("demo.jason", "Acknowledged", True),
                    ("demo.jason", "Pending", False)], 1):
                    upload, assignment, attachment = map(str, (uuid.uuid4(), uuid.uuid4(), uuid.uuid4()))
                    payload = json.dumps({"assignmentId": assignment, "uploadId": upload, "fileName": "photo.jpg",
                                          "contentType": "image/jpeg", "contentBase64": "SECRET_PHOTO_BYTES"})
                    result = {"result": {"uploadId": upload, "assignmentId": assignment, "attachmentId": attachment}}
                    if modern:
                        result["result"].update(versionId=str(uuid.uuid4()), messageId=str(uuid.uuid4()))
                    encoded = json.dumps(result)
                    rows.append((sequence, upload, user, "AssignmentUpload", payload, state, encoded, "original-time"))
                connection.executemany("INSERT INTO workday_updates VALUES(?,?,?,?,?,?,?,?)", rows)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(repair.main(["--database", str(database), "--user", "demo.jason"]), 0)
            self.assertIn("Preview only", output.getvalue())
            self.assertNotIn("SECRET_PHOTO_BYTES", output.getvalue())
            with sqlite3.connect(database) as connection:
                self.assertEqual(connection.execute("SELECT * FROM workday_updates").fetchall(), rows)
            with contextlib.redirect_stdout(io.StringIO()):
                repair.main(["--database", str(database), "--user", "demo.jason", "--apply"])
            backups = list(Path(directory).glob("*.before-workday-gallery-*.sqlite"))
            self.assertEqual(len(backups), 1)
            self.assertEqual(backups[0].stat().st_mode & 0o777, 0o600)
            with sqlite3.connect(backups[0]) as backup:
                self.assertEqual(backup.execute("SELECT * FROM workday_updates").fetchall(), rows)
            with sqlite3.connect(database) as connection:
                after = connection.execute("SELECT * FROM workday_updates ORDER BY sequence").fetchall()
            self.assertEqual(after[0][5], "Pending")
            self.assertIsNone(after[0][7])
            self.assertEqual(after[0][4], rows[0][4])
            self.assertEqual(after[0][6], rows[0][6])
            self.assertEqual(after[1:], rows[1:])
            with contextlib.redirect_stdout(io.StringIO()):
                repair.main(["--database", str(database), "--user", "demo.jason", "--apply"])
            self.assertEqual(len(list(Path(directory).glob("*.before-workday-gallery-*.sqlite"))), 1)


if __name__ == "__main__":
    unittest.main()
