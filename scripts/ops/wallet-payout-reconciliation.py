#!/usr/bin/env python3
"""Read-only evidence report; never finishes a batch, changes balances or sends coins.

Inputs are private operator exports, not live credentials. A snapshot digest is an
identity check, not proof of wallet payment. Missing or contradictory evidence
always leaves recipients uncertain and cannot authorize reconcile_batch.
"""
import argparse
from collections import Counter
from decimal import Decimal, InvalidOperation
import hashlib
import json
import os
from pathlib import Path
import re
import struct


def amount(value):
    if not isinstance(value, str):
        raise ValueError('Amounts must be exact decimal strings, never JSON floats')
    result = Decimal(value)
    if not result.is_finite() or result <= 0:
        raise ValueError('Amounts must be finite and positive')
    return result


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
        raise ValueError('Invalid original batch digest')
    result = {'batch': batch, 'authorizes_reconciliation': False,
              'snapshot_verified': False, 'recipients': [], 'warnings': []}
    if snapshot is None:
        result['warnings'].append('Original snapshot unavailable: a digest or current balances cannot reconstruct it.')
        return result
    if snapshot_digest(batch['pool_id'], snapshot) != batch['digest']:
        raise ValueError('Snapshot does not match the original admitted batch; do not substitute current balances')
    result['snapshot_verified'] = True
    addresses = [row['address'] for row in snapshot]
    if len(set(addresses)) != len(addresses):
        raise ValueError('Duplicate snapshot recipients require manual investigation')
    payments = database.get('payments', [])
    transactions = wallet.get('payments', [])
    def identity(row):
        if not isinstance(row['transaction_id'], str) or not row['transaction_id']:
            raise ValueError('Missing transaction receipt identity')
        return row['address'], amount(row['amount']), row['transaction_id']
    persisted = Counter(identity(row) for row in payments)
    observed = Counter(identity(row) for row in transactions)
    for original in snapshot:
        address = original['address']
        expected = amount(original['amount'])
        rows = [key for key in persisted if key[0] == address]
        wallet_rows = [key for key in observed if key[0] == address]
        # Deliberately conservative for split transactions, fee deductions,
        # ambiguous attribution, duplicates and a payment absent from either source.
        confirmed = (len(rows) == 1 and rows == wallet_rows and rows[0][1] == expected and
                     persisted[rows[0]] == 1 and observed[rows[0]] == 1)
        result['recipients'].append({**original,
            'status': 'persisted_and_wallet_evidence_matches' if confirmed else 'uncertain',
            'persisted_receipts': [key[2] for key in rows],
            'wallet_receipts': [key[2] for key in wallet_rows]})
    if any(row['address'] not in addresses for row in payments + transactions):
        result['warnings'].append('Evidence contains recipients outside the original snapshot; isolate the batch before reconciliation.')
    result['warnings'].append('Exports are operator-supplied evidence, not authenticated wallet attestations. Verify chain status, batch attribution, fees and completeness independently. No absence of evidence proves NotAttempted.')
    return result


def read_evidence(path):
    with Path(path).open('rb') as source:
        raw = source.read(32 * 1024 * 1024 + 1)
    if len(raw) > 32 * 1024 * 1024:
        raise ValueError('Evidence exceeds the 32 MiB review budget')
    return json.loads(raw), hashlib.sha256(raw).hexdigest()


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
    except (OSError, ValueError, KeyError, TypeError, InvalidOperation):
        parser.exit(1, 'Cannot verify reconciliation evidence; inspect the private inputs. No financial state changed.\n')


if __name__ == '__main__':
    main()
