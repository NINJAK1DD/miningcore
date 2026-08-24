#!/usr/bin/env python3

"""Validate Miningcore's managed/native and native/provider symbol contracts."""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import stat
import subprocess
import sys
from collections import defaultdict


STRUCTURAL_EXIT = 70
LIBRARY_PATTERN = re.compile(r"lib[A-Za-z0-9._+-]+\.so\Z")
SYMBOL_PATTERN = re.compile(r"[^\s\x00-\x1f\x7f]+\Z")
ATTRIBUTE_PATTERN = re.compile(
    r"\[\s*(?:(?:System\.)?Runtime\.InteropServices\.)?"
    r"(?P<kind>DllImport|LibraryImport)(?:Attribute)?\s*\("
)
IMPORT_TOKEN_PATTERN = re.compile(r"\b(?:DllImport|LibraryImport)(?:Attribute)?\b")
ENTRY_POINT_PATTERN = re.compile(
    r"\bEntryPoint\s*=\s*(?P<literal>@?\"(?:\"\"|\\.|[^\"])*\")",
    re.DOTALL,
)
METHOD_PATTERN = re.compile(
    r"\b(?:extern|partial)\b[^;{}]*?\b(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
    re.DOTALL,
)
UNDEFINED_PATTERN = re.compile(
    r"^\s*undefined symbol:\s*(?P<symbol>\S+)\s+\((?P<object>[^()]*)\)\s*$"
)


class ContractError(Exception):
    pass


def regular_readable_file(path: pathlib.Path, description: str) -> None:
    try:
        mode = path.lstat().st_mode
    except OSError as error:
        raise ContractError(f"Unable to inspect {description}: {error}") from error

    if not stat.S_ISREG(mode) or not os.access(path, os.R_OK):
        raise ContractError(f"{description} must be a readable regular file: {path}")


def read_text(path: pathlib.Path, description: str) -> str:
    regular_readable_file(path, description)

    try:
        return path.read_bytes().decode("utf-8")
    except (OSError, UnicodeError) as error:
        raise ContractError(f"Unable to read {description}: {error}") from error


def load_inventory(path: pathlib.Path) -> list[str]:
    content = read_text(path, "native-library inventory")
    if "\r" in content:
        if re.search(r"\r(?!\n)|(?<!\r)\n", content):
            raise ContractError("Native-library inventory uses mixed or invalid line endings")
        content = content.replace("\r\n", "\n")
    libraries = content.split("\n")
    if libraries and libraries[-1] == "":
        libraries.pop()

    if not libraries:
        raise ContractError("Native-library inventory is empty")

    for line_number, library in enumerate(libraries, 1):
        if not library or library != library.strip() or not LIBRARY_PATTERN.fullmatch(library):
            raise ContractError(
                f"Invalid native-library inventory entry on line {line_number}: {library!r}"
            )

    if len(set(libraries)) != len(libraries):
        raise ContractError("Native-library inventory contains duplicate entries")

    if libraries != sorted(libraries):
        raise ContractError("Native-library inventory must use deterministic lexical ordering")

    return libraries


def mask_comments(source: str, path: pathlib.Path) -> str:
    result = list(source)
    index = 0
    state = "normal"

    while index < len(source):
        current = source[index]
        following = source[index + 1] if index + 1 < len(source) else ""

        if state == "normal":
            if current == "/" and following == "/":
                result[index] = result[index + 1] = " "
                state = "line-comment"
                index += 2
                continue
            if current == "/" and following == "*":
                result[index] = result[index + 1] = " "
                state = "block-comment"
                index += 2
                continue
            if current == "@" and following == '"':
                state = "verbatim-string"
                index += 2
                continue
            if current == '"':
                state = "string"
            elif current == "'":
                state = "character"
            index += 1
            continue

        if state == "line-comment":
            if current in "\r\n":
                state = "normal"
            else:
                result[index] = " "
            index += 1
            continue

        if state == "block-comment":
            if current == "*" and following == "/":
                result[index] = result[index + 1] = " "
                state = "normal"
                index += 2
                continue
            if current not in "\r\n":
                result[index] = " "
            index += 1
            continue

        if state == "string":
            if current == "\\":
                index += 2
                continue
            if current == '"':
                state = "normal"
            index += 1
            continue

        if state == "verbatim-string":
            if current == '"' and following == '"':
                index += 2
                continue
            if current == '"':
                state = "normal"
            index += 1
            continue

        if state == "character":
            if current == "\\":
                index += 2
                continue
            if current == "'":
                state = "normal"
            index += 1

    if state == "block-comment":
        raise ContractError(f"Unterminated block comment in managed wrapper: {path}")

    return "".join(result)


