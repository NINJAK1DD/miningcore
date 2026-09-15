#!/usr/bin/env python3
"""Enforce docs/releases.md's action-pin contract. Requires python3-yaml."""

import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import yaml
from yaml.nodes import MappingNode, ScalarNode, SequenceNode


ROOT = Path(__file__).resolve().parents[2]
ACTION = re.compile(r"[\w.-]+/[\w.-]+(?:/[\w./-]+)?@[0-9a-f]{40}")
IMAGE = re.compile(r"docker://[^\s@]+@sha256:[0-9a-f]{64}")
VERSION = re.compile(r" +# +v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)? *")


def inspect_document(source, publisher=False):
    """Parse YAML so quoting, reuse and run-script text cannot bypass the guard."""
    errors = []
    count = 0
    structure_valid = True
    try:
        # Anchors/aliases and merge keys obscure each call site's version comment.
        # Fail closed rather than letting YAML expand them before validation.
        if any(isinstance(token, (yaml.tokens.AnchorToken, yaml.tokens.AliasToken))
               for token in yaml.scan(source)):
            return ["YAML anchors and aliases are unsupported by the action-pin contract"], 0, []
        root = yaml.compose(source, Loader=yaml.SafeLoader)
    except yaml.YAMLError as error:
        mark = getattr(error, "problem_mark", None)
        lines = source.splitlines()
        if mark is not None and mark.line < len(lines) and "\t" in lines[mark.line]:
            return [f"line {mark.line + 1}: invalid YAML; use spaces instead of tabs"], 0, []
        return ["invalid YAML"], 0, []
    if not isinstance(root, MappingNode):
        return ["workflow/action must be a YAML mapping"], 0, []

    lines = source.splitlines()

    def walk(node):
        nonlocal count, structure_valid
        if isinstance(node, MappingNode):
            keys = set()
            for key, value in node.value:
                if not isinstance(key, ScalarNode) or key.value in keys or key.value == "<<":
                    errors.append(f"line {key.start_mark.line + 1}: duplicate or unsupported mapping key")
                    structure_valid = False
                    continue
                keys.add(key.value)
                walk(value)
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
        elif isinstance(node, SequenceNode):
            for child in node.value:
                walk(child)

    walk(root)
    contract_errors = []
    if publisher and structure_valid:
        # Pin-format errors do not mask independent policy errors. Structural
        # errors still block loading: alias sharing would undermine secret isolation.
        # BaseLoader preserves GitHub's 'on' key and literal input strings.
        contract_errors = check_publisher(yaml.load(source, Loader=yaml.BaseLoader))
    return errors, count, contract_errors


def check_document(source):
    errors, count, _ = inspect_document(source)
    return errors, count


def manual_dispatch_only(value, require_expression=False):
    """Recognize the documented simple equality, not arbitrary Actions expressions."""
    if not isinstance(value, str):
        return False
    value = value.strip()
    if value.startswith("${{") and value.endswith("}}"):
        value = value[3:-2].strip()
    elif require_expression:
        return False
    while value.startswith("(") and value.endswith(")"):
        value = value[1:-1].strip()
    return re.fullmatch(r"github\s*(?:\.\s*event_name|\[\s*'event_name'\s*\])\s*==\s*'workflow_dispatch'", value) is not None


