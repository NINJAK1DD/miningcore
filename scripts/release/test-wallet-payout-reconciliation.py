#!/usr/bin/env python3
"""Exercise conservative recovery of partially paid wallet batches."""
import importlib.util
import hashlib
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

    def test_evidence_decoder_rejects_duplicate_keys_at_every_depth(self):
        malformed = [
            '{"batch":{},"batch":{}}',
            '{"batch":{"id":"old","id":"new"}}',
            '{"batch":{"digest":"first","digest":"last"}}',
            '{"payments":[{"transaction_id":"old","transaction_id":"new"}]}',
            '{"attributions":[{"batch_id":"old","batch_id":"new"}]}',
            r'{"attributions":[{"evidence_reference":"first","evidence_\u0072eference":"last"}]}',
            '[{"address":"miner-a","amount":"1.25","amount":"1.25"}]',
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'evidence.json'
            for raw in malformed:
                for encoding in ('utf-8', 'utf-16'):
                    with self.subTest(raw=raw, encoding=encoding):
                        path.write_bytes(raw.encode(encoding))
                        with self.assertRaisesRegex(recovery.EvidenceError, '^Duplicate JSON object keys'):
                            recovery.read_evidence(path)
            # Repeated field names in separate objects are valid; retain the hash
            # of the exact bytes, not a normalized or reserialized document.
            raw = b'[ {"address":"miner-a"}, {"address":"miner-b"} ]\n'
            path.write_bytes(raw)
            decoded, digest = recovery.read_evidence(path)
            self.assertEqual([{'address': 'miner-a'}, {'address': 'miner-b'}], decoded)
            self.assertEqual(hashlib.sha256(raw).hexdigest(), digest)

    def test_cli_rejects_duplicates_in_each_input_without_disclosing_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = [root/'db.json', root/'wallet.json', root/'snapshot.json']
            values = [self.database, {'payments': [self.payment]}, self.snapshot]
            command = [sys.executable, str(spec.origin), '--database-export', str(paths[0]),
                       '--wallet-export', str(paths[1]), '--snapshot', str(paths[2]), '--output', str(root/'out.json')]
            for target in paths:
                with self.subTest(target=target.name):
                    for path, value in zip(paths, values):
                        path.write_text(json.dumps(value))
                    target.write_text('{"nested":[{"PRIVATE_KEY_NAME":"PRIVATE_VALUE_A",'
                                      '"PRIVATE_KEY_NAME":"PRIVATE_VALUE_B"}]}')
                    before = [path.read_bytes() for path in paths]
                    run = subprocess.run(command, capture_output=True, text=True, timeout=10)
                    self.assertEqual(1, run.returncode)
                    self.assertEqual('Duplicate JSON object keys are not permitted in reconciliation evidence. '
                                     'No financial state changed.\n', run.stderr)
                    self.assertEqual('', run.stdout)
                    self.assertNotIn('PRIVATE_', run.stderr)
                    self.assertFalse((root/'out.json').exists())
                    self.assertEqual(before, [path.read_bytes() for path in paths])


if __name__ == '__main__':
    unittest.main()
