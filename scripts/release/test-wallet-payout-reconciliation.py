#!/usr/bin/env python3
"""Exercise conservative recovery of partially paid wallet batches."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

spec = importlib.util.spec_from_file_location('reconciliation', Path(__file__).parents[1] / 'ops/wallet-payout-reconciliation.py')
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)


class ReconciliationTests(unittest.TestCase):
    def setUp(self):
        self.snapshot = [{'address': 'miner-a', 'amount': '1.25'}, {'address': 'miner-b', 'amount': '2.5'}]
        self.payment = {'address': 'miner-a', 'amount': '1.25', 'transaction_id': 'wallet-tx-1',
                        'pool_id': 'pool', 'created_at': '2026-11-18T12:01:00Z'}
        self.database = {'batch': {'id': 'original-batch', 'epoch': 3, 'pool_id': 'pool',
                         'admitted_at': '2026-11-18T12:00:00Z', 'evidence_until': '2026-11-18T12:05:00Z',
                         'digest': recovery.snapshot_digest('pool', self.snapshot)}, 'payments': [self.payment],
                         'payment_batches': [{'pool_id': 'pool', 'transaction_id': 'wallet-tx-1',
                                              'created_at': '2026-11-18T12:01:00Z'}],
                         'attributions': [{'batch_id': 'original-batch', 'pool_id': 'pool',
                                           'transaction_id': 'wallet-tx-1', 'submitted_at': '2026-11-18T12:00:30Z',
                                           'evidence_reference': 'retained-incident-record'}]}

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

    def test_historical_wrong_pool_or_missing_batch_attribution_stays_uncertain(self):
        mutations = [
            lambda db: db['attributions'][0].update(batch_id='unrelated-batch'),
            lambda db: db['attributions'][0].update(submitted_at='2026-11-17T12:00:30Z'),
            lambda db: db['payments'][0].update(created_at='2026-11-17T12:01:00Z'),
            lambda db: db['payment_batches'][0].update(created_at='2026-11-17T12:01:00Z'),
            lambda db: db['payments'][0].update(pool_id='other-pool'),
            lambda db: db['attributions'][0].update(evidence_reference=''),
            lambda db: db.update(attributions=[]),
            lambda db: db.update(payment_batches=[]),
            lambda db: db['attributions'].append(dict(db['attributions'][0])),
            lambda db: db['payment_batches'].append(dict(db['payment_batches'][0])),
            lambda db: db['batch'].pop('admitted_at'),
            lambda db: db['batch'].update(evidence_until='2026-11-18T11:00:00Z'),
        ]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                db = json.loads(json.dumps(self.database))
                mutate(db)
                result = recovery.report(db, {'payments': db['payments']}, self.snapshot)
                self.assertEqual('uncertain', result['recipients'][0]['status'])
        # Matching transaction/amount alone cannot bind a historical wallet entry.
        wallet = {'payments': [{**self.payment, 'created_at': '2026-11-17T12:01:00Z'}]}
        self.assertEqual('uncertain', recovery.report(self.database, wallet, self.snapshot)['recipients'][0]['status'])

    def test_ten_thousand_recipients_with_mixed_evidence(self):
        snapshot, payments, wallet, batches, links, expected = [], [], [], [], [], []
        for i in range(10000):
            row = {**self.payment, 'address': f'miner-{i}', 'transaction_id': f'tx-{i}'}
            snapshot.append({'address': row['address'], 'amount': row['amount']})
            payments.append(row)
            batches.append({**self.database['payment_batches'][0], 'transaction_id': row['transaction_id']})
            links.append({**self.database['attributions'][0], 'transaction_id': row['transaction_id']})
            if i % 4 == 0:
                wallet.append(row)
            elif i % 4 == 2:
                wallet.append({**row, 'amount': '1.24'})
            elif i % 4 == 3:
                wallet.extend([row, row])
            expected.append('persisted_and_wallet_evidence_matches' if i % 4 == 0 else 'uncertain')
        db = {**self.database, 'batch': {**self.database['batch'], 'digest': recovery.snapshot_digest('pool', snapshot)},
              'payments': payments, 'payment_batches': batches, 'attributions': links}
        started = time.monotonic()
        result = recovery.report(db, {'payments': wallet}, snapshot)
        elapsed = time.monotonic() - started
        self.assertEqual(expected, [row['status'] for row in result['recipients']])
        self.assertFalse(result['authorizes_reconciliation'])
        # Generous hang guard, not a throughput SLA on shared CI runners.
        self.assertLess(elapsed, 20)
        print(f'Reconciliation: 10000 recipients / {len(payments) + len(wallet)} payment rows in {elapsed:.3f}s')

    def test_cli_reports_only_safe_validation_diagnostics(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            db, wallet, snapshot = root/'db.json', root/'wallet.json', root/'snapshot.json'
            db.write_text(json.dumps(self.database))
            wallet.write_text(json.dumps({'payments': [self.payment]}))
            command = [sys.executable, str(spec.origin), '--database-export', str(db),
                       '--wallet-export', str(wallet), '--snapshot', str(snapshot), '--output', str(root/'out.json')]
            for value, diagnostic in [
                ([{'address': 'PRIVATE_CANARY', 'amount': 1.25}], 'Amounts must be exact decimal strings'),
                ([{'address': 'PRIVATE_CANARY', 'amount': '1.25'}], 'Snapshot does not match'),
            ]:
                snapshot.write_text(json.dumps(value))
                run = subprocess.run(command, capture_output=True, text=True, timeout=10)
                self.assertEqual(1, run.returncode)
                self.assertIn(diagnostic, run.stderr)
                self.assertNotIn('PRIVATE_CANARY', run.stderr)
                self.assertFalse((root/'out.json').exists())
            duplicate = [self.snapshot[0], self.snapshot[0]]
            snapshot.write_text(json.dumps(duplicate))
            db.write_text(json.dumps({**self.database, 'batch': {**self.database['batch'],
                         'digest': recovery.snapshot_digest('pool', duplicate)}}))
            run = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertIn('Duplicate snapshot recipients', run.stderr)
            snapshot.write_text('{"PRIVATE_CANARY":invalid-json}')
            run = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertEqual(1, run.returncode)
            self.assertNotIn('PRIVATE_CANARY', run.stderr)
            self.assertIn('Cannot verify reconciliation evidence', run.stderr)


if __name__ == '__main__':
    unittest.main()
