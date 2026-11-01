#!/usr/bin/env python3
"""Preserve Cargo's locked dependency inventory and distributed license/notice files."""
import json
import pathlib
import shutil
import sys

metadata = json.load(sys.stdin)
destination = pathlib.Path(sys.argv[1])
destination.mkdir(parents=True, exist_ok=True)
inventory = []
for package in sorted(metadata["packages"], key=lambda item: (item["name"], item["version"])):
    root = pathlib.Path(package["manifest_path"]).parent
    files = {path for pattern in ("LICENSE*", "LICENCE*", "COPYING*", "NOTICE*", "license*", "licence*")
             for path in root.glob(pattern) if path.is_file()}
    if package.get("license_file"):
        files.add(root / package["license_file"])
    target = destination / (package["name"] + "-" + package["version"])
    target.mkdir(exist_ok=True)
    for path in sorted(files):
        shutil.copyfile(path, target / path.name)
    inventory.append({"name": package["name"], "version": package["version"],
                      "license": package.get("license"), "source": package.get("source"),
                      "repository": package.get("repository"), "files": sorted(path.name for path in files)})
(destination / "cargo-inventory.json").write_text(json.dumps(inventory, indent=2) + "\n")
