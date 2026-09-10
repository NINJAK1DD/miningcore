#!/usr/bin/env python3
"""Exercise snapshot helpers' guards in isolated synthetic repositories.

Linux: run both helpers with Bash, Python 3 and PowerShell 7.
Windows: select PowerShellSnapshotHelperTests; also requires the .NET 10 SDK.
A missing interpreter fails, never skips.
"""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


SENTINEL = b"reviewed fixture must survive failure\n"


class SnapshotHelperContract:
    @classmethod
    def setUpClass(cls):
        if os.name == "nt":
            # Build a real apphost once; .cmd/shebang shims do not exercise the
            # helper's shell-free Windows ProcessStartInfo execution path.
            directory = tempfile.TemporaryDirectory(prefix="miningcore fake dotnet build ")
            cls.addClassCleanup(directory.cleanup)
            cls.windows_shim = Path(directory.name) / "bin"
            project = Path(__file__).parent / "fixtures/ConfigDiagnosticsFakeDotnet/ConfigDiagnosticsFakeDotnet.csproj"
            result = subprocess.run([
                "dotnet", "build", str(project), "--configuration", "Release",
                "--output", str(cls.windows_shim),
                "-p:BaseIntermediateOutputPath=" + str(Path(directory.name) / "obj") + os.sep,
                "--verbosity", "quiet",
            ], capture_output=True, timeout=120, check=False)
            if result.returncode != 0:
                raise RuntimeError("Could not build the Windows test fixture: " +
                                   (result.stdout + result.stderr).decode("utf-8", errors="replace"))

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="miningcore snapshot tests ")
        self.addCleanup(self.directory.cleanup)
        # Windows runner TEMP may use an 8.3 alias (e.g. RUNNER~1), whereas
        # PowerShell expands it in PSScriptRoot. Resolve once so exact argv
        # checks compare the same canonical spelling on every platform.
        self.root = Path(self.directory.name).resolve()
        source = Path(__file__).with_name(self.helper_name)
        self.helper = self.root / "scripts/release" / source.name
        self.helper.parent.mkdir(parents=True)
        shutil.copyfile(source, self.helper)
        for configuration in ("Debug", "Release"):
            assembly = self.assembly(configuration)
            assembly.parent.mkdir(parents=True)
            assembly.touch()
        (self.root / "config.example.json").write_text("{}", encoding="utf-8")
        self.snapshot = self.root / "src/Miningcore.Tests/Fixtures/config-diagnostics-v2.json"
        self.snapshot.parent.mkdir(parents=True)
        self.snapshot.write_bytes(SENTINEL)
        self.response = self.root / "response.json"
        self.response.write_bytes(b'{"diagnosticFormatVersion":2,"configuration":{}}\n')
        (self.root / "status").write_text("0", encoding="utf-8")
        self.bin = self.root / "fake-bin"
        self.bin.mkdir()
        if os.name == "nt":
            for artifact in self.windows_shim.iterdir():
                if artifact.is_file():
                    shutil.copyfile(artifact, self.bin / artifact.name)
            return
        dotnet = self.bin / "dotnet"
        dotnet.write_text(
            "#!/usr/bin/env python3\n"
            "import json, pathlib, sys\n"
            "root = pathlib.Path(__file__).resolve().parent.parent\n"
            "(root / 'invoked').touch()\n"
            "(root / 'actual-args.json').write_text(json.dumps(sys.argv[1:]))\n"
            "if sys.argv[1:] != json.loads((root / 'expected-args.json').read_text()):\n"
            "    sys.exit(99)\n"
            "sys.stdout.buffer.write((root / 'response.json').read_bytes())\n"
            "sys.stderr.write('synthetic subprocess diagnostic')\n"
            "sys.exit(int((root / 'status').read_text()))\n",
            encoding="utf-8",
        )
        dotnet.chmod(0o700)

    def assembly(self, configuration):
        return self.root / f"src/Miningcore/bin/{configuration}/net10.0/Miningcore.dll"

    def prepare_run(self, *arguments):
        # Isolate subcases so an unexpected write/invocation is reported once,
        # rather than contaminating every subsequent guard assertion.
        self.snapshot.write_bytes(SENTINEL)
        (self.root / "invoked").unlink(missing_ok=True)
        (self.root / "actual-args.json").unlink(missing_ok=True)
        configuration = arguments[0] if arguments else "Debug"
        (self.root / "expected-args.json").write_text(json.dumps([
            str(self.assembly(configuration)), "--dumpconfig", "-c",
            str(self.root / "config.example.json"),
        ]), encoding="utf-8")

    def run_helper(self, *arguments):
        self.prepare_run(*arguments)
        return subprocess.run(
            [*self.interpreter, str(self.helper), *arguments],
            cwd=self.root.parent,
            env={**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"]},
            capture_output=True, timeout=15, check=False,
        )

    def assert_invoked(self):
        self.assertTrue((self.root / "invoked").exists(), "Synthetic dotnet was not invoked")
        self.assertEqual(
            json.loads((self.root / "expected-args.json").read_text(encoding="utf-8")),
            json.loads((self.root / "actual-args.json").read_text(encoding="utf-8")),
        )

    def assert_preserved(self, result, invoked=True):
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(SENTINEL, self.snapshot.read_bytes())
        self.assertNotIn(b"synthetic subprocess diagnostic", result.stdout + result.stderr)
        if invoked:
            self.assert_invoked()

    def test_synthetic_child_contract(self):
        # Prove the stub itself works before interpreting helper failures.
        # Its input, output and arguments are entirely synthetic and safe to
        # include in an assertion; the real helper still suppresses stderr.
        self.prepare_run()
        arguments = json.loads((self.root / "expected-args.json").read_text(encoding="utf-8"))
        child = self.bin / ("dotnet.exe" if os.name == "nt" else "dotnet")
        result = subprocess.run([str(child), *arguments], capture_output=True,
                                timeout=15, check=False)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assert_invoked()
        self.assertEqual(self.response.read_bytes(), result.stdout)
        self.assertEqual(b"synthetic subprocess diagnostic", result.stderr)
        self.assertEqual(SENTINEL, self.snapshot.read_bytes())

    def test_default_debug_and_release_preserve_format_with_utf8_lf_no_bom(self):
        expected = '{\n  "diagnosticFormatVersion": 2,\n  "notice": "caf\u00e9",\n  "configuration": {}\n}\n'.encode("utf-8")
        for arguments in ((), ("Debug",), ("Release",)):
            for bom in (b"", b"\xef\xbb\xbf"):
                with self.subTest(arguments=arguments, bom=bom):
                    self.response.write_bytes(bom + expected.replace(b"\n", b"\r\n"))
                    result = self.run_helper(*arguments)
                    self.assert_invoked()
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertEqual(expected, self.snapshot.read_bytes())
                    self.assertNotIn(b"synthetic subprocess diagnostic", result.stdout + result.stderr)

    def test_nonzero_exit_never_overwrites_even_valid_json(self):
        (self.root / "status").write_text("1", encoding="utf-8")
        self.assert_preserved(self.run_helper())

    def test_missing_assembly_does_not_invoke_dotnet(self):
        self.assembly("Debug").unlink()
        self.assert_preserved(self.run_helper(), invoked=False)
        self.assertFalse((self.root / "invoked").exists())

    def test_rejects_invalid_arguments_without_invoking_dotnet(self):
        for arguments in (("../Release",), ("Debug", "production.json"), ("--help",), ("",)):
            with self.subTest(arguments=arguments):
                self.assert_preserved(self.run_helper(*arguments), invoked=False)
                self.assertFalse((self.root / "invoked").exists())

    def test_malformed_json_and_encoding_preserve_fixture(self):
        for response in (b"", b"{", b"{} {}", b"\xff",
                         b'{"diagnosticFormatVersion":2,"configuration":{"bad":"\xff"}}'):
            with self.subTest(response=response):
                self.response.write_bytes(response)
                self.assert_preserved(self.run_helper())

    def test_wrong_contract_preserves_fixture(self):
        for document in (
            None, [], {}, [{"diagnosticFormatVersion": 2, "configuration": {}}],
            {"diagnosticFormatVersion": 1, "configuration": {}},
            {"diagnosticFormatVersion": "2", "configuration": {}},
            {"diagnosticFormatVersion": 2.0, "configuration": {}},
            {"diagnosticFormatVersion": True, "configuration": {}},
            {"diagnosticFormatVersion": 2},
            {"diagnosticFormatVersion": 2, "configuration": None},
            {"diagnosticFormatVersion": 2, "configuration": []},
            {"diagnosticFormatVersion": 2, "configuration": [{}]},
            {"diagnosticFormatVersion": 2, "configuration": "not an object"},
        ):
            with self.subTest(document=document):
                self.response.write_text(json.dumps(document), encoding="utf-8")
                self.assert_preserved(self.run_helper())


class BashSnapshotHelperTests(SnapshotHelperContract, unittest.TestCase):
    helper_name = "update-config-diagnostics-snapshot.sh"
    interpreter = ("bash",)


class PowerShellSnapshotHelperTests(SnapshotHelperContract, unittest.TestCase):
    helper_name = "update-config-diagnostics-snapshot.ps1"
    interpreter = ("pwsh", "-NoProfile", "-File")


if __name__ == "__main__":
    unittest.main()
