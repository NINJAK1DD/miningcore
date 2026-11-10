#!/usr/bin/env python3
"""Offline download-failure and real MSBuild policy regressions (requires the .NET SDK)."""
import copy
import errno
import hashlib
import importlib.util
import io
import http.client
from pathlib import Path
import subprocess
import ssl
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
import urllib.error
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("fixture", HERE / "install-runtime-fixture.py")
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


class DownloadTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.archive = Path(temporary.name) / "runtime.zip"
        self.digest = hashlib.sha512(b"verified archive").hexdigest()
        self.opener = self.enterContext(patch.object(fixture.urllib.request, "urlopen"))
        self.sleep = self.enterContext(patch.object(fixture.time, "sleep"))

    def download(self):
        fixture.download_verified("https://example.invalid/runtime.zip", self.archive, self.digest)

    def test_transient_failures_retry_with_bounded_backoff(self):
        self.opener.side_effect = [urllib.error.URLError("network"),
                                  urllib.error.HTTPError("url", 503, "busy", {}, None),
                                  io.BytesIO(b"verified archive")]
        self.download()
        self.assertEqual([2, 4], [call.args[0] for call in self.sleep.call_args_list])
        self.assertEqual(b"verified archive", self.archive.read_bytes())

    def test_partial_download_is_truncated_before_retry(self):
        class Interrupted(io.BytesIO):
            def read(self, size=-1):
                if self.tell():
                    raise ConnectionResetError("interrupted")
                return super().read(size)
        self.opener.side_effect = [Interrupted(b"partial response longer than the real archive"),
                                  io.BytesIO(b"verified archive")]
        self.download()
        self.assertEqual(b"verified archive", self.archive.read_bytes())

    def test_retries_stop_after_three_attempts(self):
        self.opener.side_effect = TimeoutError("timeout")
        with self.assertRaises(TimeoutError):
            self.download()
        self.assertEqual(3, self.opener.call_count)
        self.assertEqual(2, self.sleep.call_count)

    def test_permanent_http_errors_are_not_retried(self):
        self.opener.side_effect = urllib.error.HTTPError("url", 404, "missing", {}, None)
        with self.assertRaises(urllib.error.HTTPError):
            self.download()
        self.assertEqual(1, self.opener.call_count)
        self.sleep.assert_not_called()

    def test_checksum_failure_is_not_retried(self):
        self.opener.return_value = io.BytesIO(b"untrusted archive")
        with self.assertRaisesRegex(ValueError, "SHA-512"):
            self.download()
        self.assertEqual(1, self.opener.call_count)
        self.sleep.assert_not_called()

    def test_tls_http_protocol_and_io_failures_during_body_are_retried(self):
        for error in (ssl.SSLError("TLS read failed"), http.client.BadStatusLine("invalid status"),
                      OSError("I/O failure"), http.client.RemoteDisconnected("closed")):
            with self.subTest(error=type(error).__name__):
                class Interrupted(io.BytesIO):
                    def read(self, size=-1):
                        raise error
                self.opener.reset_mock()
                self.opener.side_effect = [Interrupted(), io.BytesIO(b"verified archive")]
                self.download()
                self.assertEqual(2, self.opener.call_count)

    def test_size_limit_stops_unbounded_body_without_retry_or_excess_disk_use(self):
        self.opener.return_value = io.BytesIO(b"x" * 65)
        with patch.object(fixture, "MAX_ARCHIVE_BYTES", 64):
            with self.assertRaisesRegex(ValueError, "archive limit"):
                self.download()
        self.assertLessEqual(self.archive.stat().st_size, 64)
        self.assertEqual(1, self.opener.call_count)
        self.sleep.assert_not_called()

    def test_local_open_failure_stops_before_network_access(self):
        with patch.object(Path, "open", side_effect=PermissionError("archive is not writable")):
            with self.assertRaises(PermissionError):
                self.download()
        self.opener.assert_not_called()
        self.sleep.assert_not_called()

    def test_local_write_failure_is_not_retried(self):
        self.opener.return_value = io.BytesIO(b"verified archive")
        with patch.object(Path, "open") as local_open:
            local_open.return_value.__enter__.return_value.write.side_effect = OSError(errno.ENOSPC, "disk full")
            with self.assertRaisesRegex(ValueError, "Unable to write runtime fixture archive"):
                self.download()
        self.assertEqual(1, self.opener.call_count)
        self.sleep.assert_not_called()

    def test_local_close_or_flush_failure_is_not_retried(self):
        self.opener.return_value = io.BytesIO(b"verified archive")
        with patch.object(Path, "open") as local_open:
            local_open.return_value.__exit__.side_effect = OSError(errno.ENOSPC, "flush failed")
            with self.assertRaisesRegex(OSError, "flush failed"):
                self.download()
        self.assertEqual(1, self.opener.call_count)
        self.sleep.assert_not_called()


