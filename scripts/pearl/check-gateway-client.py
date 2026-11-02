#!/usr/bin/env python3
"""Check the pinned official Python client against a running pool adapter.

Run in the upstream client's environment. This checks protocol/identity/target
compatibility; it does not replace a real-miner proof and node-acceptance test.
No credentials, addresses, targets, jobs or host details are printed.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys

REVISION = 'd7c517d267f10f5bcbbdc366f3e603942f9a5ab5'


def check(upstream, socket_path):
    revision = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=upstream, text=True).strip()
    if revision != REVISION:
        raise ValueError('wrong official source revision')
    for name in ('pearl-gateway', 'miner-base', 'miner-utils'):
        sys.path.insert(0, str(upstream / 'miner' / name / 'src'))
    from miner_base.gateway_client import MiningClient
    from pearl_gateway.config import MinerRpcConfig
    from loguru import logger
    logger.remove()  # Upstream connection logs contain local endpoint paths.

    settings = MinerRpcConfig(transport='uds', socket_path=str(socket_path))
    def rejected(client, job, code):
        try:
            client.client.call('submitPlainProof', {'plain_proof': 'AA==', 'mining_job': job})
        except Exception as error:
            if not str(error).startswith(f'JSON-RPC error {code}:'):
                raise ValueError('expected an explicit RPC rejection, not a transport failure') from None
        else:
            raise ValueError('invalid job was not rejected')

    with MiningClient(settings) as client:
        client.client._socket.settimeout(70)
        job = client.get_mining_info()
        encoded = job.to_dict()
        if type(job.target) is not int or not 0 < job.target < 2**256 or len(job.incomplete_header_bytes) != 76 or int(job.cert_version) != 3:
            raise ValueError('unsupported job representation')
        if json.loads(json.dumps(encoded))['target'] != job.target:
            raise ValueError('integer target lost precision')
        rejected(client, encoded | {'target': job.target + 1}, -32003)
        rejected(client, encoded | {'wallet': 'injected'}, -32602)
        with MiningClient(settings) as other:
            other.client._socket.settimeout(70)
            rejected(other, encoded, -32003)
    return {'officialRevision': REVISION, 'exactIntegerTarget': True,
            'modifiedTargetRejected': True, 'workerInjectionRejected': True,
            'crossSessionRejected': True}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--upstream', type=Path, required=True)
    parser.add_argument('--socket', type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(check(args.upstream, args.socket), indent=2))
    except Exception:
        parser.exit(1, 'Official client compatibility check failed; inspect the private environment and adapter configuration.\n')
