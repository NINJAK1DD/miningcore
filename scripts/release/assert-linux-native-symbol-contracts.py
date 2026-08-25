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
CONTRACT_EXIT = 1
LIBRARY_PATTERN = re.compile(r"lib[A-Za-z0-9._+-]+\.so\Z")
SYMBOL_PATTERN = re.compile(r"[^\s\x00-\x1f\x7f]+\Z")
ATTRIBUTE_PATTERN = re.compile(
    r"(?:\[\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*:\s*)?|,\s*)"
    r"(?:(?:global\s*::\s*)?[A-Za-z_][A-Za-z0-9_]*\s*(?:\.\s*|::\s*))*"
    r"(?P<kind>DllImport|LibraryImport)(?:Attribute)?\s*\("
)
IMPORT_TOKEN_PATTERN = re.compile(r"\b(?:DllImport|LibraryImport)(?:Attribute)?\b")
IMPORT_LIBRARY_PARAMETER_NAMES = {
    "DllImport": "dllName",
    "LibraryImport": "libraryName",
}
ENTRY_POINT_PATTERN = re.compile(
    r"\s*(?P<literal>@?\"(?:\"\"|\\.|[^\"])*\")",
    re.DOTALL,
)
ENTRY_POINT_ASSIGNMENT_PATTERN = re.compile(r"\bEntryPoint\s*=")
METHOD_PATTERN = re.compile(
    r"\b(?:extern|partial)\b[^;{}]*?\b(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
    re.DOTALL,
)
UNDEFINED_PATTERN = re.compile(
    r"^\s*undefined symbol:\s*(?P<symbol>\S+)\s+\((?P<object>.*)\)\s*$"
)
CALLABLE_SYMBOL_TYPES = {"T", "W", "i"}
GENERATED_DIRECTORY_NAMES = {"bin", "obj"}
IGNORED_TOOLCHAIN_WEAK_SYMBOLS = {
    "_ITM_deregisterTMCloneTable",
    "_ITM_registerTMCloneTable",
    "__cxa_finalize",
    "__cxa_pure_virtual",
    "__gmon_start__",
}


class ContractError(Exception):
    pass


class ContractViolation(ContractError):
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
            if source.startswith('$@"', index) or source.startswith('@$"', index):
                state = "verbatim-string"
                index += 3
                continue
            if current == "$":
                dollar_count = 1
                while (
                    index + dollar_count < len(source)
                    and source[index + dollar_count] == "$"
                ):
                    dollar_count += 1
                quote_start = index + dollar_count
                quote_count = 0
                while (
                    quote_start + quote_count < len(source)
                    and source[quote_start + quote_count] == '"'
                ):
                    quote_count += 1
                if quote_count >= 3:
                    state = f"raw-string:{quote_count}"
                    index = quote_start + quote_count
                    continue
            if current == "@" and following == '"':
                state = "verbatim-string"
                index += 2
                continue
            if current == "$" and following == '"':
                state = "string"
                index += 2
                continue
            # Raw strings use three or more quotes. A shorter run is handled by
            # the ordinary-string branch immediately below.
            if current == '"':
                quote_count = 1
                while index + quote_count < len(source) and source[index + quote_count] == '"':
                    quote_count += 1
                if quote_count >= 3:
                    state = f"raw-string:{quote_count}"
                    index += quote_count
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

        if state.startswith("raw-string:"):
            quote_count = int(state.split(":", 1)[1])
            if source.startswith('"' * quote_count, index):
                state = "normal"
                index += quote_count
            else:
                index += 1

    if state != "normal" and state != "line-comment":
        raise ContractError(f"Unterminated comment or literal in managed wrapper: {path}")

    return "".join(result)