class WorkerTests(unittest.TestCase):
    def test_total_deadline_terminates_a_stalled_worker(self):
        with tempfile.TemporaryDirectory() as temporary:
            worker = Path(temporary) / "stalled.py"
            worker.write_text("import time\ntime.sleep(30)\n", encoding="utf-8")
            start = time.monotonic()
            with patch.object(fixture, "__file__", str(worker)), \
                    patch.object(fixture, "DOWNLOAD_DEADLINE_SECONDS", 0.2):
                with self.assertRaisesRegex(ValueError, "total download deadline"):
                    fixture.run_download_worker(Path(temporary) / "archive.zip", "win-x64")
            self.assertLess(time.monotonic() - start, 10)

    def test_worker_timeout_or_failure_never_reaches_extraction(self):
        for error in (subprocess.TimeoutExpired("worker", 300), subprocess.CalledProcessError(1, "worker")):
            with self.subTest(error=type(error).__name__), tempfile.TemporaryDirectory() as temporary:
                destination = Path(temporary) / "fixture"
                with patch.object(sys, "argv", ["installer", str(destination), "--rid", "win-x64"]), \
                        patch.object(fixture.subprocess, "run", side_effect=error), \
                        patch.object(fixture.zipfile, "ZipFile") as extract:
                    with self.assertRaises((ValueError, subprocess.CalledProcessError)):
                        fixture.main()
                    extract.assert_not_called()
                    self.assertFalse(destination.exists())


class BuildPolicyTests(unittest.TestCase):
    def invoke(self, versions):
        # Exercise the actual committed target without restoring packages or compiling native code.
        source = ET.parse(HERE.parents[1] / "src/Miningcore/Miningcore.csproj")
        target = source.find("./Target[@Name='EmbedReviewedPkcsMinimum']")
        self.assertIsNotNone(target)
        project = ET.Element("Project")
        items = ET.SubElement(project, "ItemGroup")
        for version in versions:
            ET.SubElement(items, "PackageReference", Include="System.Security.Cryptography.Pkcs", Version=version)
        project.append(copy.deepcopy(target))
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "policy.proj"
            ET.ElementTree(project).write(path, encoding="utf-8")
            return subprocess.run(["dotnet", "msbuild", str(path), "-nologo",
                                   "-t:EmbedReviewedPkcsMinimum", "-getItem:AssemblyMetadata"],
                                  capture_output=True, text=True, timeout=60)

    def test_stable_version_is_embedded(self):
        result = self.invoke(["10.0.15"])
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('"Value": "10.0.15"', result.stdout)

    def test_invalid_version_fails_before_embedding(self):
        for version in ("[10.0.14,)", "10.0.*", "10.0.15-preview.1", "10.1.0", "", "10.0.14\n"):
            with self.subTest(version=version):
                result = self.invoke([version])
                self.assertNotEqual(0, result.returncode)
                self.assertIn("plain stable 10.0.N", result.stdout + result.stderr)

    def test_missing_or_duplicate_reference_fails(self):
        for versions in ([], ["10.0.14", "10.0.15"]):
            result = self.invoke(versions)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Expected one reviewed PKCS", result.stdout + result.stderr)

    def test_actual_sdk_rejects_self_contained_configuration(self):
        project = HERE.parents[1] / "src/Miningcore/Miningcore.csproj"
        for setting in ("SelfContained", "PublishSelfContained"):
            with self.subTest(setting=setting):
                # Supply a valid RID so SDK missing-RID validation cannot mask our policy error.
                result = subprocess.run(["dotnet", "msbuild", str(project), "-nologo",
                                         "-t:ProcessFrameworkReferences", "-p:RuntimeIdentifier=linux-x64",
                                         f"-p:{setting}=true"],
                                        capture_output=True, text=True, timeout=60)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("framework-dependent publishing only", result.stdout + result.stderr)

    def test_publish_guard_also_runs_without_build(self):
        policy = HERE.parents[1] / "src/Miningcore/RuntimeDependencyPolicy.props"
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "publish.proj"
            project = ET.Element("Project")
            ET.SubElement(project, "Import", Project=str(policy))
            ET.SubElement(project, "Target", Name="PrepareForPublish")
            ET.ElementTree(project).write(path, encoding="utf-8")
            for value in ("true", "false"):
                result = subprocess.run(["dotnet", "msbuild", str(path), "-nologo",
                                         "-t:PrepareForPublish", f"-p:SelfContained={value}"],
                                        capture_output=True, text=True, timeout=60)
                self.assertEqual(value == "false", result.returncode == 0, result.stdout + result.stderr)
                if value == "true":
                    self.assertIn("framework-dependent publishing only", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
