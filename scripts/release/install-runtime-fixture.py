#!/usr/bin/env python3
"""Extract a hash-verified old runtime into a new test-only directory; never change PATH."""
import argparse
import hashlib
import http.client
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

# SHA-512 values from Microsoft's .NET 10 release metadata. These deliberately old
# binaries are negative fixtures, not deployment or build prerequisites.
# https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
FIXTURES = {
    "linux-x64": ("tar.gz", "dd024fd2616926a555b75cbf7a836f6dce998199f0596d9d75676dca2b9d59c8ee8673c6ac23473c0f1a4bdd59eb5cd02fe9ab097eeaf2dd8c8bd117c85e7031"),
    "win-x64": ("zip", "f26712cbc7a0c3de6c78c78d2ec80f5a96e3da97f3ce67e964a66bc2d8638cf02f67113c28be5431b2739b29732d918bc3210064b52c845fe914e56fcd058acb"),
}
MAX_ARCHIVE_BYTES = 128 * 1024 * 1024
DOWNLOAD_DEADLINE_SECONDS = 300


def download_verified(url, archive, expected):
    for attempt in range(3):
        # File open and close/flush failures are outside the network retry boundary.
        # Opening with wb discards a partial response before every retry.
        with archive.open("wb") as output:
            try:
                with urllib.request.urlopen(url, timeout=120) as response:
                    size = 0
                    while chunk := response.read(min(64 * 1024, MAX_ARCHIVE_BYTES - size + 1)):
                        size += len(chunk)
                        if size > MAX_ARCHIVE_BYTES:
                            raise ValueError("Runtime fixture exceeds the 128 MiB archive limit")
                        try:
                            output.write(chunk)
                        except OSError as error:
                            raise ValueError(f"Unable to write runtime fixture archive: {error}") from error
                break
            except (OSError, http.client.HTTPException) as error:
                if isinstance(error, urllib.error.HTTPError):
                    error.close()
                    if error.code not in (408, 429, 500, 502, 503, 504):
                        raise
                if attempt == 2:
                    raise
        delay = 2 ** (attempt + 1)
        print(f"Runtime fixture download interrupted; retrying in {delay}s ({attempt + 2}/3)", flush=True)
        time.sleep(delay)
    # Integrity failures are final: never retry or extract an unverified archive.
    with archive.open("rb") as content:
        actual = hashlib.file_digest(content, "sha512").hexdigest()
    if actual != expected:
        raise ValueError("Runtime fixture SHA-512 does not match Microsoft release metadata")


def run_download_worker(archive, rid):
    # A separate process makes the deadline effective even during DNS, TLS, or a
    # slow response body. subprocess.run kills and waits for it before cleanup.
    try:
        subprocess.run([sys.executable, str(Path(__file__).resolve()), str(archive),
                        "--rid", rid, "--download-only"], check=True,
                       timeout=DOWNLOAD_DEADLINE_SECONDS,
                       creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    except subprocess.TimeoutExpired:
        raise ValueError("Runtime fixture exceeded the 300-second total download deadline") from None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--rid", choices=FIXTURES, required=True)
    parser.add_argument("--download-only", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    destination = args.destination.resolve()
    if destination.exists():
        parser.error("fixture destination must not already exist")
    extension, expected = FIXTURES[args.rid]
    url = ("https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.14/"
           f"aspnetcore-runtime-10.0.14-{args.rid}.{extension}")
    if args.download_only:
        download_verified(url, destination, expected)
        return
    with tempfile.TemporaryDirectory(prefix="runtime-fixture-download-") as temporary:
        archive = Path(temporary) / ("runtime." + extension)
        run_download_worker(archive, args.rid)
        destination.mkdir(parents=True)
        if extension == "zip":
            with zipfile.ZipFile(archive) as package:
                for member in package.namelist():
                    if not (destination / member).resolve().is_relative_to(destination):
                        raise ValueError("Runtime archive contains an unsafe path")
                package.extractall(destination)
        else:
            with tarfile.open(archive) as package:
                package.extractall(destination, filter="data")
    for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
        if not (destination / "shared" / framework / "10.0.14").is_dir():
            raise ValueError(f"Runtime fixture is missing {framework}")
    print(f"Isolated negative runtime fixture: {destination}; build toolchain and PATH unchanged")


if __name__ == "__main__":
    try:
        main()
    except (OSError, http.client.HTTPException, ValueError, subprocess.CalledProcessError) as error:
        print(f"Runtime fixture installation failed: {error}", file=sys.stderr)
        sys.exit(1)