def mask_literals(source: str, path: pathlib.Path) -> str:
    """Mask C# string/character literals while preserving positions and newlines."""
    result = list(source)
    index = 0

    def blank(start: int, end: int) -> None:
        for position in range(start, min(end, len(result))):
            if result[position] not in "\r\n":
                result[position] = " "

    while index < len(source):
        start = index
        verbatim = False
        raw_quotes = 0

        if source.startswith('$@"', index) or source.startswith('@$"', index):
            verbatim = True
            index += 3
        elif source.startswith('@"', index):
            verbatim = True
            index += 2
        elif source[index] == "$":
            dollar_count = 1
            while (
                index + dollar_count < len(source)
                and source[index + dollar_count] == "$"
            ):
                dollar_count += 1
            quote_start = index + dollar_count
            while (
                quote_start + raw_quotes < len(source)
                and source[quote_start + raw_quotes] == '"'
            ):
                raw_quotes += 1
            if raw_quotes >= 3:
                index = quote_start + raw_quotes
            elif dollar_count == 1 and raw_quotes == 1:
                raw_quotes = 0
                index += 2
            else:
                raw_quotes = 0
                index += 1
                continue
        elif source[index] == '"':
            while index + raw_quotes < len(source) and source[index + raw_quotes] == '"':
                raw_quotes += 1
            if raw_quotes >= 3:
                index += raw_quotes
            else:
                raw_quotes = 0
                index += 1
        elif source[index] == "'":
            index += 1
            while index < len(source):
                if source[index] == "\\":
                    index += 2
                elif source[index] == "'":
                    index += 1
                    break
                else:
                    index += 1
            else:
                raise ContractError(f"Unterminated character literal in managed wrapper: {path}")
            blank(start, index)
            continue
        else:
            index += 1
            continue

        if raw_quotes:
            closing = '"' * raw_quotes
            end = source.find(closing, index)
            if end == -1:
                raise ContractError(f"Unterminated raw string in managed wrapper: {path}")
            index = end + raw_quotes
        elif verbatim:
            while index < len(source):
                if source.startswith('""', index):
                    index += 2
                elif source[index] == '"':
                    index += 1
                    break
                else:
                    index += 1
            else:
                raise ContractError(f"Unterminated verbatim string in managed wrapper: {path}")
        else:
            while index < len(source):
                if source[index] == "\\":
                    index += 2
                elif source[index] == '"':
                    index += 1
                    break
                else:
                    index += 1
            else:
                raise ContractError(f"Unterminated string in managed wrapper: {path}")

        blank(start, index)

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


def match_import_library_literal(
    arguments: str, kind: str
) -> re.Match[str] | None:
    parameter_name = IMPORT_LIBRARY_PARAMETER_NAMES[kind]
    return re.match(
        rf'\s*(?:@?{parameter_name}\s*:\s*)?'
        r'(?P<literal>@?"(?:""|\\.|[^"])*")',
        arguments,
        re.DOTALL,
    )


def inventory_library_aliases(library: str) -> set[str]:
    extensionless = library.removesuffix(".so")
    loader_stem = extensionless.removeprefix("lib")
    return {
        extensionless,
        library,
        loader_stem,
        f"{loader_stem}.so",
    }


def import_library_basename(library: str) -> str:
    # Treat both separators conservatively. Linux varies names only when no '/'
    # is present, but source can be built on Windows and path-qualified imports
    # must not evade the packaged-library ownership boundary on either host.
    return re.split(r"[/\\]", library)[-1]


def matching_inventory_libraries(library: str, inventory: set[str]) -> list[str]:
    basename = import_library_basename(library)
    return sorted(
        candidate
        for candidate in inventory
        if basename in inventory_library_aliases(candidate)
    )


def prepare_managed_source(
    path: pathlib.Path, description: str
) -> tuple[str, str]:
    source = read_text(path, description)
    return prepare_managed_text(source, path)


def prepare_managed_text(source: str, path: pathlib.Path) -> tuple[str, str]:
    masked = mask_comments(source, path)
    return masked, mask_literals(masked, path)


