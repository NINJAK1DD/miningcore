#!/usr/bin/env python3
"""Extract a hash-verified old runtime into a new test-only directory; never change PATH."""
import argparse
import hashlib
import http.client
from pathlib import Path
import shutil
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
    "linux-x64": ("tar.gz", "7b20d4164f097e1cf616d8174b6a64be100e6f30dc4e028b16e551e8b0c86dff0ef424bb4bf52d1ff0ecca38e102d3fa80814e06cb85a32d6eee4c3b99764f79"),
    "win-x64": ("zip", "cf71f882366ea544beda44c812a7b26bc571fb3f0c5102249e615b9ee953fe5c2c5ee9198cda55f499ad55284bac7748abe35dd4478e528b94b71bc09d9403af"),
}


def download_verified(url, archive, expected):
    for attempt in range(3):
        try:
            # Opening with wb discards a partial response before every retry.
            with urllib.request.urlopen(url, timeout=120) as response, archive.open("wb") as output:
                shutil.copyfileobj(response, output)
            break
        except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.IncompleteRead) as error:
            if isinstance(error, urllib.error.HTTPError) and error.code not in (408, 429, 500, 502, 503, 504):
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--rid", choices=FIXTURES, required=True)
    args = parser.parse_args()
    destination = args.destination.resolve()
    if destination.exists():
        parser.error("fixture destination must not already exist")
    extension, expected = FIXTURES[args.rid]
    url = ("https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.13/"
           f"aspnetcore-runtime-10.0.13-{args.rid}.{extension}")
    with tempfile.TemporaryDirectory(prefix="runtime-fixture-download-") as temporary:
        archive = Path(temporary) / ("runtime." + extension)
        download_verified(url, archive, expected)
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
        if not (destination / "shared" / framework / "10.0.13").is_dir():
            raise ValueError(f"Runtime fixture is missing {framework}")
    print(f"Isolated negative runtime fixture: {destination}; build toolchain and PATH unchanged")


if __name__ == "__main__":
    main()