def check_publisher(workflow):
    """Check live publication policy separately from the hermetic checker tests."""
    errors = []
    if not isinstance(workflow, dict):
        return ["publisher must be a mapping"]
    triggers = workflow.get("on")
    if not isinstance(triggers, dict) or set(triggers) != {"pull_request", "workflow_dispatch"}:
        errors.append("publisher permits only pull_request and workflow_dispatch triggers")
    if workflow.get("permissions") != {"contents": "read"}:
        errors.append("publisher must have read-only contents permission")
    jobs = workflow.get("jobs")
    if not isinstance(jobs, dict) or set(jobs) != {"build"} or not isinstance(jobs["build"], dict):
        return errors + ["publisher must contain one build job"]
    job = jobs["build"]
    if "permissions" in job and job["permissions"] != {"contents": "read"}:
        errors.append("build job must not override read-only contents permission")
    steps = job.get("steps")
    if not isinstance(steps, list) or not all(isinstance(step, dict) for step in steps):
        return errors + ["publisher steps must be a sequence of mappings"]
    login = [step for step in steps if str(step.get("uses", "")).startswith("docker/login-action@")]
    build = [step for step in steps if str(step.get("uses", "")).startswith("docker/build-push-action@")]
    if len(login) != 1 or len(build) != 1:
        return errors + ["publisher requires exactly one Docker login and build step"]
    login, build = login[0], build[0]
    if not manual_dispatch_only(login.get("if")):
        errors.append("Docker login must be gated by the manual-dispatch equality")
    inputs = build.get("with")
    if not isinstance(inputs, dict):
        errors.append("Docker build inputs must be a mapping")
    else:
        if not manual_dispatch_only(inputs.get("push"), require_expression=True):
            errors.append("Docker push must evaluate the manual-dispatch equality")
        if inputs.get("provenance") != "mode=max" or inputs.get("sbom") != "true":
            errors.append("Docker build must retain provenance and SBOM validation")

    def has_secrets(value):
        # Only login inputs may reference secrets. Include top-level/job env and
        # both dot/bracket notation, without relying on Python's dictionary repr.
        if value is login.get("with"):
            return False
        if isinstance(value, str):
            # Deliberately conservative: even a step name or script comment using
            # the bare word "secrets" requires rewording or a contract review.
            return re.search(r"\bsecrets\b", value, re.IGNORECASE) is not None
        if isinstance(value, dict):
            return any(has_secrets(key) or has_secrets(child) for key, child in value.items())
        if isinstance(value, list):
            return any(has_secrets(child) for child in value)
        return False

    if has_secrets(workflow):
        errors.append("secret references are allowed only in the gated Docker login inputs")
    return errors