def find_closing_parenthesis(source: str, opening: int, path: pathlib.Path) -> int:
    depth = 0
    index = opening
    state = "normal"

    while index < len(source):
        current = source[index]
        following = source[index + 1] if index + 1 < len(source) else ""

        if state == "normal":
            if current == "@" and following == '"':
                state = "verbatim-string"
                index += 2
                continue
            if current == '"':
                state = "string"
            elif current == "'":
                state = "character"
            elif current == "(":
                depth += 1
            elif current == ")":
                depth -= 1
                if depth == 0:
                    return index
            index += 1
            continue

        if state in {"string", "character"}:
            quote = '"' if state == "string" else "'"
            if current == "\\":
                index += 2
                continue
            if current == quote:
                state = "normal"
            index += 1
            continue

        if current == '"' and following == '"':
            index += 2
            continue
        if current == '"':
            state = "normal"
        index += 1

    raise ContractError(f"Unterminated native import attribute in {path}")


def decode_csharp_string(literal: str, path: pathlib.Path) -> str:
    if literal.startswith('@"'):
        return literal[2:-1].replace('""', '"')

    try:
        return json.loads(literal)
    except json.JSONDecodeError as error:
        raise ContractError(f"Unsupported string literal in native import in {path}") from error


def parse_imports(path: pathlib.Path) -> list[tuple[str, str]]:
    source = read_text(path, "managed native wrapper")
    masked = mask_comments(source, path)
    token_count = len(IMPORT_TOKEN_PATTERN.findall(masked))
    imports: list[tuple[str, str]] = []
    search_index = 0

    while match := ATTRIBUTE_PATTERN.search(masked, search_index):
        opening = match.end() - 1
        closing = find_closing_parenthesis(masked, opening, path)
        arguments = masked[opening + 1 : closing]
        library_match = re.match(
            r'\s*(?P<literal>@?\"(?:\"\"|\\.|[^\"])*\")', arguments, re.DOTALL
        )

        if library_match is None:
            raise ContractError(f"Native import library must be a string literal in {path}")

        library = decode_csharp_string(library_match.group("literal"), path)
        entry_matches = list(ENTRY_POINT_PATTERN.finditer(arguments))
        if len(entry_matches) > 1:
            raise ContractError(f"Native import contains multiple EntryPoint values in {path}")

        if entry_matches:
            symbol = decode_csharp_string(entry_matches[0].group("literal"), path)
        else:
            declaration_end = masked.find(";", closing + 1)
            if declaration_end == -1:
                raise ContractError(f"Native import has no terminating declaration in {path}")
            declaration = masked[closing + 1 : declaration_end + 1]
            method_match = METHOD_PATTERN.search(declaration)
            if method_match is None:
                raise ContractError(
                    f"Unable to derive the default native entry point after an import in {path}"
                )
            symbol = method_match.group("name")

        if not library or not symbol or not SYMBOL_PATTERN.fullmatch(symbol):
            raise ContractError(f"Invalid managed native import in {path}: {library!r}, {symbol!r}")

        imports.append((library, symbol))
        search_index = closing + 1

    if len(imports) != token_count:
        raise ContractError(
            f"Managed native import parsing was incomplete in {path}: "
            f"found {token_count} tokens but parsed {len(imports)} attributes"
        )

    return imports


