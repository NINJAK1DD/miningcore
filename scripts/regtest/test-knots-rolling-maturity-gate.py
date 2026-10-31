#!/usr/bin/env python3
"""Run the shared offline contract against this exact released gate."""
import unittest
import knots_maturity_gate_testlib as support

class GateTests(support.KnotsGateTests):
    release = '29.4.3'
    source_sha = 'f1613e4ec63b7a1d87e9eba10aed730195a08c732e8883c4215e2d4680b9ed83'
    scripts = support.ROLLING_SCRIPTS

if __name__ == '__main__':
    unittest.main()
