#!/usr/bin/env python3
"""Require successful Windows TRX evidence with reviewed discovery and case baselines."""

import argparse
import collections
import contextlib
import copy
import html
import io
import json
import os
import re
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
BASELINE = Path(__file__).with_name("windows-suite-baseline.json")


def case_identity(case):
    identity = tuple(case.get(key) for key in ("class", "method", "case"))
    if any(not isinstance(value, str) or not value.strip() for value in identity):
        raise ValueError("Case identity requires nonempty class, method and parameterized case name")
    return identity


def read_observed(root):
    results = root.findall(NS + "Results/" + NS + "UnitTestResult")
    definitions = {}
    for definition in root.findall(NS + "TestDefinitions/" + NS + "UnitTest"):
        test_id = definition.get("id")
        method = definition.find(NS + "TestMethod")
        if not test_id or test_id in definitions or method is None:
            raise ValueError("Missing or ambiguous test definition")
        definitions[test_id] = method
    observed = {"Passed": collections.Counter(), "NotExecuted": collections.Counter()}
    executions = set()
    for result in results:
        method = definitions.get(result.get("testId"))
        execution = result.get("executionId")
        if method is None or not execution or execution in executions:
            raise ValueError("Missing test definition or missing/duplicate execution identity")
        executions.add(execution)
        identity = case_identity({"class": method.get("className"), "method": method.get("name"),
                                  "case": result.get("testName")})
        if result.get("outcome") not in observed:
            raise ValueError("Unsuccessful or unknown test outcome")
        observed[result.get("outcome")][identity] += 1
    return observed


def validate(root, baseline):
    minimum = baseline["minimum_total"]
    if type(minimum) is not int or minimum < 1:
        raise ValueError("Positive discovery minimum is required")
    required = collections.Counter(map(case_identity, baseline["required_passes"]))
    expected_skips = collections.Counter(map(case_identity, baseline["expected_skips"]))
    if not required or any(count != 1 for count in required.values()) or required.keys() & expected_skips.keys():
        raise ValueError("Required cases must be nonempty, unique and disjoint from expected skips")
    results = root.findall(NS + "Results/" + NS + "UnitTestResult")
    if not results:
        raise ValueError("Nonempty test results are required")
    counts = collections.Counter(item.get("outcome") for item in results)
    if set(counts) - {"Passed", "NotExecuted"} or not counts["Passed"]:
        raise ValueError(f"Unsuccessful or unknown test outcomes: {dict(counts)}")
    summary = root.find(NS + "ResultSummary")
    counters = summary.find(NS + "Counters") if summary is not None else None
    if summary is None or summary.get("outcome") != "Completed" or counters is None:
        raise ValueError("Missing or unsuccessful TRX summary")
    for key, expected in {"total": len(results), "passed": counts["Passed"],
                          "executed": counts["Passed"], "failed": 0}.items():
        if int(counters.get(key, "-1")) != expected:
            raise ValueError(f"TRX counter mismatch: {key}")
    if len(results) < minimum:
        raise ValueError(f"Windows test coverage decreased: {len(results)} discovered; minimum {minimum}")
    # VSTest leaves notExecuted=0 for xUnit skips; count the individual results.

    observed = read_observed(root)
    for identity in required:
        if observed["Passed"][identity] != 1:
            raise ValueError(f"Required regression case missing, repeated or not passed: {identity!r}")
    skipped = observed["NotExecuted"]
    if skipped != expected_skips:
        raise ValueError(f"Skipped case baseline changed; added: {dict(skipped - expected_skips)!r}; "
                         f"removed: {dict(expected_skips - skipped)!r}")
    text = (f"Windows suite: {len(results)} total (minimum {minimum}), {counts['Passed']} passed, "
            f"{counts['NotExecuted']} skipped.\n\n"
            f"All {sum(required.values())} required regression cases passed; exact skipped-case baseline matched.\n\n")
    # Class + method + case avoid conflating identical display names from different tests.
    names = [f"{cls}.{method}: {case}" for (cls, method, case), count in sorted(skipped.items())
             for _ in range(count)]
    return text + "<details><summary>Skipped cases</summary>\n<pre>" + \
        html.escape("\n".join(names)) + "</pre>\n</details>\n"


def report(path, baseline_path):
    with open(baseline_path, encoding="utf-8") as source:
        baseline = json.load(source)
    return validate(ET.parse(path).getroot(), baseline)


