#!/usr/bin/env python3
"""Linux installer contract: checksums, ownership-safe upgrades and symlink refusal."""
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

INSTALLER = Path(__file__).with_name("install-worker.sh").resolve()
VERSION = "miningcore-pearl-worker 2 d7c517d267f10f5bcbbdc366f3e603942f9a5ab5 linux-x64"


class Installation(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bundle = self.root / "bundle"
        (self.bundle / "licenses").mkdir(parents=True)
        (self.bundle / "licenses/cargo-inventory.json").write_text("[]\n")
        (self.bundle / "pearl-upstream-LICENSE").write_text("installer test fixture\n")
        worker = self.bundle / "miningcore-pearl-worker"
        worker.write_text(f"#!/bin/sh\nprintf '%s\\n' '{VERSION}'\n")
        worker.chmod(0o755)
        manifest = "".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(self.bundle)}\n"
                           for p in sorted(self.bundle.rglob("*")) if p.is_file())
        (self.bundle / "SHA256SUMS").write_text(manifest)
        self.publish = self.root / "publish"
        self.publish.mkdir()

    def install(self, success=True):
        result = subprocess.run(["bash", str(INSTALLER), str(self.bundle), str(self.publish)],
                                capture_output=True, timeout=10)
        self.assertEqual(result.returncode == 0, success)

    def test_read_only_upgrade_and_permissions(self):
        self.install()
        self.install()  # Prior executable is 0555; upgrade must still work.
        for path in (self.publish / "pearl").rglob("*"):
            self.assertEqual(path.stat().st_uid, os.geteuid())
            self.assertEqual(path.stat().st_mode & 0o022, 0)
        self.assertEqual((self.publish / "pearl/miningcore-pearl-worker").stat().st_mode & 0o777, 0o555)

    def test_tampered_bundle_is_rejected(self):
        (self.bundle / "pearl-upstream-LICENSE").write_text("changed")
        self.install(False)
        self.assertFalse((self.publish / "pearl").exists())

    def test_bundle_symlink_is_rejected(self):
        (self.bundle / "link").symlink_to(self.root)
        self.install(False)

    def test_destination_symlink_is_rejected_without_touching_target(self):
        target = self.root / "outside"
        target.mkdir()
        (self.publish / "pearl").symlink_to(target)
        self.install(False)
        self.assertEqual(list(target.iterdir()), [])

    def test_existing_nested_symlink_is_rejected(self):
        self.install()
        (self.publish / "pearl/licenses/link").symlink_to(self.root)
        self.install(False)


if __name__ == "__main__":
    unittest.main()
