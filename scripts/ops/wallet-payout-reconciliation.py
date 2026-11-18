#!/usr/bin/env python3
"""Read-only evidence report; never finishes a batch, changes balances or sends coins.

Inputs are private operator exports, not live credentials. A snapshot digest is an
identity check, not proof of wallet payment. Missing or contradictory evidence
always leaves recipients uncertain and cannot authorize reconcile_batch.
"""
import argparse
from collections import defaultdict
from datetime import datetime
from decimal import Decimal, InvalidOperation
import hashlib
import json
import os
from pathlib import Path
import re
import struct


class EvidenceError(ValueError):
    """Only fixed, non-sensitive diagnostics may be exposed by the CLI."""


def amount(value):
    if not isinstance(value, str):
        raise EvidenceError('Amounts must be exact decimal strings, never JSON floats')
    try:
        result = Decimal(value)
    except InvalidOperation:
        raise EvidenceError('Invalid decimal amount') from None
    if not result.is_finite() or result <= 0:
        raise EvidenceError('Amounts must be finite and positive')
    return result


def timestamp(value):
    try:
        result = datetime.fromisoformat(value.replace('Z', '+00:00'))
        if result.utcoffset() is None:
            raise ValueError()
        return result
    except (ValueError, AttributeError, TypeError):
        raise EvidenceError('Evidence timestamps must be ISO 8601 with an explicit timezone') from None


def group(rows, key):
    indexed = defaultdict(list)
    for row in rows:
        indexed[key(row)].append(row)
    return indexed


def transaction_key(row):
    return row.get('pool_id'), row.get('transaction_id')


def attributed(batch, payment, wallet_payment, payment_batches, attributions):
    # The receipt schema has no control-batch foreign key. A separate retained
    # incident record must link the transaction to this batch; time alone cannot.
    if not batch.get('id') or not batch.get('admitted_at') or not batch.get('evidence_until'):
        return False
    key = transaction_key(payment)
    if key[0] != batch['pool_id'] or transaction_key(wallet_payment) != key:
        return False
    receipts, links = payment_batches.get(key, []), attributions.get(key, [])
    if len(receipts) != 1 or len(links) != 1:
        return False
    receipt, link = receipts[0], links[0]
    if link.get('batch_id') != batch['id'] or not isinstance(link.get('evidence_reference'), str) or not link['evidence_reference'].strip():
        return False
    values = [link.get('submitted_at'), receipt.get('created_at'),
              payment.get('created_at'), wallet_payment.get('created_at')]
    if any(value is None for value in values):
        return False
    start, end = timestamp(batch['admitted_at']), timestamp(batch['evidence_until'])
    submitted, recorded, paid, observed = map(timestamp, values)
    return (start <= submitted <= recorded <= end and submitted <= paid <= end and
            submitted <= observed <= end)


def binary_string(value):
    raw = value.encode('utf-8', errors='strict')
    length = len(raw)
    prefix = bytearray()
    while length >= 128:
        prefix.append((length & 127) | 128)
        length >>= 7
    return bytes(prefix) + bytes([length]) + raw


def snapshot_digest(pool, snapshot):
    # Amount strings must be the original invariant .NET G29 representation.
    # We deliberately do not round, infer or reformat missing financial evidence.
    ordered = sorted(snapshot, key=lambda row: (row['address'].encode('utf-16-be'), amount(row['amount'])))
    data = bytearray(binary_string('miningcore-wallet-payout-v1') + binary_string(pool) + struct.pack('<i', len(ordered)))
    for row in ordered:
        data.extend(binary_string(row['address']) + binary_string(row['amount']))
    return hashlib.sha256(data).hexdigest()


