#!/usr/bin/env python3
"""Local, identity-bound adapter for Pearl v1.5.5's Unix-socket miner RPC.

Python 3.12+, standard library only. This is a pool client, not a solo gateway:
Miningcore alone verifies proofs, accounts shares, and submits candidates.
"""
import argparse
import asyncio
import base64
from collections import OrderedDict
import contextlib
import json
import os
from pathlib import Path
import re
import ssl
import stat
import time

MAX_FRAME = 11_200_000
MAX_SESSIONS = 4
RPC_TIMEOUT = 60
JOB_AGE = 90


class RpcError(Exception):
    def __init__(self, code, message):
        self.code, self.message = code, message


def strict_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError('duplicate JSON key')
            result[key] = value
        return result
    def constant(_):
        raise ValueError('non-finite JSON number')
    return json.loads(raw, object_pairs_hook=pairs, parse_constant=constant)


def load_config(path):
    # O_NOFOLLOW also prevents accidentally reading a substituted secret file.
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(fd) as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
            raise ValueError('configuration must be a private, user-owned regular file')
        config = strict_json(source.read(16_385))
    allowed = {'socket', 'host', 'port', 'wallet', 'ca_file', 'loopback_plaintext'}
    if not isinstance(config, dict) or set(config) - allowed:
        raise ValueError('unsupported configuration')
    if not isinstance(config.get('host'), str) or not 1 <= len(config['host']) <= 253:
        raise ValueError('pool host required')
    if type(config.get('port')) is not int or not 1 <= config['port'] <= 65535:
        raise ValueError('invalid pool port')
    if not isinstance(config.get('wallet'), str) or not re.fullmatch(r'[a-z0-9]{10,100}\.[A-Za-z0-9_-]{1,64}', config['wallet']):
        raise ValueError('a fixed wallet.worker identity is required')
    if type(config.get('loopback_plaintext', False)) is not bool:
        raise ValueError('invalid transport setting')
    if config.get('loopback_plaintext') and config['host'] not in ('127.0.0.1', '::1'):
        raise ValueError('plaintext is restricted to literal loopback addresses')
    if not isinstance(config.get('socket'), str) or not Path(config['socket']).is_absolute() or len(os.fsencode(config['socket'])) > 100:
        raise ValueError('an absolute short Unix socket path is required')
    return config


async def send(writer, value):
    writer.write(json.dumps(value, separators=(',', ':'), allow_nan=False).encode() + b'\n')
    await asyncio.wait_for(writer.drain(), RPC_TIMEOUT)


class Session:
    def __init__(self, config):
        self.config = config
        self.jobs = OrderedDict()
        self.writer = None
        self.reader_task = None
        self.pending = None
        self.sequence = 0
        self.last_submit = float('-inf')
        self.tip = None

    async def connect(self):
        tls = None
        if not self.config.get('loopback_plaintext'):
            tls = ssl.create_default_context(cafile=self.config.get('ca_file'))
            tls.minimum_version = ssl.TLSVersion.TLSv1_2
        reader, self.writer = await asyncio.wait_for(asyncio.open_connection(
            self.config['host'], self.config['port'], ssl=tls, limit=32768), 10)
        self.reader_task = asyncio.create_task(self.receive(reader))
        await self.call('mining.authorize', {
            'wallet': self.config['wallet'], 'agent': 'Miningcore-Pearl-Gateway/1', 'type': 'v2'})

    def notify(self, job):
        if not isinstance(job, dict) or not re.fullmatch(r'[0-9a-fA-F]{32}', str(job.get('job_id', ''))):
            raise ValueError('invalid pool job')
        header_hex, target_hex = job.get('header'), job.get('target')
        if not isinstance(header_hex, str) or not re.fullmatch(r'[0-9a-fA-F]{152}', header_hex):
            raise ValueError('invalid pool header')
        if not isinstance(target_hex, str) or not re.fullmatch(r'[0-9a-fA-F]{64}', target_hex):
            raise ValueError('invalid pool target')
        target = int(target_hex, 16)
        if not 0 < target < 2**256 or type(job.get('cert_version')) is not int or job['cert_version'] != 3:
            raise ValueError('unsupported pool job')
        header = bytes.fromhex(header_hex)
        if self.tip != header[4:36]:
            self.jobs.clear()
            self.tip = header[4:36]
        key = (base64.b64encode(header).decode(), target, 3)
        self.jobs[key] = (job['job_id'], time.monotonic())
        self.jobs.move_to_end(key)
        while len(self.jobs) > 8:
            self.jobs.popitem(last=False)

    async def receive(self, reader):
        try:
            while True:
                raw = await asyncio.wait_for(reader.readline(), 120)
                if not raw or len(raw) > 32768 or not raw.endswith(b'\n'):
                    raise ValueError('pool disconnected or invalid frame')
                value = strict_json(raw)
                if not isinstance(value, dict):
                    raise ValueError('invalid pool response')
                if value.get('method') == 'mining.notify':
                    self.notify(value.get('params'))
                elif self.pending and type(value.get('id')) is int and value['id'] == self.sequence:
                    if self.pending.done():
                        raise ValueError('duplicate pool response')
                    self.pending.set_result(value)
                else:
                    raise ValueError('unexpected pool response')
        except (Exception, asyncio.CancelledError):
            self.jobs.clear()
            if self.pending and not self.pending.done():
                self.pending.set_exception(RpcError(-32000, 'Pool connection unavailable'))

    async def call(self, method, params):
        if self.reader_task.done():
            raise RpcError(-32000, 'Pool connection unavailable')
        self.sequence += 1
        self.pending = asyncio.get_running_loop().create_future()
        try:
            await send(self.writer, {'id': self.sequence, 'method': method, 'params': params})
            value = await asyncio.wait_for(self.pending, RPC_TIMEOUT)
            if value.get('error') is not None or value.get('result') is not True:
                # Never relay upstream diagnostic text, addresses or exception data.
                raise RpcError(-32002, 'Pool rejected request; no share acceptance confirmed')
        finally:
            if self.pending and not self.pending.done():
                self.pending.cancel()
            self.pending = None

    async def dispatch(self, request):
        if not isinstance(request, dict) or set(request) != {'jsonrpc', 'id', 'method', 'params'} or request['jsonrpc'] != '2.0':
            raise RpcError(-32600, 'Invalid request')
        identity = request['id']
        if identity is not None and not (type(identity) is int and abs(identity) <= 2**53 or isinstance(identity, str) and len(identity) <= 64):
            raise RpcError(-32600, 'Invalid request id')
        params = request['params']
        if not isinstance(params, dict):
            raise RpcError(-32602, 'Invalid parameters')
        if self.reader_task.done():
            raise RpcError(-32000, 'Pool connection unavailable')
        if request['method'] == 'getMiningInfo':
            if params:
                raise RpcError(-32602, 'getMiningInfo takes no parameters')
            if not self.jobs:
                raise RpcError(-32001, 'mining_paused')
            key = next(reversed(self.jobs))
            if time.monotonic() - self.jobs[key][1] > 30:
                raise RpcError(-32001, 'mining_paused')
            return dict(zip(('incomplete_header_bytes', 'target', 'cert_version'), key))
        if request['method'] != 'submitPlainProof':
            raise RpcError(-32601, 'Method not supported')
        if set(params) != {'plain_proof', 'mining_job'}:
            raise RpcError(-32602, 'Invalid proof parameters')
        job, proof = params['mining_job'], params['plain_proof']
        if not isinstance(job, dict) or set(job) != {'incomplete_header_bytes', 'target', 'cert_version'}:
            raise RpcError(-32602, 'Invalid mining job')
        if not isinstance(job['incomplete_header_bytes'], str) or type(job['target']) is not int or type(job['cert_version']) is not int:
            raise RpcError(-32602, 'Invalid mining job types')
        key = (job['incomplete_header_bytes'], job['target'], job['cert_version'])
        assigned = self.jobs.get(key)
        if assigned is None or time.monotonic() - assigned[1] > JOB_AGE:
            raise RpcError(-32003, 'Unknown, modified or expired session job')
        if not isinstance(proof, str) or not 1 <= len(proof) <= 11_184_812:
            raise RpcError(-32602, 'Invalid proof size')
        now = time.monotonic()
        if now - self.last_submit < 1:
            raise RpcError(-32004, 'Submit rate exceeded')
        self.last_submit = now
        await self.call('mining.submit', {'job_id': assigned[0], 'plain_proof': proof})
        return True  # Only the pool's verified, durable receipt can reach here.

    async def close(self):
        if self.reader_task:
            self.reader_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self.reader_task
        if self.writer:
            self.writer.close()
            with contextlib.suppress(Exception):
                await asyncio.wait_for(self.writer.wait_closed(), 5)