def discover_managed_contracts(
    source_dir: pathlib.Path, inventory: set[str]
) -> dict[str, set[str]]:
    try:
        source_mode = source_dir.lstat().st_mode
    except OSError as error:
        raise ContractError(
            f"Unable to inspect managed native-wrapper directory: {error}"
        ) from error

    if not stat.S_ISDIR(source_mode):
        raise ContractError(f"Managed native-wrapper path is not a directory: {source_dir}")

    try:
        source_files = sorted(source_dir.rglob("*.cs"))
    except OSError as error:
        raise ContractError(
            f"Unable to enumerate managed native-wrapper sources: {error}"
        ) from error
    if not source_files:
        raise ContractError("No managed native-wrapper sources were found")

    libraries_to_files: dict[str, set[pathlib.Path]] = defaultdict(set)
    exports: dict[str, set[str]] = defaultdict(set)

    for source_file in source_files:
        imports = parse_imports(source_file)
        file_libraries = {library for library, _ in imports}

        if len(file_libraries) > 1:
            listed = ", ".join(sorted(file_libraries))
            raise ContractError(
                f"Managed wrapper maps ambiguously to multiple libraries: {source_file}: {listed}"
            )

        for managed_library, symbol in imports:
            native_library = f"{managed_library}.so"
            if native_library not in inventory:
                raise ContractError(
                    f"Managed wrapper references a library outside the reviewed inventory: "
                    f"{source_file}: {managed_library}"
                )
            libraries_to_files[native_library].add(source_file)
            exports[native_library].add(symbol)

    for library in sorted(inventory):
        files = libraries_to_files.get(library, set())
        if not files:
            raise ContractError(f"No managed native wrapper maps to {library}")
        if len(files) != 1:
            listed = ", ".join(str(path) for path in sorted(files))
            raise ContractError(f"Multiple managed wrappers map to {library}: {listed}")

    return exports


def run_tool(command: list[str], description: str) -> str:
    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="strict",
            env={**os.environ, "LC_ALL": "C"},
            timeout=30,
        )
    except subprocess.TimeoutExpired as error:
        raise ContractError(f"{description} exceeded the 30-second inspection limit") from error
    except (OSError, UnicodeError) as error:
        raise ContractError(f"Unable to run {description}: {error}") from error

    output = result.stdout + result.stderr
    if result.returncode != 0:
        bounded = output[:4096]
        detail = f"; diagnostic={json.dumps(bounded)}" if bounded else ""
        raise ContractError(f"{description} failed with status {result.returncode}{detail}")

    return output


def load_exceptions(path: pathlib.Path | None, inventory: set[str]) -> dict[tuple[str, str], dict]:
    if path is None:
        return {}

    content = read_text(path, "native-symbol exception manifest")
    try:
        document = json.loads(content)
    except json.JSONDecodeError as error:
        raise ContractError(f"Native-symbol exception manifest is invalid JSON: {error}") from error

    if not isinstance(document, list):
        raise ContractError("Native-symbol exception manifest must contain a JSON array")

    expected_fields = {"library", "symbol", "consumer", "rationale"}
    exceptions: dict[tuple[str, str], dict] = {}

    for index, entry in enumerate(document):
        if not isinstance(entry, dict) or set(entry) != expected_fields:
            raise ContractError(
                f"Native-symbol exception {index} must contain exactly: "
                "library, symbol, consumer, rationale"
            )

        if not all(isinstance(entry[field], str) and entry[field] for field in expected_fields):
            raise ContractError(
                f"Native-symbol exception {index} contains an empty or non-string field"
            )

        library = entry["library"]
        symbol = entry["symbol"]
        if library not in inventory or not SYMBOL_PATTERN.fullmatch(symbol):
            raise ContractError(f"Native-symbol exception {index} has an invalid library or symbol")

        for field in ("consumer", "rationale"):
            value = entry[field]
            if value != value.strip() or any(
                ord(character) < 32 or ord(character) == 127 for character in value
            ):
                raise ContractError(f"Native-symbol exception {index} has invalid {field} text")

        key = (library, symbol)
        if key in exceptions:
            raise ContractError(f"Duplicate native-symbol exception: {library}: {symbol}")
        exceptions[key] = entry

    return exceptions


