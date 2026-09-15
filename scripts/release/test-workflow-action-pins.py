#!/usr/bin/env python3
"""Enforce docs/releases.md's action-pin contract. Requires python3-yaml."""

import re
import sys
import unittest
from pathlib import Path

import yaml
from yaml.nodes import MappingNode, ScalarNode, SequenceNode


ROOT = Path(__file__).resolve().parents[2]
ACTION = re.compile(r"[\w.-]+/[\w.-]+(?:/[\w./-]+)?@[0-9a-f]{40}")
IMAGE = re.compile(r"docker://[^\s@]+@sha256:[0-9a-f]{64}")
VERSION = re.compile(r"[ \t]+#[ \t]+v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?[ \t]*")


def check_document(source):
    """Parse YAML so quoting, reuse and run-script text cannot bypass the guard."""
    errors = []
    count = 0
    try:
        # Anchors/aliases and merge keys obscure each call site's version comment.
        # Fail closed rather than letting YAML expand them before validation.
        if any(isinstance(token, (yaml.tokens.AnchorToken, yaml.tokens.AliasToken))
               for token in yaml.scan(source)):
            return ["YAML anchors and aliases are unsupported by the action-pin contract"], 0
        root = yaml.compose(source, Loader=yaml.SafeLoader)
    except yaml.YAMLError:
        return ["invalid YAML"], 0
    if not isinstance(root, MappingNode):
        return ["workflow/action must be a YAML mapping"], 0

    lines = source.splitlines()

    def walk(node):
        nonlocal count
        if isinstance(node, MappingNode):
            keys = set()
            for key, value in node.value:
                if not isinstance(key, ScalarNode) or key.value in keys or key.value == "<<":
                    errors.append(f"line {key.start_mark.line + 1}: duplicate or unsupported mapping key")
                    continue
                keys.add(key.value)
                if key.value == "uses":
                    count += 1
                    line = key.start_mark.line + 1
                    if not isinstance(value, ScalarNode) or value.tag != "tag:yaml.org,2002:str":
                        errors.append(f"line {line}: uses must be a string")
                        continue
                    ref = value.value
                    if ref.startswith("./") and ".." not in ref.split("/") and "${{" not in ref:
                        continue
                    if IMAGE.fullmatch(ref):
                        continue
                    if not ACTION.fullmatch(ref):
                        errors.append(f"line {line}: external uses requires a full commit SHA (or Docker SHA-256 digest)")
                        continue
                    # Only single-line scalars with a bare version comment are accepted.
                    if (value.start_mark.line != value.end_mark.line or
                            not VERSION.fullmatch(lines[value.end_mark.line][value.end_mark.column:])):
                        errors.append(f"line {line}: external action requires a bare trailing # vX.Y.Z comment")
                walk(value)
        elif isinstance(node, SequenceNode):
            for child in node.value:
                walk(child)

    walk(root)
    return errors, count


class ContractTests(unittest.TestCase):
    sha = "a" * 40

    def test_annotated_and_quoted_pins(self):
        for quote in ("", "'", '"'):
            source = f"steps:\n- uses: {quote}owner/action@{self.sha}{quote} # v1.2.3\n"
            self.assertEqual(check_document(source), ([], 1))

    def test_reusable_workflow(self):
        self.assertEqual(check_document(
            f"jobs:\n  build:\n    uses: owner/repo/.github/workflows/build.yml@{self.sha} # v1.2.3\n"), ([], 1))

    def test_floating_short_and_missing_comment(self):
        for ref in ("owner/action@v1", "owner/action@abc1234", f"owner/action@{self.sha}"):
            with self.subTest(ref=ref):
                self.assertTrue(check_document(f"steps:\n- uses: {ref}\n")[0])

    def test_misleading_version_comment(self):
        for comment in ("# v1", "# v1.2.3; node24", "# v1.2.3 extra"):
            self.assertTrue(check_document(f"steps:\n- uses: owner/action@{self.sha} {comment}\n")[0])

    def test_local_and_container_actions(self):
        self.assertEqual(check_document("steps:\n- uses: ./local/action\n"), ([], 1))
        self.assertEqual(check_document(f"steps:\n- uses: docker://alpine@sha256:{'b' * 64}\n"), ([], 1))
        self.assertTrue(check_document("steps:\n- uses: docker://alpine:latest\n")[0])

    def test_script_content_is_not_an_action(self):
        self.assertEqual(check_document("steps:\n- run: |\n    uses: owner/action@v1\n"), ([], 0))

    def test_duplicate_alias_and_multiline_bypass(self):
        for source in (
            f"uses: owner/action@{self.sha} # v1.2.3\nuses: owner/action@v1\n",
            "anchor: &ref owner/action@v1\nuses: *ref\n",
            f"uses: >- # v1.2.3\n  owner/action@{self.sha}\n",
            "steps: [\n",
            "uses: null\n",
        ):
            self.assertTrue(check_document(source)[0])

    def test_flow_mapping_without_usable_comment(self):
        self.assertTrue(check_document(f"steps: [{{uses: owner/action@{self.sha}}}] # v1.2.3\n")[0])

    def test_manual_publisher_keeps_pr_builds_unprivileged(self):
        path = ROOT / ".github/workflows/docker-image.yml"
        # BaseLoader preserves GitHub's 'on' key rather than YAML 1.1's Boolean coercion.
        workflow = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
        self.assertEqual(set(workflow["on"]), {"pull_request", "workflow_dispatch"})
        self.assertEqual(workflow["permissions"], {"contents": "read"})
        steps = workflow["jobs"]["build"]["steps"]
        login = next(step for step in steps if step.get("uses", "").startswith("docker/login-action@"))
        self.assertEqual(login["if"], "github.event_name == 'workflow_dispatch'")
        build = next(step for step in steps if step.get("uses", "").startswith("docker/build-push-action@"))
        self.assertEqual(build["with"]["push"], "${{ github.event_name == 'workflow_dispatch' }}")
        self.assertEqual(build["with"]["provenance"], "mode=max")
        self.assertEqual(build["with"]["sbom"], "true")
        for step in steps:
            if step is not login:
                self.assertNotIn("secrets.", str(step))


def main():
    paths = set((ROOT / ".github/workflows").glob("*.yml"))
    paths.update((ROOT / ".github/workflows").glob("*.yaml"))
    # Include repository-owned composite actions, excluding generated/ignored files.
    import subprocess
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
    paths.update(ROOT / name for name in tracked if Path(name).name in ("action.yml", "action.yaml"))
    if not paths:
        raise SystemExit("No workflows found")
    failed = False
    count = 0
    for path in sorted(paths):
        errors, found = check_document(path.read_text(encoding="utf-8"))
        count += found
        for error in errors:
            # ascii escapes prevent workflow-command injection through filenames.
            print(f"Invalid action pin in {ascii(str(path.relative_to(ROOT)))}: {error}")
        failed |= bool(errors)
    if failed:
        raise SystemExit(1)
    print(f"Validated {count} uses references in {len(paths)} workflow/action files")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        unittest.main(argv=[sys.argv[0]])
    elif sys.argv[1:]:
        raise SystemExit("Usage: test-workflow-action-pins.py [--self-test]")
    else:
        main()
