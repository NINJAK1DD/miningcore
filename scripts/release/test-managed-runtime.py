#!/usr/bin/env python3
"""Real-host binding and old-framework rejection tests; never runs mining services.

Requires installed ASP.NET 10.0.14 as a negative fixture, plus the serviced host.
Only temporary copies/links are changed. The older framework is never deployed.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
PKCS = "System.Security.Cryptography.Pkcs"


def run(command, expected=None, contains=None, env=None):
    result = subprocess.run(list(map(str, command)), text=True, capture_output=True, env=env, timeout=60)
    output = result.stdout + result.stderr
    if (expected is not None and expected != 1 and result.returncode != expected or
            expected == 1 and result.returncode == 0 or
            contains and contains not in output):
        raise AssertionError(f"Unexpected process result {result.returncode}: {output}")
    print(output.strip())
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("application", type=Path)
    parser.add_argument("--old-aspnet", type=Path, required=True)
    args = parser.parse_args()
    app = args.application.resolve()
    old = args.old_aspnet.resolve()
    if old.name != "10.0.14" or not (old / (PKCS + ".dll")).is_file():
        raise ValueError("Expected the real ASP.NET 10.0.14 negative fixture")
    dotnet = Path(shutil.which("dotnet")).resolve()
    spec = importlib.util.spec_from_file_location("smoke", HERE / "run-managed-dependency-smoke.py")
    smoke = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(smoke)
    with tempfile.TemporaryDirectory(prefix="managed-runtime-tests-") as temporary:
        root = Path(temporary)
        version, mode = smoke.prepare(app, root / "probe")
        runtime = root / "app"
        shutil.copytree(app, runtime)
        assembly = runtime / "Miningcore.ManagedDependencySmoke.dll"
        shutil.copyfile(root / "probe" / assembly.name, assembly)
        deps = runtime / "Miningcore.deps.json"
        config = runtime / "Miningcore.runtimeconfig.json"

        def command(host=dotnet, source=mode, reference=version):
            return [host, "exec", "--depsfile", deps, "--runtimeconfig", config, assembly, reference, source]

        # Current release, then actual application-local PKCS (not just a mocked path).
        output = run(command(), 0, "PKCS binding accepted")
        selected = next(line.split(" at ", 1)[1] for line in output.splitlines()
                        if line.startswith("Selected Microsoft.AspNetCore.App:"))
        current_aspnet = Path(selected).parent
        current_core = Path(next(line.split(" at ", 1)[1] for line in output.splitlines()
                                 if line.startswith("Selected Microsoft.NETCore.App:")))
        manifest = json.loads(deps.read_text())
        original_manifest = deps.read_text()
        original_config = config.read_text()

        def package_fixture(package_version, dll):
            entry = PKCS + "/" + package_version
            manifest["libraries"] = {k: v for k, v in manifest["libraries"].items() if not k.startswith(PKCS + "/")}
            manifest["libraries"][entry] = {"type": "package", "serviceable": True, "sha512": ""}
            target = manifest["targets"][manifest["runtimeTarget"]["name"]]
            for key in list(target):
                if key.startswith(PKCS + "/"):
                    del target[key]
            framework_deps = json.loads((dll.parent / "Microsoft.AspNetCore.App.deps.json").read_text())
            assets = [metadata for framework_target in framework_deps["targets"].values()
                      for library in framework_target.values()
                      for name, metadata in library.get("runtime", {}).items()
                      if Path(name).name == PKCS + ".dll"]
            if len(assets) != 1:
                raise ValueError("Could not obtain real PKCS assembly/file versions for the fixture")
            target[entry] = {"runtime": {"lib/net10.0/" + PKCS + ".dll": assets[0]}}
            deps.write_text(json.dumps(manifest))
            shutil.copyfile(dll, runtime / (PKCS + ".dll"))

        # The current local DLL must really load locally; use its real file version in deps.
        package_fixture(version, current_aspnet / (PKCS + ".dll"))
        run(command(source="package"), 0, "PKCS binding accepted: package")
        # Equal framework assets can win host conflict resolution too. To exercise
        # actual app-local loading, make a disposable framework with its PKCS asset
        # at 10.0.14; Core and the ASP.NET marker remain the real serviced binaries.
        isolated = root / "dotnet"
        isolated.mkdir()
        shutil.copy2(dotnet, isolated / dotnet.name)
        shutil.copytree(dotnet.parent / "host", isolated / "host")
        shutil.copytree(current_core, isolated / "shared/Microsoft.NETCore.App" / current_core.name)
        local_framework = isolated / "shared/Microsoft.AspNetCore.App" / current_aspnet.name
        shutil.copytree(current_aspnet, local_framework)
        shutil.copyfile(old / (PKCS + ".dll"), local_framework / (PKCS + ".dll"))
        local_deps = local_framework / "Microsoft.AspNetCore.App.deps.json"
        local_manifest = json.loads(local_deps.read_text())
        old_manifest = json.loads((old / "Microsoft.AspNetCore.App.deps.json").read_text())
        old_asset = next(metadata for target in old_manifest["targets"].values()
                         for library in target.values() for name, metadata in library.get("runtime", {}).items()
                         if Path(name).name == PKCS + ".dll")
        for target in local_manifest["targets"].values():
            for library in target.values():
                for name in library.get("runtime", {}):
                    if Path(name).name == PKCS + ".dll":
                        library["runtime"][name] = old_asset
        local_deps.write_text(json.dumps(local_manifest))
        env = dict(os.environ, DOTNET_ROOT=str(isolated), DOTNET_MULTILEVEL_LOOKUP="0")
        env.pop("DOTNET_ROLL_FORWARD", None)
        run(command(host=isolated / dotnet.name, source="package"), 0,
            str(runtime / (PKCS + ".dll")), env)
        # A newer framework may legitimately replace an older app copy.
        package_fixture("10.0.14", old / (PKCS + ".dll"))
        run(command(source="package", reference="10.0.14"), 0, str(current_aspnet))

        # Serviced Core/ASP.NET markers cannot hide PKCS below the embedded reviewed
        # package minimum. Both the smoke and normal Main must reject it.
        run(command(host=isolated / dotnet.name, source="package", reference="10.0.14"),
            1, f"PKCS 10.0.14 requires {version}", env)
        run([isolated / dotnet.name, runtime / "Miningcore.dll", "--version"],
            78, f"PKCS 10.0.14 requires {version}", env)

        # Isolate host selection: Core stays serviced, but ASP.NET is genuinely old.
        shutil.rmtree(isolated / "shared/Microsoft.AspNetCore.App")
        shutil.copytree(old, isolated / "shared/Microsoft.AspNetCore.App/10.0.14")
        env = dict(os.environ, DOTNET_ROOT=str(isolated), DOTNET_MULTILEVEL_LOOKUP="0")
        env.pop("DOTNET_ROLL_FORWARD", None)
        deps.write_text(original_manifest)
        (runtime / (PKCS + ".dll")).unlink()
        run(command(host=isolated / dotnet.name), 1, "Microsoft.AspNetCore.App", env)

        # Even a lowered runtimeconfig cannot bypass the startup/smoke guard.
        options = json.loads(config.read_text())
        for framework in options["runtimeOptions"]["frameworks"]:
            if framework["name"] == "Microsoft.AspNetCore.App":
                framework["version"] = "10.0.14"
        config.write_text(json.dumps(options))
        run(command(host=isolated / dotnet.name), 1, "Microsoft.AspNetCore.App 10.0.14 requires", env)
        run([isolated / dotnet.name, runtime / "Miningcore.dll", "--version"], 78,
            "Runtime dependency validation failed", env)
        old_core = old.parents[1] / "Microsoft.NETCore.App/10.0.14"
        shutil.rmtree(isolated / "shared/Microsoft.NETCore.App")
        shutil.copytree(old_core, isolated / "shared/Microsoft.NETCore.App/10.0.14")
        for framework in options["runtimeOptions"]["frameworks"]:
            framework["version"] = "10.0.14"
        config.write_text(json.dumps(options))
        run(command(host=isolated / dotnet.name), 1, "Microsoft.NETCore.App 10.0.14 requires", env)
        run([isolated / dotnet.name, runtime / "Miningcore.dll", "--version"], 78,
            "Runtime dependency validation failed", env)

        # An explicit offline diagnostic config may relax the host floor, but only
        # read-only incident verification can proceed; no latch/state is written.
        offline = runtime / "offline.json"
        state = root / "incident-state"
        offline.write_text(json.dumps({"pools": [], "shareRecoveryStateDirectory": str(state)}))
        # Mutation modes must fail at the runtime guard before even trying to read config.
        for option in ("--recover-payout-ownership", "--acknowledge-share-recovery-state"):
            run([isolated / dotnet.name, runtime / "Miningcore.dll", option, "-c", root / "missing.json"],
                78, "Runtime dependency validation failed", env)
        run([isolated / dotnet.name, runtime / "Miningcore.dll", "--recover-payout-ownership", "--version"],
            1, "Use --recover-payout-ownership on its own", env)
        output = run([isolated / dotnet.name, runtime / "Miningcore.dll",
                      "--verify-share-recovery-state", "-c", offline], 0, "RESULT: no incidents found", env)
        if "WARNING: This runtime is not accepted" not in output or state.exists():
            raise AssertionError("Offline verification must warn and leave state untouched")

        # A damaged embedded policy gives an actionable installation error at Main.
        config.write_text(original_config)
        application = runtime / "Miningcore.dll"
        binary = application.read_bytes()
        resource = b"Miningcore.RuntimeDependencyPolicy.props\0"
        if binary.count(resource) != 1:
            raise AssertionError("Expected exactly one embedded runtime-policy resource name")
        application.write_bytes(binary.replace(resource, b"Miningcore.RuntimeDependencyPolicy.broke\0"))
        run([dotnet, application, "--version"], 78,
            "Runtime dependency policy could not be loaded. Reinstall the complete reviewed Miningcore release")
        print("Real runtime tests passed: package binding, newer framework, host floor and startup guard")


if __name__ == "__main__":
    main()
