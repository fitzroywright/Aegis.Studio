#!/usr/bin/env python3
"""Requeue a named user's legacy attachment-only WorkDay photo imports.
Run only after deploying the Studio gallery/message importer. Preview is the default.
No photo bytes, credentials, password hashes or enrollment data are printed.
"""
import argparse
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import uuid


def legacy_rows(connection, user, limit):
    rows = connection.execute("""
        SELECT operation_id, result,
               json_extract(payload, '$.assignmentId') AS assignment_id,
               json_extract(payload, '$.fileName') AS file_name,
               json_extract(payload, '$.contentType') AS content_type
        FROM workday_updates
        WHERE user_key=? AND kind='AssignmentUpload' AND state='Acknowledged'
          AND json_extract(result, '$.result.versionId') IS NULL
        ORDER BY sequence LIMIT ?
    """, (user.strip().lower(), limit)).fetchall()
    selected = []
    for row in rows:
        try:
            result = json.loads(row["result"] or "{}").get("result") or {}
            operation = uuid.UUID(row["operation_id"])
            assignment = uuid.UUID(row["assignment_id"])
            if uuid.UUID(result["uploadId"]) != operation:
                continue
            if uuid.UUID(result["assignmentId"]) != assignment:
                continue
            uuid.UUID(result["attachmentId"])
            if result.get("versionId") or result.get("messageId"):
                continue
            if row["content_type"] not in ("image/jpeg", "image/png", "image/webp"):
                continue
        except (ValueError, TypeError, KeyError, AttributeError):
            continue
        selected.append(row)
    return selected


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path, required=True)
    parser.add_argument("--user", required=True, help="Exact WorkDay username to recover")
    parser.add_argument("--limit", type=int, default=100)
    parser.add_argument("--apply", action="store_true", help="Back up the database and requeue the displayed uploads")
    args = parser.parse_args(argv)
    if not args.user.strip() or not 1 <= args.limit <= 1000:
        parser.error("Specify a username and a limit between 1 and 1000.")
    database = args.database.resolve(strict=True)
    if not database.is_file():
        parser.error("The database must be an existing file.")
    mode = "rw" if args.apply else "ro"
    with closing(sqlite3.connect(database.as_uri() + "?mode=" + mode, uri=True, timeout=30)) as connection:
        connection.row_factory = sqlite3.Row
        rows = legacy_rows(connection, args.user, args.limit)
        print(f"{len(rows)} legacy photograph uploads selected for {args.user.strip()}.")
        for row in rows:
            print(json.dumps({"uploadId": row["operation_id"], "fileName": row["file_name"]}, ensure_ascii=True))
        if not args.apply:
            print("Preview only. No data changed.")
            return 0
        if not rows:
            print("Nothing to requeue.")
            return 0
        descriptor, backup_name = tempfile.mkstemp(prefix=database.name + ".before-workday-gallery-", suffix=".sqlite", dir=database.parent)
        os.close(descriptor)
        try:
            with closing(sqlite3.connect(backup_name)) as backup:
                connection.backup(backup)
            os.chmod(backup_name, 0o600)
        except BaseException:
            Path(backup_name).unlink(missing_ok=True)
            raise
        connection.execute("BEGIN IMMEDIATE")
        changed = 0
        for row in rows:
            # Compare the result as well as state: never overwrite a newer acknowledgment.
            changed += connection.execute("""
                UPDATE workday_updates SET state='Pending', processed_at=NULL
                WHERE operation_id=? AND user_key=? AND kind='AssignmentUpload'
                  AND state='Acknowledged' AND result=?
            """, (row["operation_id"], args.user.strip().lower(), row["result"])).rowcount
        connection.commit()
        print(f"Backup saved: {backup_name}")
        print(f"Requeued {changed} uploads. Payloads, upload IDs and previous results were preserved.")
        print("Verify Studio imports and the new acknowledgment's assetId, versionId and messageId before repeating.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
