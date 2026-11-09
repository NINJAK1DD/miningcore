#!/usr/bin/env python3
"""Prepare a portable probe or run it against an unchanged published application."""
import argparse
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def prepare(application, output):
    spec = importlib.util.spec_from_file_location("pkcs", HERE / "validate-published-pkcs.py")
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
    version, mode = gate.validate(application, ROOT / "src/Miningcore/Miningcore.csproj")
    print(f"Published PKCS manifest verified: {mode} binding; reference {version}", flush=True)
    subprocess.run(["dotnet", "build", str(HERE / "managed-dependency-smoke/Miningcore.ManagedDependencySmoke.csproj"),
                    "-c", "Release", "-o", str(output), "-p:TreatWarningsAsErrors=true",
                    f"-p:MiningcorePublishDir={application}"], check=True)
    (output / "pkcs-binding.txt").write_text(f"{version} {mode}\n", encoding="utf-8")
    shutil.copyfile(HERE / "run-container-dependency-smoke.sh", output / "run-container-dependency-smoke.sh")
    return version, mode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("application", type=Path)
    parser.add_argument("--prepare", type=Path, help="Export probe for a runtime-only container")
    args = parser.parse_args()
    application = args.application.resolve()
    if args.prepare:
        prepare(application, args.prepare.resolve())
        return
    with tempfile.TemporaryDirectory(prefix="managed-dependency-smoke-") as temporary:
        root = Path(temporary)
        version, mode = prepare(application, root / "probe")
        runtime = root / "runtime"
        shutil.copytree(application, runtime)
        assembly = "Miningcore.ManagedDependencySmoke.dll"
        shutil.copyfile(root / "probe" / assembly, runtime / assembly)
        subprocess.run(["dotnet", "exec", "--depsfile", str(runtime / "Miningcore.deps.json"),
                        "--runtimeconfig", str(runtime / "Miningcore.runtimeconfig.json"),
                        str(runtime / assembly), version, mode], check=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, AttributeError, ET.ParseError) as error:
        print(f"Published PKCS validation failed: {error}", file=sys.stderr)
        sys.exit(1)
    except subprocess.CalledProcessError as error:
        print(f"Managed dependency validation failed: command exited {error.returncode}", file=sys.stderr)
        sys.exit(1)