def parse_imports(
    path: pathlib.Path, prepared: tuple[str, str] | None = None
) -> list[tuple[str, str]]:
    masked, code_only = (
        prepared
        if prepared is not None
        else prepare_managed_source(path, "managed native wrapper")
    )
    token_count = len(IMPORT_TOKEN_PATTERN.findall(code_only))
    imports: list[tuple[str, str]] = []
    search_index = 0

    while match := ATTRIBUTE_PATTERN.search(code_only, search_index):
        opening = match.end() - 1
        closing = find_closing_parenthesis(code_only, opening, path)
        arguments = masked[opening + 1 : closing]
        argument_code = code_only[opening + 1 : closing]
        library_match = match_import_library_literal(arguments, match.group("kind"))

        if library_match is None:
            raise ContractError(f"Native import library must be a string literal in {path}")

        argument_end = argument_code.find(",", library_match.end())
        if argument_end == -1:
            argument_end = len(argument_code)
        if argument_code[library_match.end() : argument_end].strip():
            raise ContractError(
                f"Native import library must be one string literal in {path}"
            )

        library = decode_csharp_string(library_match.group("literal"), path)
        assignments = list(ENTRY_POINT_ASSIGNMENT_PATTERN.finditer(argument_code))
        if len(assignments) > 1:
            raise ContractError(f"Native import contains multiple EntryPoint values in {path}")

        if assignments:
            literal_match = ENTRY_POINT_PATTERN.match(arguments, assignments[0].end())
            if literal_match is None:
                raise ContractError(
                    f"Native import EntryPoint must be a string literal in {path}"
                )
            argument_end = argument_code.find(",", literal_match.end())
            if argument_end == -1:
                argument_end = len(argument_code)
            if argument_code[literal_match.end() : argument_end].strip():
                raise ContractError(
                    f"Native import EntryPoint must be one string literal in {path}"
                )
            symbol = decode_csharp_string(literal_match.group("literal"), path)
        else:
            declaration_end = code_only.find(";", closing + 1)
            if declaration_end == -1:
                raise ContractError(f"Native import has no terminating declaration in {path}")
            declaration = code_only[closing + 1 : declaration_end + 1]
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

    prepared_sources: dict[pathlib.Path, tuple[str, str]] = {}
    for source_file in source_files:
        prepared = prepare_managed_source(source_file, "managed native wrapper")
        prepared_sources[source_file] = prepared
        _, code_only = prepared
        if re.search(r"(?m)^\s*#(?:if|elif|else|endif)\b", code_only):
            raise ContractError(
                f"Conditional native imports are unsupported; keep the Linux contract "
                f"unconditional or extend the validator explicitly: {source_file}"
            )

    libraries_to_files: dict[str, set[pathlib.Path]] = defaultdict(set)
    exports: dict[str, set[str]] = defaultdict(set)

    for source_file in source_files:
        imports = parse_imports(source_file, prepared_sources[source_file])
        resolved_imports: list[tuple[str, str]] = []
        for managed_library, symbol in imports:
            if import_library_basename(managed_library) != managed_library:
                raise ContractError(
                    f"Managed wrapper library must not contain a path: "
                    f"{source_file}: {managed_library}"
                )

            matches = matching_inventory_libraries(managed_library, inventory)
            if not matches:
                raise ContractError(
                    f"Managed wrapper references a library outside the reviewed inventory: "
                    f"{source_file}: {managed_library}"
                )
            if len(matches) > 1:
                raise ContractError(
                    f"Managed wrapper library name is ambiguous under Unix loader variations: "
                    f"{source_file}: {managed_library}: {', '.join(matches)}"
                )

            resolved_imports.append((matches[0], symbol))
        file_libraries = {library for library, _ in resolved_imports}

        if len(file_libraries) > 1:
            listed = ", ".join(sorted(file_libraries))
            raise ContractError(
                f"Managed wrapper maps ambiguously to multiple libraries: {source_file}: {listed}"
            )

        for native_library, symbol in resolved_imports:
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


def reject_inventory_imports_outside_directory(
    project_dir: pathlib.Path, source_dir: pathlib.Path, inventory: set[str]
) -> None:
    try:
        project_mode = project_dir.lstat().st_mode
    except OSError as error:
        raise ContractError(f"Unable to inspect managed project directory: {error}") from error
    if not stat.S_ISDIR(project_mode):
        raise ContractError(f"Managed project path is not a directory: {project_dir}")

    source_dir = source_dir.resolve()
    inventory_import_names = {
        alias
        for library in inventory
        for alias in inventory_library_aliases(library)
    }
    for source_file in sorted(project_dir.rglob("*.cs")):
        relative_parts = source_file.relative_to(project_dir).parts
        if any(part in GENERATED_DIRECTORY_NAMES for part in relative_parts[:-1]):
            continue

        try:
            source_file.resolve().relative_to(source_dir)
            continue
        except ValueError:
            pass

        source = read_text(source_file, "managed project source")
        # The outer scan does not apply the strict wrapper grammar. Mask and
        # inspect every source-controlled file that can contain an import token,
        # so attribute layout and escaped library literals cannot bypass it while
        # unrelated constant-based operating-system imports remain unaffected.
        if IMPORT_TOKEN_PATTERN.search(source) is None:
            continue

        masked, code_only = prepare_managed_text(source, source_file)
        search_index = 0
        while match := ATTRIBUTE_PATTERN.search(code_only, search_index):
            opening = match.end() - 1
            closing = find_closing_parenthesis(code_only, opening, source_file)
            arguments = masked[opening + 1 : closing]
            library_match = match_import_library_literal(
                arguments, match.group("kind")
            )

            if library_match is not None:
                library = decode_csharp_string(library_match.group("literal"), source_file)
                if import_library_basename(library) in inventory_import_names:
                    raise ContractError(
                        f"Packaged native import is outside the reviewed Native directory: "
                        f"{source_file}: {library}"
                    )

            search_index = closing + 1


def normalize_elf_symbol(symbol: str) -> str:
    """Return the symbol name used by contracts, without an ELF version suffix."""
    normalized = symbol.split("@", 1)[0]
    if not normalized or not SYMBOL_PATTERN.fullmatch(normalized):
        raise ContractError(f"Invalid versioned ELF symbol: {symbol!r}")
    return normalized


