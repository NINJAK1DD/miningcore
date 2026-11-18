#!/usr/bin/env python3
"""Exercise conservative recovery of partially paid wallet batches."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('reconciliation', Path(__file__).parents[1] / 'ops/wallet-payout-reconciliation.py')
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)


class ReconciliationTests(unittest.TestCase):
    def setUp(self):
        self.snapshot = [{'address': 'miner-a', 'amount': '1.25'}, {'address': 'miner-b', 'amount': '2.5'}]
        self.payment = {'address': 'miner-a', 'amount': '1.25', 'transaction_id': 'wallet-tx-1'}
        self.database = {'batch': {'id': 'original-batch', 'epoch': 3, 'pool_id': 'pool',
                         'digest': recovery.snapshot_digest('pool', self.snapshot)}, 'payments': [self.payment]}

    def test_partial_payment_after_restart_preserves_uncertainty(self):
        result = recovery.report(self.database, {'payments': [self.payment]}, self.snapshot)
        self.assertTrue(result['snapshot_verified'])
        self.assertFalse(result['authorizes_reconciliation'])
        self.assertEqual(['persisted_and_wallet_evidence_matches', 'uncertain'],
                         [row['status'] for row in result['recipients']])

    def test_missing_snapshot_does_not_infer_recipients_from_current_balances(self):
        result = recovery.report(self.database, {}, None)
        self.assertFalse(result['snapshot_verified'])
        self.assertEqual([], result['recipients'])
        with self.assertRaisesRegex(ValueError, 'original admitted batch'):
            recovery.report(self.database, {}, [{'address': 'miner-a', 'amount': '0.25'}])

    def test_missing_contradictory_or_duplicate_evidence_cannot_confirm_payment(self):
        for rows in ([], [{**self.payment, 'amount': '1.24'}],
                     [{**self.payment, 'transaction_id': 'different-tx'}], [self.payment, self.payment]):
            with self.subTest(rows=rows):
                result = recovery.report(self.database, {'payments': rows}, self.snapshot)
                self.assertTrue(all(row['status'] == 'uncertain' for row in result['recipients']))

    def test_order_and_exact_decimal_evidence(self):
        self.assertEqual('b0721cbaab9da336c2464d0ebd00fb7f7cf389be5dbe882629e8d59b606b7de9',
                         recovery.snapshot_digest('pool', self.snapshot))
        self.assertEqual(recovery.snapshot_digest('pool', self.snapshot),
                         recovery.snapshot_digest('pool', list(reversed(self.snapshot))))
        with self.assertRaises(ValueError):
            recovery.snapshot_digest('pool', [{'address': 'a', 'amount': 1.25}])


if __name__ == '__main__':
    unittest.main()
