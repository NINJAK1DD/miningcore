#!/usr/bin/env python3
import copy
import contextlib
import importlib.util
import io
import json
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("pause", Path(__file__).with_name("verify-payout-pause-support.py"))
pause = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pause)
SCHEMA = {"definitions": {"ClusterPaymentProcessingConfig": {
    "properties": {"paused": {"type": "boolean", "default": False}},
    "additionalProperties": False}}}
STATE = {"configuration": {"paymentProcessing": {"enabled": True, "paused": True}}}


class PauseSupportTests(unittest.TestCase):
    def test_cli_reports_malformed_schema_nodes_without_tracebacks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            binary, config = root / "Miningcore", root / "config.json"
            binary.touch()
            config.touch()
            for node in (None, [], "private-schema-value", 1):
                (root / "config.schema.json").write_text(json.dumps({"definitions": {
                    "ClusterPaymentProcessingConfig": node}}), encoding="utf-8")
                result = subprocess.run([sys.executable, str(Path(pause.__file__)),
                    "--binary", str(binary), "--config", str(config), "--all-payout-nodes-stopped"],
                    capture_output=True, text=True, check=False)
                with self.subTest(node=node):
                    self.assertEqual(78, result.returncode)
                    self.assertIn("check FAILED", result.stdout)
                    self.assertNotIn("private-schema-value", result.stdout)
                    self.assertEqual("", result.stderr)

    def test_attribute_error_is_redacted_and_requires_operator(self):
        output = io.StringIO()
        with mock.patch("sys.argv", ["verify", "--binary", "unused", "--config", "unused",
                                     "--all-payout-nodes-stopped"]), \
                mock.patch.object(pause, "verify", side_effect=AttributeError("private-schema-value")), \
                contextlib.redirect_stdout(output):
            self.assertEqual(78, pause.main())
        self.assertIn("check FAILED", output.getvalue())
        self.assertNotIn("private-schema-value", output.getvalue())

    def test_current_contract(self):
        pause.validate(SCHEMA, STATE)

    def test_legacy_schema_or_coercion_cannot_certify_pause(self):
        for value in (None, {}, {"type": "string"}, {"type": ["boolean", "null"]},
                      {"type": "boolean", "default": True}):
            schema = copy.deepcopy(SCHEMA)
            schema["definitions"]["ClusterPaymentProcessingConfig"]["properties"]["paused"] = value
            with self.subTest(value=value), self.assertRaises((ValueError, TypeError, AttributeError)):
                pause.validate(schema, STATE)

    def test_future_flags_must_fail_closed(self):
        schema = copy.deepcopy(SCHEMA)
        del schema["definitions"]["ClusterPaymentProcessingConfig"]["additionalProperties"]
        with self.assertRaises(ValueError):
            pause.validate(schema, STATE)

    def test_unpaused_or_coerced_configuration_refused(self):
        for field in ("enabled", "paused"):
            for value in (False, None, "true", 1):
                state = copy.deepcopy(STATE)
                state["configuration"]["paymentProcessing"][field] = value
                with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                    pause.validate(SCHEMA, state)

    def test_exact_binary_and_config_used_without_service_start(self):
        with tempfile.TemporaryDirectory() as directory:
            binary, config = Path(directory) / "Miningcore", Path(directory) / "config.json"
            binary.touch()
            config.touch()
            (Path(directory) / "config.schema.json").write_text(json.dumps(SCHEMA), encoding="utf-8")
            calls = []
            def run(argv, **options):
                self.assertTrue(options["check"])
                self.assertEqual(60, options["timeout"])
                calls.append(argv)
                if argv[1] == "--generate-config-schema":
                    Path(argv[2]).write_text(json.dumps(SCHEMA), encoding="utf-8")
                    return subprocess.CompletedProcess(argv, 0, "")
                return subprocess.CompletedProcess(argv, 0, json.dumps(STATE))
            pause.verify(binary, config, run)
            self.assertEqual([str(binary.resolve())] * 2, [call[0] for call in calls])
            self.assertEqual(["--dumpconfig", "-c", str(config.resolve())], calls[1][1:])
            def failed(*args, **kwargs):
                raise subprocess.CalledProcessError(1, args[0])
            with self.assertRaises(subprocess.CalledProcessError):
                pause.verify(binary, config, failed)

    def test_installed_schema_is_mandatory_and_matches_generated_schema(self):
        with tempfile.TemporaryDirectory() as directory:
            binary, config = Path(directory) / "Miningcore", Path(directory) / "config.json"
            binary.touch()
            config.touch()
            installed = Path(directory) / "config.schema.json"
            def run(argv, **options):
                if argv[1] == "--generate-config-schema":
                    Path(argv[2]).write_text(json.dumps(SCHEMA), encoding="utf-8")
                    return subprocess.CompletedProcess(argv, 0, "")
                return subprocess.CompletedProcess(argv, 0, json.dumps(STATE))
            with self.assertRaises(OSError):
                pause.verify(binary, config, run)
            for invalid in ("not json", "{}", json.dumps({"definitions": {
                    "ClusterPaymentProcessingConfig": {"properties": {"paused": {"type": "boolean", "default": False}}}}})):
                installed.write_text(invalid, encoding="utf-8")
                with self.subTest(invalid=invalid), self.assertRaises((ValueError, KeyError)):
                    pause.verify(binary, config, run)
            changed = copy.deepcopy(SCHEMA)
            changed["description"] = "stale installation"
            installed.write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "differs"):
                pause.verify(binary, config, run)
            installed.write_text(json.dumps(SCHEMA), encoding="utf-8")
            self.assertEqual(hashlib.sha256(b"").hexdigest(), pause.verify(binary, config, run)["apphost"]["sha256"])

    def test_changes_during_verification_are_rejected(self):
        for target in ("Miningcore", "config.json", "config.schema.json"):
            with self.subTest(target=target), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                binary, config = root / "Miningcore", root / "config.json"
                binary.touch()
                config.touch()
                (root / "config.schema.json").write_text(json.dumps(SCHEMA), encoding="utf-8")
                def run(argv, **options):
                    if argv[1] == "--generate-config-schema":
                        Path(argv[2]).write_text(json.dumps(SCHEMA), encoding="utf-8")
                        return subprocess.CompletedProcess(argv, 0, "")
                    with (root / target).open("a", encoding="utf-8") as changed:
                        changed.write(" ")
                    return subprocess.CompletedProcess(argv, 0, json.dumps(STATE))
                with self.assertRaisesRegex(ValueError, "changed during"):
                    pause.verify(binary, config, run)

    def test_receipt_is_private_and_never_overwrites_files(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "receipt.json"
            fingerprints = {"apphost": {"sha256": "test-evidence"}}
            pause.write_receipt(path, fingerprints)
            self.assertEqual(fingerprints, json.loads(path.read_text())["checked_files"])
            if os.name != "nt":
                self.assertEqual(0o600, path.stat().st_mode & 0o777)
            original = path.read_bytes()
            with self.assertRaises(FileExistsError):
                pause.write_receipt(path, {})
            self.assertEqual(original, path.read_bytes())


if __name__ == "__main__":
    unittest.main()