def run_tool(
    command: list[str], description: str, environment: dict[str, str] | None = None
) -> str:
    tool_environment = dict(os.environ)
    tool_environment.pop("LD_PRELOAD", None)
    tool_environment.pop("LD_LIBRARY_PATH", None)
    tool_environment["LC_ALL"] = "C"
    if environment:
        tool_environment.update(environment)

    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="strict",
            env=tool_environment,
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
        if normalize_elf_symbol(symbol) != symbol:
            raise ContractError(
                f"Native-symbol exception {index} must use an unversioned symbol name"
            )

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
        [ldd_tool, "-r", str(library_path)],
        f"dynamic relocation inspection for {library}",
    )
    unresolved: set[str] = set()

    for line in relocation_output.splitlines():
        if "not found" in line:
            raise ContractViolation(f"{library} has an unresolved provider: {line.strip()}")
        if "undefined symbol:" not in line:
            continue
        match = UNDEFINED_PATTERN.fullmatch(line)
        if match is None:
            raise ContractError(
                f"Unrecognized unresolved-symbol diagnostic for {library}: {line!r}"
            )
        unresolved.add(normalize_elf_symbol(match.group("symbol")))

    weak_output = run_tool(
        [nm_tool, "-D", "--undefined-only", "--format=posix", str(library_path)],
        f"weak-import inspection for {library}",
    )
    for line in weak_output.splitlines():
        if not line.strip():
            continue
        fields = line.split()
        if (
            len(fields) < 2
            or not SYMBOL_PATTERN.fullmatch(fields[0])
            or len(fields[1]) != 1
        ):
            raise ContractError(f"Unrecognized undefined-symbol diagnostic for {library}: {line!r}")
        symbol = normalize_elf_symbol(fields[0])
        if fields[1] in {"w", "v"} and symbol not in IGNORED_TOOLCHAIN_WEAK_SYMBOLS:
            unresolved.add(symbol)

    violations: list[str] = []
    for symbol in sorted(unresolved):
        key = (library, symbol)
        if key not in exceptions:
            violations.append(f"{library} contains an unapproved unresolved symbol: {symbol}")
        else:
            observed_exceptions.add(key)

    export_output = run_tool(
        [nm_tool, "-D", "--defined-only", "--format=posix", str(library_path)],
        f"export inspection for {library}",
    )
    callable_exports: set[str] = set()
    non_callable_exports: dict[str, str] = {}
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
        if fields[1] in CALLABLE_SYMBOL_TYPES:
            callable_exports.add(fields[0])
        else:
            non_callable_exports[fields[0]] = fields[1]

    for symbol in sorted(expected_exports - callable_exports):
        if symbol in non_callable_exports:
            violations.append(
                f"{library} exports managed entry point {symbol} as non-callable "
                f"symbol type {non_callable_exports[symbol]}"
            )
        else:
            violations.append(f"{library} does not export managed entry point: {symbol}")

    if violations:
        raise ContractViolation("; ".join(violations))


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("publish_directory", type=pathlib.Path)
    parser.add_argument("managed_source_directory", type=pathlib.Path)
    parser.add_argument("inventory", type=pathlib.Path)
    parser.add_argument("--managed-project-directory", required=True, type=pathlib.Path)
    parser.add_argument("--exceptions", type=pathlib.Path)
    parser.add_argument("--ldd", default=os.environ.get("MININGCORE_LDD", "ldd"))
    parser.add_argument("--nm", default=os.environ.get("MININGCORE_NM", "nm"))
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    libraries: list[str] = []
    expected_exports: dict[str, set[str]] = {}

    try:
        libraries = load_inventory(arguments.inventory)
        inventory = set(libraries)
        expected_exports = discover_managed_contracts(arguments.managed_source_directory, inventory)
        reject_inventory_imports_outside_directory(
            arguments.managed_project_directory,
            arguments.managed_source_directory,
            inventory,
        )
        exceptions = load_exceptions(arguments.exceptions, inventory)
        observed_exceptions: set[tuple[str, str]] = set()
        violations: list[str] = []

        for library in libraries:
            try:
                validate_library(
                    library,
                    arguments.publish_directory,
                    expected_exports[library],
                    arguments.ldd,
                    arguments.nm,
                    exceptions,
                    observed_exceptions,
                )
            except ContractViolation as error:
                violations.append(str(error))

        stale_exceptions = set(exceptions) - observed_exceptions
        if stale_exceptions:
            details = ", ".join(
                f"{library}: {symbol}" for library, symbol in sorted(stale_exceptions)
            )
            violations.append(f"Native-symbol exception manifest contains stale entries: {details}")

        if violations:
            raise ContractViolation("\n- ".join(violations))

    except ContractViolation as error:
        print(f"Native symbol contract violation:\n- {error}", file=sys.stderr)
        return CONTRACT_EXIT
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
