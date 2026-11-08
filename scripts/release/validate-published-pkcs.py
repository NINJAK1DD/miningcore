#!/usr/bin/env python3
"""Check the PKCS runtime asset against the reviewed direct package reference."""

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

PACKAGE = "System.Security.Cryptography.Pkcs"
ASSEMBLY = PACKAGE + ".dll"


def validate(publish_dir: Path, project: Path) -> str:
    versions = [node.get("Version") for node in ET.parse(project).iter("PackageReference")
                if node.get("Include") == PACKAGE]
    if len(versions) != 1 or not re.fullmatch(r"10\.0\.\d+", versions[0] or ""):
        raise ValueError("expected one explicit stable PKCS 10.0.x package reference")
    version = versions[0]
    if int(version.split(".")[2]) < 13:
        raise ValueError("PKCS reference is below the 10.0.13 security floor")

    manifest = json.loads((publish_dir / "Miningcore.deps.json").read_text(encoding="utf-8"))
    entry = f"{PACKAGE}/{version}"
    libraries = manifest["libraries"]
    entries = [key for key in libraries if key.startswith(PACKAGE + "/")]
    if entries != [entry] or libraries[entry].get("type") != "package":
        raise ValueError(f"published libraries must resolve exactly {entry}")
    target = manifest["targets"][manifest["runtimeTarget"]["name"]]
    target_entries = [key for key in target if key.startswith(PACKAGE + "/")]
    if target_entries != [entry]:
        raise ValueError(f"active runtime target must resolve exactly {entry}")
    runtime = target[entry].get("runtime", {})
    if not any(Path(asset).name == ASSEMBLY for asset in runtime):
        raise ValueError(f"active runtime target has no {ASSEMBLY} runtime asset")
    if not (publish_dir / ASSEMBLY).is_file():
        raise ValueError(f"published assembly is missing: {ASSEMBLY}")
    return entry


def main() -> int:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <publish-directory> <Miningcore.csproj>", file=sys.stderr)
        return 2
    try:
        entry = validate(Path(sys.argv[1]), Path(sys.argv[2]))
    except (OSError, ValueError, KeyError, TypeError, AttributeError, ET.ParseError) as error:
        print(f"Published PKCS validation failed: {error}", file=sys.stderr)
        return 1
    print(f"Published PKCS dependency verified: {entry}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
