#!/usr/bin/env python3
"""Run the shared offline contract against this exact released gate."""
import unittest
import knots_maturity_gate_testlib as support

class GateTests(support.KnotsGateTests):
    release = '29.4.2'
    source_sha = '11c0b99a82b8b1c9c29ab76d9b0507ce1017813665741627b3f3883a4c2f7a7f'
    scripts = support.HISTORICAL_SCRIPTS

if __name__ == '__main__':
    unittest.main()
