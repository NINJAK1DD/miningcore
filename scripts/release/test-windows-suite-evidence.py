#!/usr/bin/env python3
"""Require complete, successful Windows TRX evidence and a reviewed skip ceiling."""

import argparse
import collections
import html
import os
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def report(path, max_skipped):
    root = ET.parse(path).getroot()
    results = list(root.iter(NS + "UnitTestResult"))
    if not results or max_skipped < 0:
        raise ValueError("Nonempty test results and a nonnegative skip ceiling are required")
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
    # VSTest leaves notExecuted=0 for xUnit skips; count the individual results.
    if counts["NotExecuted"] > max_skipped:
        raise ValueError(f"Skipped {counts['NotExecuted']} tests; reviewed ceiling is {max_skipped}")
    text = (f"Windows suite: {len(results)} total, {counts['Passed']} passed, "
            f"{counts['NotExecuted']} skipped (ceiling {max_skipped}).\n\n")
    # Preserve individual results: theory display names are not unique identifiers.
    skipped = sorted(item.get("testName", "(unnamed)") for item in results
                     if item.get("outcome") == "NotExecuted")
    return text + "<details><summary>Skipped cases</summary>\n<pre>" + \
        html.escape("\n".join(skipped)) + "</pre>\n</details>\n"


class EvidenceTests(unittest.TestCase):
    def test_validation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "suite.trx"
            with self.assertRaises(FileNotFoundError):
                report(path, 1)
            def write(outcomes, summary="Completed", total=None):
                root = ET.Element(NS + "TestRun")
                for outcome in outcomes:
                    ET.SubElement(root, NS + "UnitTestResult", outcome=outcome, testName="<same&name>")
                result = ET.SubElement(root, NS + "ResultSummary", outcome=summary)
                ET.SubElement(result, NS + "Counters", total=str(len(outcomes) if total is None else total),
                              executed=str(outcomes.count("Passed")), passed=str(outcomes.count("Passed")),
                              failed="0", notExecuted=str(outcomes.count("NotExecuted")))
                ET.ElementTree(root).write(path)
            write(["Passed", "Passed", "NotExecuted"])
            self.assertIn("3 total, 2 passed, 1 skipped", report(path, 1))
            self.assertIn("&lt;same&amp;name&gt;", report(path, 1))
            with self.assertRaises(ValueError):
                report(path, 0)
            for outcomes, summary, total in [([], "Completed", None),
                    (["NotExecuted"], "Completed", None), (["Failed"], "Completed", None),
                    (["Passed", "Unknown"], "Completed", None), (["Passed"], "Aborted", None),
                    (["Passed"], "Completed", 2)]:
                write(outcomes, summary, total)
                with self.assertRaises(ValueError):
                    report(path, 1)
            path.write_text("<invalid", encoding="utf-8")
            with self.assertRaises(ET.ParseError):
                report(path, 1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", nargs="?")
    parser.add_argument("--max-skipped", type=int, default=176)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(EvidenceTests)
        if not unittest.TextTestRunner().run(suite).wasSuccessful():
            raise SystemExit(1)
    if args.trx:
        output = report(args.trx, args.max_skipped)
        print(output)
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
                summary.write(output)
    elif not args.self_test:
        parser.error("TRX path is required")
