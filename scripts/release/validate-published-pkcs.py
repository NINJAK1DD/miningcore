#!/usr/bin/env python3
"""Check the PKCS runtime asset against the reviewed direct package reference."""

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

PACKAGE = "System.Security.Cryptography.Pkcs"
ASSEMBLY = PACKAGE + ".dll"


POLICY = Path(__file__).resolve().parents[2] / "src/Miningcore/RuntimeDependencyPolicy.props"


def stable_version(value):
    if not isinstance(value, str) or not re.fullmatch(r"10\.0\.\d+", value):
        raise ValueError("expected an explicit stable 10.0.x version")
    return tuple(map(int, value.split(".")))


def runtime_floors(publish_dir):
    config = json.loads((publish_dir / "Miningcore.runtimeconfig.json").read_text(encoding="utf-8"))
    options = config["runtimeOptions"]
    if "framework" in options and "frameworks" in options:
        raise ValueError("runtimeconfig has both framework and frameworks")
    frameworks = options.get("frameworks", [options["framework"]] if "framework" in options else [])
    if not isinstance(frameworks, list):
        raise ValueError("runtimeconfig frameworks must be an array")
    result = {}
    for item in frameworks:
        name = item["name"]
        if name in result:
            raise ValueError("duplicate framework in runtimeconfig")
        result[name] = stable_version(item["version"])
    # A singular ASP.NET entry can delegate Core resolution to its framework config.
    # Its minimum and the live Core check still enforce both layers.
    if "Microsoft.AspNetCore.App" not in result:
        raise ValueError("runtimeconfig is without an ASP.NET 10 shared framework")
    return result


def validate(publish_dir: Path, project: Path) -> tuple[str, str]:
    versions = [node.get("Version") for node in ET.parse(project).iter("PackageReference")
                if node.get("Include") == PACKAGE]
    if len(versions) != 1 or not re.fullmatch(r"10\.0\.\d+", versions[0] or ""):
        raise ValueError("expected one explicit stable PKCS 10.0.x package reference")
    version = versions[0]
    policy = ET.parse(POLICY)
    security_floor = stable_version(policy.findtext(".//MiningcorePkcsSecurityMinimum"))
    runtime_floor = stable_version(policy.findtext(".//MiningcoreRuntimeMinimum"))
    if stable_version(version) < security_floor:
        raise ValueError("PKCS reference is below the security floor")
    extensions = [stable_version(node.get("Version")) for node in ET.parse(project).iter("PackageReference")
                  if (node.get("Include") or "").startswith("Microsoft.Extensions.") and
                  re.fullmatch(r"10\.0\.\d+", node.get("Version") or "")]
    if not extensions:
        raise ValueError("no explicit Microsoft.Extensions servicing baseline")
    if stable_version(version) < max(extensions):
        raise ValueError("PKCS reference is below the Microsoft.Extensions servicing baseline")
    frameworks = runtime_floors(publish_dir)
    if any(value < runtime_floor for value in frameworks.values()):
        raise ValueError("runtimeconfig framework minimum is below the runtime policy floor")

    manifest = json.loads((publish_dir / "Miningcore.deps.json").read_text(encoding="utf-8"))
    entry = f"{PACKAGE}/{version}"
    libraries = manifest["libraries"]
    entries = [key for key in libraries if key.startswith(PACKAGE + "/")]
    target = manifest["targets"][manifest["runtimeTarget"]["name"]]
    target_entries = [key for key in target if key.startswith(PACKAGE + "/")]
    if not entries and not target_entries:
        # SDK publish conflict resolution may omit the package in favor of ASP.NET.
        if frameworks["Microsoft.AspNetCore.App"] < stable_version(version):
            raise ValueError("ASP.NET runtimeconfig minimum is below the PKCS reference")
        if (publish_dir / ASSEMBLY).exists():
            raise ValueError("untracked PKCS DLL present despite framework-only manifest")
        return version, "framework"
    if entries != [entry] or libraries[entry].get("type") != "package":
        raise ValueError(f"published libraries must resolve exactly {entry}")
    if target_entries != [entry]:
        raise ValueError(f"active runtime target must resolve exactly {entry}")
    runtime = target[entry].get("runtime", {})
    if not any(Path(asset).name == ASSEMBLY for asset in runtime):
        raise ValueError(f"active runtime target has no {ASSEMBLY} runtime asset")
    if not (publish_dir / ASSEMBLY).is_file():
        raise ValueError(f"published assembly is missing: {ASSEMBLY}")
    return version, "package"


def main() -> int:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <publish-directory> <Miningcore.csproj>", file=sys.stderr)
        return 2
    try:
        version, source = validate(Path(sys.argv[1]), Path(sys.argv[2]))
    except (OSError, ValueError, KeyError, TypeError, AttributeError, ET.ParseError) as error:
        print(f"Published PKCS validation failed: {error}", file=sys.stderr)
        return 1
    print(f"Published PKCS manifest verified: {source} binding; reference {PACKAGE}/{version}", file=sys.stderr)
    print(version, source)
    return 0


if __name__ == "__main__":
    sys.exit(main())