class Adapter:
    def __init__(self, config):
        self.config, self.clients = config, set()

    async def handle(self, reader, writer):
        task = asyncio.current_task()
        if len(self.clients) >= MAX_SESSIONS:
            writer.close()
            return
        self.clients.add(task)
        session = Session(self.config)
        try:
            await session.connect()
            while True:
                raw = await asyncio.wait_for(reader.readline(), 120)
                if not raw:
                    break
                if len(raw) > MAX_FRAME or not raw.endswith(b'\n'):
                    break
                request = strict_json(raw)
                await asyncio.sleep(0.05)  # Bound local polling to 20 requests/second/session.
                identity = request.get('id') if isinstance(request, dict) else None
                # Never echo an unbounded/malformed id, even in an error response.
                if not (identity is None or type(identity) is int and abs(identity) <= 2**53 or isinstance(identity, str) and len(identity) <= 64):
                    identity = None
                try:
                    result = await session.dispatch(request)
                    await send(writer, {'jsonrpc': '2.0', 'id': identity, 'result': result})
                except RpcError as error:
                    await send(writer, {'jsonrpc': '2.0', 'id': identity,
                                        'error': {'code': error.code, 'message': error.message}})
        except (Exception, asyncio.CancelledError):
            # Malformed frames, timeouts, transport loss: close, never acknowledge.
            pass
        finally:
            await session.close()
            writer.close()
            with contextlib.suppress(Exception):
                await asyncio.wait_for(writer.wait_closed(), 5)
            self.clients.discard(task)

    async def run(self):
        path = Path(self.config['socket'])
        path.parent.mkdir(mode=0o700, parents=False, exist_ok=True)
        info = path.parent.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
            raise ValueError('socket directory must be private and user-owned')
        if path.exists() or path.is_symlink():
            raise ValueError('socket path already exists; inspect and remove stale socket manually')
        old_mask = os.umask(0o077)
        try:
            server = await asyncio.start_unix_server(self.handle, path=str(path), limit=MAX_FRAME)
            path.chmod(0o600)
            inode = path.stat().st_ino
        finally:
            os.umask(old_mask)
        try:
            async with server:
                await server.serve_forever()
        finally:
            for task in list(self.clients):
                task.cancel()
            await asyncio.gather(*list(self.clients), return_exceptions=True)
            if path.exists() and path.lstat().st_ino == inode:
                path.unlink()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True)
    args = parser.parse_args()
    try:
        asyncio.run(Adapter(load_config(args.config)).run())
    except KeyboardInterrupt:
        pass
    except Exception:
        parser.exit(1, 'Gateway adapter failed; check private configuration, permissions and pool availability.\n')
