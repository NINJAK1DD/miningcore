#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 SOURCE_ROOT SHA256_MANIFEST" >&2
  exit 64
fi

source_root=$1
manifest=$2

if [[ ! -d "$source_root" || ! -f "$manifest" || ! -r "$manifest" ]]; then
  echo "Pinned source verification could not read its inputs" >&2
  exit 70
fi

verified=0

while IFS= read -r entry || [[ -n "$entry" ]]; do
  if [[ ! "$entry" =~ ^([a-f0-9]{64})[[:space:]][[:space:]]([A-Za-z0-9._/-]+)$ ]]; then
    echo "Pinned source manifest contains a malformed entry" >&2
    exit 70
  fi

  expected=${BASH_REMATCH[1]}
  relative_path=${BASH_REMATCH[2]}

  if [[ "$relative_path" == /* || "/$relative_path/" == *"/../"* ]]; then
    echo "Pinned source manifest contains an unsafe path" >&2
    exit 70
  fi

  source_file="$source_root/$relative_path"

  if [[ ! -f "$source_file" || -L "$source_file" ]]; then
    echo "Pinned source file is missing or is not a regular file: $relative_path" >&2
    exit 1
  fi

  actual=$(sha256sum -- "$source_file" | awk '{print $1}')

  if [[ "$actual" != "$expected" ]]; then
    echo "Pinned source file identity mismatch: $relative_path" >&2
    exit 1
  fi

  verified=$((verified + 1))
done < "$manifest"

if [[ "$verified" -eq 0 ]]; then
  echo "Pinned source manifest contains no files" >&2
  exit 70
fi

echo "Pinned source identity verified for $verified file(s)"
