#!/usr/bin/env python3
"""Exercise the release gate with valid and broken published package manifests."""

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("validate-published-pkcs.py")
PACKAGE = "System.Security.Cryptography.Pkcs"
ASSEMBLY = PACKAGE + ".dll"


class PublishedPkcsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.project = self.root / "Miningcore.csproj"
        self.project.write_text(
            f'<Project><ItemGroup><PackageReference Include="{PACKAGE}" Version="10.0.14" />'
            '<PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="10.0.14" />'
            '</ItemGroup></Project>', encoding="utf-8")
        self.entry = PACKAGE + "/10.0.14"
        self.target = ".NETCoreApp,Version=v10.0/linux-x64"
        self.manifest = {
            "runtimeTarget": {"name": self.target},
            "targets": {self.target: {self.entry: {"runtime": {f"lib/net10.0/{ASSEMBLY}": {}}}}},
            "libraries": {self.entry: {"type": "package"}},
        }
        self.config = {"runtimeOptions": {"frameworks": [
            {"name": "Microsoft.NETCore.App", "version": "10.0.14"},
            {"name": "Microsoft.AspNetCore.App", "version": "10.0.14"}]}}
        self.write_config()
        (self.root / ASSEMBLY).touch()

    def run_gate(self, expected=0, message="verified"):
        (self.root / "Miningcore.deps.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        result = subprocess.run([sys.executable, str(SCRIPT), str(self.root), str(self.project)],
                                capture_output=True, text=True, check=False)
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        self.assertIn(message, result.stdout + result.stderr)

    def test_current_package(self):
        self.run_gate()

    def test_future_servicing_reference(self):
        self.project.write_text(self.project.read_text().replace("10.0.14", "10.0.15"), encoding="utf-8")
        self.manifest = json.loads(json.dumps(self.manifest).replace("10.0.14", "10.0.15"))
        self.run_gate(message=PACKAGE + "/10.0.15")

    def test_wrong_resolved_version(self):
        self.manifest = json.loads(json.dumps(self.manifest).replace("10.0.14", "10.0.13"))
        self.run_gate(1, "must resolve exactly")

    def test_duplicate_resolved_version(self):
        self.manifest["libraries"][PACKAGE + "/10.0.12"] = {"type": "package"}
        self.run_gate(1, "must resolve exactly")

    def test_package_missing(self):
        self.manifest["libraries"].clear()
        self.run_gate(1, "must resolve exactly")

    def test_wrong_library_type(self):
        self.manifest["libraries"][self.entry]["type"] = "project"
        self.run_gate(1, "must resolve exactly")

    def test_inactive_target_does_not_count(self):
        self.manifest["targets"]["inactive"] = self.manifest["targets"][self.target]
        self.manifest["targets"][self.target] = {}
        self.run_gate(1, "active runtime target must resolve")

    def test_missing_runtime_asset(self):
        self.manifest["targets"][self.target][self.entry] = {"compile": {ASSEMBLY: {}}}
        self.run_gate(1, "no " + ASSEMBLY)

    def test_missing_assembly(self):
        (self.root / ASSEMBLY).unlink()
        self.run_gate(1, "assembly is missing")

    def test_vulnerable_reference(self):
        self.project.write_text(self.project.read_text().replace("10.0.14", "10.0.12"), encoding="utf-8")
        self.run_gate(1, "security floor")

    def test_floating_reference(self):
        self.project.write_text(self.project.read_text().replace("10.0.14", "10.0.*"), encoding="utf-8")
        self.run_gate(1, "explicit stable")

    def test_invalid_manifest(self):
        self.manifest = {"runtimeTarget": None, "libraries": {self.entry: {"type": "package"}}}
        self.run_gate(1, "validation failed")

    def framework_manifest(self):
        self.manifest["libraries"].clear()
        self.manifest["targets"][self.target].clear()
        (self.root / ASSEMBLY).unlink()
        self.config = {"runtimeOptions": {"frameworks": [
            {"name": "Microsoft.NETCore.App", "version": "10.0.14"},
            {"name": "Microsoft.AspNetCore.App", "version": "10.0.14"}]}}
        self.write_config()

    def write_config(self):
        (self.root / "Miningcore.runtimeconfig.json").write_text(json.dumps(self.config), encoding="utf-8")

    def test_framework_supplied_package(self):
        self.framework_manifest()
        self.run_gate(message="10.0.14 framework")

    def test_framework_without_aspnet_framework(self):
        self.framework_manifest()
        self.config["runtimeOptions"]["frameworks"].pop()
        self.write_config()
        self.run_gate(1, "without an ASP.NET 10")

    def test_framework_with_untracked_dll(self):
        self.framework_manifest()
        (self.root / ASSEMBLY).touch()
        self.run_gate(1, "untracked PKCS DLL")

    def test_framework_with_wrong_framework_band(self):
        self.framework_manifest()
        self.config["runtimeOptions"]["frameworks"][1]["version"] = "9.0.0"
        self.write_config()
        self.run_gate(1, "explicit stable")

    def test_below_servicing_baseline_in_both_modes(self):
        for mode in ("package", "framework"):
            with self.subTest(mode=mode):
                if mode == "framework":
                    self.framework_manifest()
                self.project.write_text(self.project.read_text().replace(
                    'Include="Microsoft.Extensions.Caching.Memory" Version="10.0.14"',
                    'Include="Microsoft.Extensions.Caching.Memory" Version="10.0.15"'))
                self.run_gate(1, "servicing baseline")

    def test_framework_config_below_pkcs_reference(self):
        self.framework_manifest()
        self.project.write_text(self.project.read_text().replace("10.0.14", "10.0.15"))
        self.run_gate(1, "MiningcoreRuntimeMinimum in src/Miningcore/RuntimeDependencyPolicy.props")

    def test_old_runtime_floors_in_both_modes(self):
        for mode in ("package", "framework"):
            if mode == "framework":
                self.framework_manifest()
            for index in (0, 1):
                with self.subTest(mode=mode, index=index):
                    self.config["runtimeOptions"]["frameworks"][index]["version"] = "10.0.13"
                    self.write_config()
                    self.run_gate(1, "runtime policy floor")
                    self.config["runtimeOptions"]["frameworks"][index]["version"] = "10.0.14"

    def test_singular_aspnet_framework(self):
        self.framework_manifest()
        self.config["runtimeOptions"] = {"framework": self.config["runtimeOptions"]["frameworks"][1]}
        self.write_config()
        self.run_gate()

    def test_ambiguous_frameworks_rejected(self):
        self.config["runtimeOptions"]["framework"] = self.config["runtimeOptions"]["frameworks"][0]
        self.write_config()
        self.run_gate(1, "both framework and frameworks")

    def test_highest_extensions_version_wins(self):
        self.project.write_text(self.project.read_text().replace('</ItemGroup>',
            '<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.15" /></ItemGroup>'))
        self.run_gate(1, "servicing baseline")

    def test_baseline_ignores_other_bands_and_prereleases(self):
        for version in ("9.0.99", "10.1.0", "11.0.0", "10.0.99-preview.1"):
            with self.subTest(version=version):
                original = self.project.read_text()
                self.project.write_text(original.replace('</ItemGroup>',
                    '<PackageReference Include="Microsoft.Extensions.Configuration.Json" '
                    f'Version="{version}" /></ItemGroup>'))
                self.run_gate()
                self.project.write_text(original)

    def test_baseline_requires_at_least_one_eligible_reference(self):
        self.project.write_text(self.project.read_text().replace(
            'Include="Microsoft.Extensions.Caching.Memory" Version="10.0.14"',
            'Include="Microsoft.Extensions.Caching.Memory" Version="10.1.0"'))
        self.run_gate(1, "baseline")

    def test_wrapper_reports_invalid_manifest_without_traceback(self):
        (self.root / "Miningcore.deps.json").write_text('{broken')
        result = subprocess.run([sys.executable, str(SCRIPT.with_name("run-managed-dependency-smoke.py")),
                                 str(self.root)], capture_output=True, text=True, check=False)
        self.assertEqual(1, result.returncode)
        self.assertIn("Published PKCS validation failed:", result.stderr)
        self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
