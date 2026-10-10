#!/usr/bin/env python3
"""Fail CI when a required Linux-native pool test is absent, skipped or failed."""

import argparse
import re
import xml.etree.ElementTree as ET

NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
CONTENTION = "NativeConcretePool_AuthorizationIdleAndBroadcastCompleteUnderContention"
CANCELLATION = "NativePool_AuthorizationCancellationDoesNotCommitQueuedDifficulty"
REQUEST = "NativePool_SubUnitRequestCannotPublishUnderweightedWork"
PROOF = "NativeProof_AfterRejectedSubUnitAssignmentRetainsRepresentableCredit"
HINT = "NativePool_DispatchedHintsKeepOneResponseAndValidWork"
WAIT = "NativePool_DispatchedLoginWaitsBeforeAuthorizing"
PATHS = (("Conceal", "login"), ("Cryptonote", "login"), ("Zano", "login"),
         ("Zano", "eth_submitLogin"), ("Zano", "mining.authorize"))
EXPECTED = {
    (method, family, ordering)
    for family in ("Conceal", "Cryptonote", "Zano")
    for method, ordering in ((CONTENTION, "False"), (CONTENTION, "True"), (CANCELLATION, None))
}
EXPECTED |= {(REQUEST, family, invalid) for family in ("Conceal", "Cryptonote", "Zano")
             for invalid in ("0.5", "0.1", "0.00390625")}
EXPECTED |= {(PROOF, family, invalid) for family in ("Conceal", "Cryptonote")
             for invalid in ("0.5", "0.1", "0.00390625")}
EXPECTED |= {(HINT, family, (protocol, nicehash, oversized)) for family, protocol in PATHS
             for nicehash in ("False", "True") for oversized in ("False", "True")}
EXPECTED |= {(WAIT, family, protocol) for family, protocol in PATHS}


def validate(root):
    results = {}
    for result in root.iter(f"{{{NAMESPACE}}}UnitTestResult"):
        name = result.get("testName", "")
        method = name.split("(", 1)[0]
        if method not in (CONTENTION, CANCELLATION, REQUEST, PROOF, HINT, WAIT):
            continue
        family = re.search(r'\bfamily:\s*"([^"\r\n]+)"', name)
        ordering = re.search(r"\binvalid:\s*([0-9.]+)\b", name) if method in (REQUEST, PROOF) else \
            re.search(r"\bidleOwnsGate:\s*(True|False)\b", name)
        value = ordering.group(1) if ordering else None
        # xUnit can print 0.1 as the round-trip spelling 0.10000000000000001.
        # Compare the represented double, while still rejecting other values.
        if method in (REQUEST, PROOF) and value is not None:
            value = str(float(value))
        if method in (HINT, WAIT):
            protocol = re.search(r'\bprotocol:\s*"([^"\r\n]+)"', name)
            value = protocol.group(1) if protocol else None
            if method == HINT:
                nicehash = re.search(r"\bnicehash:\s*(True|False)\b", name)
                oversized = re.search(r"\boversized:\s*(True|False)\b", name)
                value = (value, nicehash.group(1) if nicehash else None, oversized.group(1) if oversized else None)
        key = (method, family.group(1) if family else None, value)
        if key not in EXPECTED:
            raise ValueError(f"Unrecognized native-family case: {name}")
        results.setdefault(key, []).append(result.get("outcome"))
    for key in sorted(EXPECTED, key=str):
        if results.get(key) != ["Passed"]:
            raise ValueError(f"Native-family evidence missing, duplicated or not passed: {key}: {results.get(key)}")


def fixture():
    root = ET.Element(f"{{{NAMESPACE}}}TestRun")
    for method, family, ordering in sorted(EXPECTED, key=str):
        name = f'{method}(family: "{family}"'
        if method == HINT:
            protocol, nicehash, oversized = ordering
            name += f', protocol: "{protocol}", nicehash: {nicehash}, oversized: {oversized}'
        elif method == WAIT:
            name += f', protocol: "{ordering}"'
        elif ordering is not None:
            name += f", {'invalid' if method in (REQUEST, PROOF) else 'idleOwnsGate'}: {ordering}"
        ET.SubElement(root, f"{{{NAMESPACE}}}UnitTestResult", testName=name + ")", outcome="Passed")
    return root


def self_test():
    validate(fixture())
    verbose = fixture()
    for result in verbose:
        result.set("testName", result.get("testName").replace("invalid: 0.1)", "invalid: 0.10000000000000001)"))
    validate(verbose)
    invalid = []
    for outcome in ("NotExecuted", "Failed", ""):
        root = fixture()
        root[0].set("outcome", outcome)
        invalid.append(root)
    root = fixture()
    root.remove(root[0])
    invalid.append(root)
    root = fixture()
    ET.SubElement(root, root[0].tag, **root[0].attrib)
    invalid.append(root)
    root = fixture()
    root[0].set("testName", root[0].get("testName").replace("Conceal", "RenamedFamily"))
    invalid.append(root)
    invalid.append(ET.Element("TestRun"))
    for root in invalid:
        try:
            validate(root)
        except ValueError:
            continue
        raise AssertionError("Native-family evidence guard accepted an invalid fixture")
    print("Native-family evidence guard negative fixtures passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", nargs="?")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    if args.trx:
        try:
            validate(ET.parse(args.trx).getroot())
        except (OSError, ET.ParseError, ValueError) as error:
            parser.exit(1, f"{error}\n")
        print(f"All {len(EXPECTED)} required Linux-native family/credit cases executed and passed")
    elif not args.self_test:
        parser.error("provide a TRX file or --self-test")


if __name__ == "__main__":
    main()
