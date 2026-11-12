#!/usr/bin/env python3
"""Install pinned upstream CLI tools into an isolated lab directory (never system PATH)."""
import concurrent.futures
import hashlib
from pathlib import Path
import sys
import subprocess
import tarfile
import tempfile
import urllib.request

PACKAGES = [
    ("xelis-1.25.0", "https://github.com/xelis-project/xelis-blockchain/releases/download/v1.25.0/x86_64-unknown-linux-gnu.tar.gz",
     "424ac65de320a835b4cbe2c2a8b9140b12e8bab86cf5ebda89ffee024947135e"),
    ("zcash-6.20.0", "https://apt.z.cash/pool/main/z/zcash/zcash-6.20.0-amd64-bookworm.deb",
     "863595b5567c97502450413d3e4ce633fac69c587db65fc964139fc4f9b60fac"),
    ("verus-1.2.18", "https://github.com/VerusCoin/VerusCoin/releases/download/v1.2.18/Verus-CLI-Linux-v1.2.18-x86_64.tgz",
     "3c8a749a2d18059988bafcff37e72175bb8b2853caa356f820ad1e75c8189cb0"),
    ("monero-0.18.5.4", "https://downloads.getmonero.org/cli/monero-linux-x64-v0.18.5.4.tar.bz2",
     "ba2928962c101b609434bf8c1b43ad59ebe96c5525977284ca599d8bf0f02d10"),
    ("zano-2.2.3.608", "https://build.zano.org/builds/zano-linux-x64-cli-release-v2.2.3.608%5B09c0526%5D.tar.bz2",
     "aee9387a064d4f0964321959c56c944298b01fed877f328ea81ad205caf8eebd"),
    ("conceal-6.7.6", "https://github.com/ConcealNetwork/conceal-core/releases/download/6.7.6/ccx-cli-ubuntu-2604-v6.7.6.tar.gz",
     "1dc8d5e478539d0d10308aa95798cdfa77a519fda98e1ff3d9630f6987b30114"),
]

def install(root, package):
    name, url, digest = package
    target = root / name
    archive = root / (name + ".archive")
    if not archive.exists():
        request = urllib.request.Request(url, headers={"User-Agent": "Miningcore isolated compatibility lab"})
        with urllib.request.urlopen(request, timeout=120) as source, archive.open("xb") as output:
            while chunk := source.read(1024 * 1024):
                output.write(chunk)
    with archive.open("rb") as source:
        actual = hashlib.file_digest(source, "sha256").hexdigest()
    if actual != digest:
        raise RuntimeError(f"Checksum mismatch for {name}; retain the archive for inspection")
    marker = target / ".verified-install"
    if marker.exists() and marker.read_text().strip() == digest:
        print(f"Verified existing {name}: {digest}; {target}", flush=True)
        return
    # A failed extraction must never become an apparently complete installation.
    # Retain older/incomplete directories for inspection instead of deleting them.
    with tempfile.TemporaryDirectory(prefix=name + "-extract-", dir=root) as temporary:
        staging = Path(temporary) / "package"
        staging.mkdir(mode=0o700)
        if url.endswith(".deb"):
            # Extract a verified package; do not install it or execute maintainer scripts.
            subprocess.run(["dpkg-deb", "--extract", str(archive), str(staging)], check=True)
        else:
            with tarfile.open(archive) as source:
                source.extractall(staging, filter="data")
        if name.startswith("verus-"):
            nested = list(staging.glob("*.tar.gz"))
            if len(nested) != 1:
                raise RuntimeError("Unexpected Verus release archive layout")
            with tarfile.open(nested[0]) as source:
                source.extractall(staging, filter="data")
        (staging / ".verified-install").write_text(digest + "\n")
        if target.exists():
            retained = root / (name + ".previous-" + Path(temporary).name)
            target.rename(retained)
        staging.rename(target)
    print(f"Verified {name}: {digest}; {target}", flush=True)

if __name__ == "__main__":
    root = Path(sys.argv[1]).resolve()
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as workers:
        for result in workers.map(lambda package: install(root, package), PACKAGES):
            pass