def discover_paths(root):
    paths = set((root / ".github/workflows").glob("*.yml"))
    paths.update((root / ".github/workflows").glob("*.yaml"))
    try:
        result = subprocess.run(["git", "ls-files", "-z"], cwd=root, capture_output=True, check=True)
    except (OSError, subprocess.CalledProcessError) as error:
        # An incomplete scan must not silently pass an extracted release tree.
        raise ValueError("cannot list tracked actions; run this check in a Git checkout with Git installed") from error
    tracked = result.stdout.decode("utf-8").split("\0")
    paths.update(root / name for name in tracked if Path(name).name in ("action.yml", "action.yaml"))
    if not paths:
        raise ValueError("no workflow files found")
    return paths


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

    def test_tab_diagnostic(self):
        errors, _ = check_document(f"uses: owner/action@{self.sha}\t# v1.2.3\n")
        self.assertIn("use spaces instead of tabs", errors[0])

    @staticmethod
    def publisher():
        return {"on": {"pull_request": {}, "workflow_dispatch": {}},
                "permissions": {"contents": "read"}, "jobs": {"build": {"steps": [
                    {"uses": "docker/login-action@" + "a" * 40,
                     "if": "github.event_name == 'workflow_dispatch'",
                     "with": {"password": "${{ secrets['DOCKER_PASSWORD'] }}"}},
                    {"uses": "docker/build-push-action@" + "b" * 40,
                     "with": {"push": "${{ github.event_name == 'workflow_dispatch' }}",
                              "provenance": "mode=max", "sbom": "true"}}]}}}

    def test_publisher_accepts_equivalent_conditions(self):
        for condition in ("github.event_name=='workflow_dispatch'",
                          "${{ ( github.event_name  ==  'workflow_dispatch' ) }}",
                          "github['event_name'] == 'workflow_dispatch'"):
            workflow = self.publisher()
            workflow["jobs"]["build"]["steps"][0]["if"] = condition
            self.assertEqual(check_publisher(workflow), [])

    def test_publisher_yaml_quote_styles(self):
        for condition in ('"github.event_name == \'workflow_dispatch\'"',
                          "'github.event_name == ''workflow_dispatch'''", "github.event_name == 'workflow_dispatch'"):
            workflow = self.publisher()
            workflow["jobs"]["build"]["steps"][0]["if"] = yaml.load("if: " + condition, Loader=yaml.BaseLoader)["if"]
            self.assertEqual(check_publisher(workflow), [])

    def test_publisher_rejects_unsafe_gates(self):
        for condition in (None, "true", "github.event_name == 'pull_request'",
                          "github.event_name == 'workflow_dispatch' || true"):
            workflow = self.publisher()
            workflow["jobs"]["build"]["steps"][0]["if"] = condition
            self.assertTrue(check_publisher(workflow))
        workflow = self.publisher()
        workflow["jobs"]["build"]["steps"][1]["with"]["push"] = "github.event_name == 'workflow_dispatch'"
        self.assertTrue(check_publisher(workflow))  # A bare action input is a truthy string.

    def test_publisher_rejects_secret_dot_and_bracket_access(self):
        for expression in ("${{ secrets.DOCKER_USER }}", "${{ secrets['DOCKER_USER'] }}",
                           "${{ SECRETS['DOCKER_USER'] }}"):
            for location in ("workflow", "job", "step"):
                workflow = self.publisher()
                target = {"workflow": workflow, "job": workflow["jobs"]["build"],
                          "step": workflow["jobs"]["build"]["steps"][1]}[location]
                target["env"] = {"TOKEN": expression}
                self.assertTrue(check_publisher(workflow))

    def test_publisher_rejects_malformed_and_privileged_jobs(self):
        for workflow in (None, {}, {"jobs": []}, {"jobs": {"build": {"steps": [None]}}}):
            self.assertTrue(check_publisher(workflow))
        workflow = self.publisher()
        workflow["jobs"]["build"]["permissions"] = {"contents": "write"}
        self.assertTrue(check_publisher(workflow))

    def test_pin_and_publisher_errors_are_reported_together(self):
        workflow = self.publisher()
        steps = workflow["jobs"]["build"]["steps"]
        steps[0]["uses"] = "docker/login-action@v4"
        steps[0]["if"] = "true"
        errors, count, policy = inspect_document(yaml.safe_dump(workflow), publisher=True)
        self.assertTrue(any("full commit SHA" in error for error in errors))
        self.assertEqual(count, 2)
        self.assertIn("Docker login must be gated by the manual-dispatch equality", policy)

    def test_structural_errors_block_publisher_loading(self):
        for source in ("jobs: [\n", "jobs: &jobs {}\ncopy: *jobs\n",
                       "jobs: {}\njobs: {}\n", "jobs:\n  <<: {}\n",
                       "uses: {duplicate: one, duplicate: two}\n"):
            with patch(__name__ + ".check_publisher") as check:
                errors, _, policy = inspect_document(source, publisher=True)
                self.assertTrue(errors)
                self.assertEqual(policy, [])
                check.assert_not_called()

    def test_discovery_failure_is_actionable(self):
        with tempfile.TemporaryDirectory() as directory:
            for failure in (FileNotFoundError(), subprocess.CalledProcessError(128, "git")):
                with patch.object(subprocess, "run", side_effect=failure):
                    with self.assertRaisesRegex(ValueError, "Git checkout"):
                        discover_paths(Path(directory))

    def test_discovery_includes_composites_and_yaml_workflows(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / ".github/workflows").mkdir(parents=True)
            (root / ".github/workflows/test.yaml").touch()
            result = subprocess.CompletedProcess([], 0, b"local/action.yml\0local/action.yaml\0src/a.cs\0")
            with patch.object(subprocess, "run", return_value=result):
                self.assertEqual(discover_paths(root), {
                    root / ".github/workflows/test.yaml", root / "local/action.yml", root / "local/action.yaml"})


def main():
    try:
        paths = discover_paths(ROOT)
    except ValueError as error:
        raise SystemExit(f"Cannot validate workflow pins: {error}") from None
    failed = False
    count = 0
    for path in sorted(paths):
        try:
            source = path.read_text(encoding="utf-8")
        except (OSError, UnicodeError):
            print(f"Cannot read workflow/action file {ascii(str(path.relative_to(ROOT)))}")
            failed = True
            continue
        errors, found, contract_errors = inspect_document(
            source, publisher=path == ROOT / ".github/workflows/docker-image.yml")
        count += found
        for error in errors:
            # ascii escapes prevent workflow-command injection through filenames.
            print(f"Invalid action pin in {ascii(str(path.relative_to(ROOT)))}: {error}")
        failed |= bool(errors)
        for error in contract_errors:
            print(f"Invalid workflow contract in '.github/workflows/docker-image.yml': {error}")
        failed |= bool(contract_errors)
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