def refresh_baseline(root, baseline, source_commit, source_run):
    if not re.fullmatch(r"[0-9a-fA-F]{40}", source_commit or ""):
        raise ValueError("Source commit must be a full 40-character Git SHA")
    if not re.fullmatch(r"https://github.com/NINJAK1DD/miningcore/actions/runs/[1-9][0-9]*", source_run or ""):
        raise ValueError("Source run must be a Miningcore GitHub Actions run URL")
    # Refresh only the skip inventory. Never silently drop required cases or lower
    # the discovery floor to bless an incomplete run, even during regeneration.
    candidate = copy.deepcopy(baseline)
    skipped = read_observed(root)["NotExecuted"]
    candidate["expected_skips"] = [dict(zip(("class", "method", "case"), identity))
                                   for identity, count in sorted(skipped.items()) for _ in range(count)]
    validate(root, candidate)
    candidate["minimum_total"] = len(root.findall(NS + "Results/" + NS + "UnitTestResult"))
    candidate["source_commit"] = source_commit.lower()
    candidate["source_run"] = source_run
    return candidate


def write_baseline(trx_path, baseline_path, output_path, source_commit, source_run):
    output_path = Path(output_path)
    if output_path.resolve() == Path(trx_path).resolve():
        raise ValueError("Baseline output must not overwrite the TRX evidence")
    with open(baseline_path, encoding="utf-8") as source:
        baseline = json.load(source)
    candidate = refresh_baseline(ET.parse(trx_path).getroot(), baseline, source_commit, source_run)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=output_path.parent,
                                         prefix=output_path.name + ".", suffix=".tmp", delete=False) as target:
            temporary = Path(target.name)
            json.dump(candidate, target, indent=2)
            target.write("\n")
        os.replace(temporary, output_path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", nargs="?")
    parser.add_argument("--baseline", type=Path, default=BASELINE)
    parser.add_argument("--write-baseline", type=Path, metavar="OUTPUT",
                        help="Refresh skips and discovery minimum from a successful TRX; preserve required passes")
    parser.add_argument("--source-commit", help="Full Git commit SHA that produced the TRX")
    parser.add_argument("--source-run", help="GitHub Actions run URL that produced the TRX")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)
    if args.write_baseline and not (args.trx and args.source_commit and args.source_run):
        parser.error("--write-baseline requires a TRX, --source-commit and --source-run")
    if not args.write_baseline and (args.source_commit or args.source_run):
        parser.error("Provenance options require --write-baseline")
    if not args.trx and not args.self_test:
        parser.error("TRX path is required")
    return args


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.cases = [
            {"class": "Suite.A", "method": "Regression", "case": "Regression(value: 1)"},
            {"class": "Suite.B", "method": "Regression", "case": "Regression(value: 1)"},
            {"class": "Suite.Optional", "method": "Native", "case": "Native(<same&name>)"},
        ]
        self.baseline = {"minimum_total": 3, "required_passes": [self.cases[0]],
                         "expected_skips": [self.cases[2]]}

    def fixture(self, cases=None, outcomes=None):
        cases = self.cases if cases is None else cases
        outcomes = ["Passed", "Passed", "NotExecuted"] if outcomes is None else outcomes
        root = ET.Element(NS + "TestRun")
        results = ET.SubElement(root, NS + "Results")
        definitions = ET.SubElement(root, NS + "TestDefinitions")
        for i, (case, outcome) in enumerate(zip(cases, outcomes)):
            ET.SubElement(results, NS + "UnitTestResult", testId=str(i), executionId=str(i),
                          outcome=outcome, testName=case["case"])
            definition = ET.SubElement(definitions, NS + "UnitTest", id=str(i))
            ET.SubElement(definition, NS + "TestMethod", className=case["class"], name=case["method"])
        result = ET.SubElement(root, NS + "ResultSummary", outcome="Completed")
        ET.SubElement(result, NS + "Counters", total=str(len(cases)), executed=str(outcomes.count("Passed")),
                      passed=str(outcomes.count("Passed")), failed="0", notExecuted="0")
        return root

    def check(self, root=None):
        return validate(self.fixture() if root is None else root, self.baseline)

    def test_success_and_escaping(self):
        self.assertIn("3 total (minimum 3), 2 passed, 1 skipped", self.check())
        self.assertIn("&lt;same&amp;name&gt;", self.check())

    def test_discovery_loss_with_consistent_counters(self):
        with self.assertRaisesRegex(ValueError, "coverage decreased"):
            self.check(self.fixture(self.cases[:1], ["Passed"]))

    def test_required_case_cannot_trade_skip_with_optional_case(self):
        with self.assertRaisesRegex(ValueError, "Required regression case"):
            self.check(self.fixture(outcomes=["NotExecuted", "Passed", "Passed"]))

    def test_skip_identity_changes_even_with_same_count(self):
        with self.assertRaisesRegex(ValueError, "Skipped case baseline changed"):
            self.check(self.fixture(outcomes=["Passed", "NotExecuted", "Passed"]))

    def test_skip_removal_requires_review_too(self):
        with self.assertRaisesRegex(ValueError, "Skipped case baseline changed"):
            self.check(self.fixture(outcomes=["Passed"] * 3))

    def test_required_case_disappearance_is_not_hidden_by_replacement(self):
        for field, replacement in (("class", "Suite.B"), ("method", "Different"), ("case", "Regression(value: 2)")):
            cases = copy.deepcopy(self.cases)
            cases[0][field] = replacement
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, "Required regression case"):
                self.check(self.fixture(cases))

    def test_new_passing_tests_are_allowed(self):
        root = self.fixture(self.cases + [{"class": "New", "method": "Test", "case": "Test"}],
                            ["Passed", "Passed", "NotExecuted", "Passed"])
        self.assertIn("4 total", self.check(root))

    def test_bad_outcomes_summaries_and_counters(self):
        for outcomes in (["NotExecuted"] * 3, ["Passed", "Failed", "NotExecuted"],
                         ["Passed", "Unknown", "NotExecuted"]):
            with self.subTest(outcomes=outcomes), self.assertRaises(ValueError):
                self.check(self.fixture(outcomes=outcomes))
        with self.assertRaises(ValueError):
            self.check(self.fixture([], []))
        for field, value in (("outcome", "Aborted"), ("total", "9"), ("passed", "1"),
                             ("executed", "1"), ("failed", "1")):
            root = self.fixture()
            summary = root.find(NS + "ResultSummary")
            (summary if field == "outcome" else summary.find(NS + "Counters")).set(field, value)
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.check(root)
        root = self.fixture()
        root.remove(root.find(NS + "ResultSummary"))
        with self.assertRaises(ValueError):
            self.check(root)

    def test_baseline_validation(self):
        for field, value in (("minimum_total", 0), ("required_passes", []),
                             ("required_passes", [self.cases[0]] * 2),
                             ("required_passes", [self.cases[2]])):
            baseline = {**self.baseline, field: value}
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                validate(self.fixture(), baseline)

    def test_missing_and_duplicate_execution_or_definition_rejected(self):
        for change in ("missing-definition", "duplicate-definition", "missing-execution", "duplicate-execution", "empty-class"):
            root = self.fixture()
            definitions = root.find(NS + "TestDefinitions")
            results = root.find(NS + "Results")
            if change == "missing-definition": definitions.remove(definitions[0])
            if change == "duplicate-definition": definitions.append(copy.deepcopy(definitions[0]))
            if change == "missing-execution": results[0].attrib.pop("executionId")
            if change == "duplicate-execution": results[1].set("executionId", "0")
            if change == "empty-class": definitions[0][0].set("className", "")
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.check(root)

    def test_missing_and_malformed_files(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "suite.trx"
            baseline = Path(directory) / "baseline.json"
            baseline.write_text(json.dumps(self.baseline), encoding="utf-8")
            with self.assertRaises(FileNotFoundError): report(path, baseline)
            path.write_text("<invalid", encoding="utf-8")
            with self.assertRaises(ET.ParseError): report(path, baseline)
            ET.ElementTree(self.fixture()).write(path)
            self.assertIn("3 total", report(path, baseline))
            baseline.write_text("{invalid", encoding="utf-8")
            with self.assertRaises(json.JSONDecodeError): report(path, baseline)

    def test_cli_requires_complete_provenance_for_updates(self):
        for argv in (["--write-baseline", "out.json"],
                     ["suite.trx", "--write-baseline", "out.json"],
                     ["suite.trx", "--source-commit", "a" * 40],
                     ["suite.trx", "--max-skipped", "1"]):
            with self.subTest(argv=argv), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                parse_args(argv)
        self.assertEqual("suite.trx", parse_args(["suite.trx"]).trx)
        self.assertTrue(parse_args(["--self-test"]).self_test)

    def refresh(self, root, baseline=None):
        return refresh_baseline(root, self.baseline if baseline is None else baseline, "a" * 40,
                                "https://github.com/NINJAK1DD/miningcore/actions/runs/1")

    def test_refresh_preserves_required_cases_and_increases_discovery_floor(self):
        original = copy.deepcopy(self.baseline)
        root = self.fixture(self.cases + [{"class": "New", "method": "Test", "case": "Test"}],
                            ["Passed", "Passed", "NotExecuted", "Passed"])
        refreshed = self.refresh(root)
        self.assertEqual(original, self.baseline)
        self.assertEqual(original["required_passes"], refreshed["required_passes"])
        self.assertEqual(4, refreshed["minimum_total"])
        self.assertEqual("a" * 40, refreshed["source_commit"])
        self.assertIn("4 total", validate(root, refreshed))
        self.assertEqual(refreshed, self.refresh(root, refreshed))

    def test_refresh_explicitly_updates_added_and_removed_skips(self):
        for outcomes in (["Passed", "NotExecuted", "NotExecuted"], ["Passed", "Passed", "Passed"]):
            root = self.fixture(outcomes=outcomes)
            refreshed = self.refresh(root)
            self.assertEqual(outcomes.count("NotExecuted"), len(refreshed["expected_skips"]))
            validate(root, refreshed)

    def test_refresh_cannot_bless_lost_required_cases_or_failed_or_incomplete_evidence(self):
        missing_required = copy.deepcopy(self.cases)
        missing_required[0]["class"] = "DifferentClass"
        roots = [self.fixture(self.cases[:1], ["Passed"]), self.fixture(missing_required),
                 self.fixture(outcomes=["NotExecuted", "Passed", "Passed"]),
                 self.fixture(outcomes=["Passed", "Failed", "NotExecuted"])]
        inconsistent = self.fixture()
        inconsistent.find(NS + "ResultSummary/" + NS + "Counters").set("total", "1")
        roots.append(inconsistent)
        for root in roots:
            with self.subTest(xml=ET.tostring(root)), self.assertRaises(ValueError):
                self.refresh(root)

    def test_refresh_requires_valid_provenance(self):
        for commit, run in (("short", "https://github.com/NINJAK1DD/miningcore/actions/runs/1"),
                            ("a" * 40, "https://example.com/run/1"), ("a" * 40, "")):
            with self.subTest(commit=commit, run=run), self.assertRaises(ValueError):
                refresh_baseline(self.fixture(), self.baseline, commit, run)

    def test_baseline_write_is_atomic_and_never_overwrites_trx(self):
        with tempfile.TemporaryDirectory() as directory:
            trx = Path(directory) / "suite.trx"
            baseline = Path(directory) / "baseline.json"
            baseline.write_text(json.dumps(self.baseline), encoding="utf-8")
            ET.ElementTree(self.fixture()).write(trx)
            arguments = (trx, baseline, baseline, "a" * 40,
                         "https://github.com/NINJAK1DD/miningcore/actions/runs/1")
            before = baseline.read_bytes()
            with mock.patch("os.replace", side_effect=OSError("injected write failure")), self.assertRaises(OSError):
                write_baseline(*arguments)
            self.assertEqual(before, baseline.read_bytes())
            self.assertEqual({trx, baseline}, set(Path(directory).iterdir()))
            with self.assertRaisesRegex(ValueError, "overwrite the TRX"):
                write_baseline(trx, baseline, trx, arguments[-2], arguments[-1])
            ET.ElementTree(self.fixture(outcomes=["NotExecuted", "Passed", "Passed"])).write(trx)
            with self.assertRaises(ValueError):
                write_baseline(*arguments)
            self.assertEqual(before, baseline.read_bytes())
            ET.ElementTree(self.fixture()).write(trx)
            write_baseline(*arguments)
            self.assertIn("3 total", report(trx, baseline))


if __name__ == "__main__":
    args = parse_args()
    if args.self_test:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(EvidenceTests)
        if not unittest.TextTestRunner().run(suite).wasSuccessful():
            raise SystemExit(1)
    if args.write_baseline:
        write_baseline(args.trx, args.baseline, args.write_baseline, args.source_commit, args.source_run)
        print(f"Wrote {args.write_baseline}; inspect the diff before committing the baseline update.")
    elif args.trx:
        output = report(args.trx, args.baseline)
        print(output)
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
                summary.write(output)