def report(database, wallet, snapshot):
    batch = database['batch']
    if not re.fullmatch(r'[0-9a-f]{64}', batch['digest']):
        raise EvidenceError('Invalid original batch digest')
    result = {'batch': batch, 'authorizes_reconciliation': False,
              'snapshot_verified': False, 'recipients': [], 'warnings': []}
    if snapshot is None:
        result['warnings'].append('Original snapshot unavailable: a digest or current balances cannot reconstruct it.')
        return result
    if snapshot_digest(batch['pool_id'], snapshot) != batch['digest']:
        raise EvidenceError('Snapshot does not match the original admitted batch; do not substitute current balances')
    result['snapshot_verified'] = True
    addresses = {row['address'] for row in snapshot}
    if len(addresses) != len(snapshot):
        raise EvidenceError('Duplicate snapshot recipients require manual investigation')
    payments = database.get('payments', [])
    transactions = wallet.get('payments', [])
    def identity(row):
        if not isinstance(row['transaction_id'], str) or not row['transaction_id']:
            raise EvidenceError('Missing transaction receipt identity')
        return row['address'], amount(row['amount']), row['transaction_id']
    # Validate every receipt, including evidence outside the selected snapshot.
    for row in payments + transactions:
        identity(row)
    persisted = group(payments, lambda row: row['address'])
    observed = group(transactions, lambda row: row['address'])
    payment_batches = group(database.get('payment_batches', []), transaction_key)
    attributions = group(database.get('attributions', []), transaction_key)
    for original in snapshot:
        address = original['address']
        expected = amount(original['amount'])
        rows = persisted.get(address, [])
        wallet_rows = observed.get(address, [])
        # Deliberately conservative for split transactions, fee deductions,
        # ambiguous attribution, duplicates and a payment absent from either source.
        confirmed = (len(rows) == len(wallet_rows) == 1 and
                     identity(rows[0]) == identity(wallet_rows[0]) and amount(rows[0]['amount']) == expected and
                     attributed(batch, rows[0], wallet_rows[0], payment_batches, attributions))
        result['recipients'].append({**original,
            'status': 'persisted_and_wallet_evidence_matches' if confirmed else 'uncertain',
            'persisted_receipts': list(dict.fromkeys(row['transaction_id'] for row in rows)),
            'wallet_receipts': list(dict.fromkeys(row['transaction_id'] for row in wallet_rows))})
    if any(row['address'] not in addresses for row in payments + transactions):
        result['warnings'].append('Evidence contains recipients outside the original snapshot; isolate the batch before reconciliation.')
    result['warnings'].append('Exports are operator-supplied evidence, not authenticated wallet attestations. Verify chain status, batch attribution, fees and completeness independently. No absence of evidence proves NotAttempted.')
    result['warnings'].append('A match requires pool and transaction identity, one payment_batches receipt, a retained transaction-to-control-batch attribution, and timestamps inside the admitted evidence window. Missing attribution remains uncertain; never manufacture it from matching amounts or timestamps.')
    return result


def unique_object(pairs):
    # JSON decodes escaped names before invoking this hook, at every object depth.
    # Reject even identical duplicates: neither first-wins nor last-wins evidence
    # is an unambiguous source. Never expose the offending key or its values.
    result = {}
    for key, value in pairs:
        if key in result:
            raise EvidenceError('Duplicate JSON object keys are not permitted in reconciliation evidence')
        result[key] = value
    return result


def read_evidence(path):
    with Path(path).open('rb') as source:
        raw = source.read(32 * 1024 * 1024 + 1)
    if len(raw) > 32 * 1024 * 1024:
        raise EvidenceError('Evidence exceeds the 32 MiB review budget')
    return json.loads(raw, object_pairs_hook=unique_object), hashlib.sha256(raw).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database-export', required=True)
    parser.add_argument('--wallet-export', required=True)
    parser.add_argument('--snapshot')
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    try:
        database, database_hash = read_evidence(args.database_export)
        wallet, wallet_hash = read_evidence(args.wallet_export)
        snapshot, snapshot_hash = read_evidence(args.snapshot) if args.snapshot else (None, None)
        result = report(database, wallet, snapshot)
        result['evidence_sha256'] = {'database_export': database_hash, 'wallet_export': wallet_hash,
                                     'original_snapshot': snapshot_hash}
        # Refuse overwrites/symlinks. On Windows protect the parent directory ACL;
        # POSIX additionally gets an owner-only file even with a permissive umask.
        fd = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w', encoding='utf-8') as output:
            json.dump(result, output, ensure_ascii=False, indent=2)
            output.write('\n')
        print('Read-only reconciliation evidence report created; no financial action authorized.')
    except EvidenceError as error:
        parser.exit(1, str(error) + '. No financial state changed.\n')
    except (OSError, ValueError, KeyError, TypeError, InvalidOperation, AttributeError, OverflowError):
        parser.exit(1, 'Cannot verify reconciliation evidence; inspect the private inputs. No financial state changed.\n')


if __name__ == '__main__':
    main()