def validate_library(
    library: str,
    publish_dir: pathlib.Path,
    expected_exports: set[str],
    ldd_tool: str,
    nm_tool: str,
    exceptions: dict[tuple[str, str], dict],
    observed_exceptions: set[tuple[str, str]],
) -> None:
    library_path = publish_dir / library
    regular_readable_file(library_path, f"published native library {library}")

    relocation_output = run_tool(
        [ldd_tool, "-r", str(library_path)], f"dynamic relocation inspection for {library}"
    )
    unresolved: set[str] = set()

    for line in relocation_output.splitlines():
        if "not found" in line:
            raise ContractError(f"{library} has an unresolved provider: {line.strip()}")
        if "undefined symbol:" not in line:
            continue
        match = UNDEFINED_PATTERN.fullmatch(line)
        if match is None:
            raise ContractError(
                f"Unrecognized unresolved-symbol diagnostic for {library}: {line!r}"
            )
        unresolved.add(match.group("symbol"))

    for symbol in sorted(unresolved):
        key = (library, symbol)
        if key not in exceptions:
            raise ContractError(f"{library} contains an unapproved unresolved symbol: {symbol}")
        observed_exceptions.add(key)

    export_output = run_tool(
        [nm_tool, "-D", "--defined-only", "--format=posix", str(library_path)],
        f"export inspection for {library}",
    )
    actual_exports: set[str] = set()
    for line in export_output.splitlines():
        if not line.strip():
            continue
        fields = line.split()
        if (
            len(fields) < 2
            or not SYMBOL_PATTERN.fullmatch(fields[0])
            or len(fields[1]) != 1
        ):
            raise ContractError(f"Unrecognized export diagnostic for {library}: {line!r}")
        actual_exports.add(fields[0])

    for symbol in sorted(expected_exports - actual_exports):
        raise ContractError(f"{library} does not export managed entry point: {symbol}")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("publish_directory", type=pathlib.Path)
    parser.add_argument("managed_source_directory", type=pathlib.Path)
    parser.add_argument("inventory", type=pathlib.Path)
    parser.add_argument("--exceptions", type=pathlib.Path)
    parser.add_argument("--ldd", default=os.environ.get("MININGCORE_LDD", "ldd"))
    parser.add_argument("--nm", default=os.environ.get("MININGCORE_NM", "nm"))
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()

    try:
        libraries = load_inventory(arguments.inventory)
        inventory = set(libraries)
        expected_exports = discover_managed_contracts(arguments.managed_source_directory, inventory)
        exceptions = load_exceptions(arguments.exceptions, inventory)
        observed_exceptions: set[tuple[str, str]] = set()

        for library in libraries:
            validate_library(
                library,
                arguments.publish_directory,
                expected_exports[library],
                arguments.ldd,
                arguments.nm,
                exceptions,
                observed_exceptions,
            )

        stale_exceptions = set(exceptions) - observed_exceptions
        if stale_exceptions:
            details = ", ".join(
                f"{library}: {symbol}" for library, symbol in sorted(stale_exceptions)
            )
            raise ContractError(
                f"Native-symbol exception manifest contains stale entries: {details}"
            )

    except ContractError as error:
        print(f"Native symbol contract validation failed: {error}", file=sys.stderr)
        return STRUCTURAL_EXIT

    import_count = sum(len(symbols) for symbols in expected_exports.values())
    print(
        f"Validated {import_count} managed entry points and dynamic relocations "
        f"for {len(libraries)} Linux native libraries"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
