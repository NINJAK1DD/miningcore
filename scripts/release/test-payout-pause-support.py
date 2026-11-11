#!/usr/bin/env python3
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("pause", Path(__file__).with_name("verify-payout-pause-support.py"))
pause = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pause)
SCHEMA = {"definitions": {"ClusterPaymentProcessingConfig": {
    "properties": {"paused": {"type": "boolean", "default": False}},
    "additionalProperties": False}}}
STATE = {"configuration": {"paymentProcessing": {"enabled": True, "paused": True}}}


class PauseSupportTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
