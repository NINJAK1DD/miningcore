#!/usr/bin/env python3
"""Bounded gateway transport and security contracts; no miner/node required."""
import asyncio
import ast
import base64
import importlib.util
import json
import os
import operator
from pathlib import Path
import re
import tempfile
import time
import unittest

spec = importlib.util.spec_from_file_location('gateway_adapter', Path(__file__).with_name('gateway-adapter.py'))
gateway = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gateway)


def notification(seed=0, target=2**252 + 123456789):
    return {'job_id': f'{seed:032x}', 'header': (b'\x01' * 76).hex(),
            'target': f'{target:064x}', 'cert_version': 3}


def request(method='getMiningInfo', params=None, identity=1):
    return {'jsonrpc': '2.0', 'id': identity, 'method': method, 'params': params or {}}


def integer_expression(expression, bindings):
    """Read source constants without executing source code; all bounds are positive integers."""
    operations = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul,
                  ast.Div: operator.floordiv, ast.FloorDiv: operator.floordiv}
    def evaluate(node):
        if isinstance(node, ast.Constant) and type(node.value) is int:
            return node.value
        if isinstance(node, ast.Name) and node.id in bindings:
            return bindings[node.id]
        if isinstance(node, ast.BinOp) and type(node.op) in operations:
            return operations[type(node.op)](evaluate(node.left), evaluate(node.right))
        raise ValueError('unsupported constant expression; update the proof-limit guard')
    expression = expression.replace('PearlPlainProof.', '').replace('PearlProofEncoding.', '')
    return evaluate(ast.parse('(' + expression + ')', mode='eval').body)


class ProofLimitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = Path(__file__).resolve().parents[2]
        cls.sources = {name: (root / path).read_text() for name, path in {
            'plain': 'src/Miningcore/Blockchain/Pearl/PearlPlainProof.cs',
            'wire': 'src/Miningcore/Blockchain/Pearl/PearlProofEncoding.cs',
            'frame': 'src/Miningcore/Stratum/StratumInputPolicy.cs',
            'native': 'src/Native/pearl-proof-worker/src/main.rs'}.items()}
        cls.gateway_limits = (gateway.MAX_PROOF_BYTES, gateway.MAX_WIRE_BYTES,
                              gateway.MAX_ENCODED_CHARACTERS, gateway.MAX_FRAME)

    def check_limits(self, sources, gateway_limits):
        def constant(source, pattern, bindings=None):
            match = re.search(pattern, sources[source])
            self.assertIsNotNone(match, 'missing proof limit in ' + source)
            return integer_expression(match[1], bindings or {})
        plain = constant('plain', r'const int PoolMaximumBytes\s*=([^;]+);')
        native = constant('native', r'const MAX_PROOF: usize\s*=([^;]+);')
        wire = constant('wire', r'const int MaximumWireBytes\s*=([^;]+);', {'PoolMaximumBytes': plain})
        encoded = constant('wire', r'const int MaximumEncodedCharacters\s*=([^;]+);', {'MaximumWireBytes': wire})
        frame = constant('frame', r'int MaximumLength => ProofSized\s*\?([^:]+):', {'MaximumEncodedCharacters': encoded})
        self.assertEqual(plain, native, 'managed/native proof limit drift')
        self.assertEqual((plain, wire, encoded, frame), gateway_limits, 'managed/gateway proof limit drift')

    def test_actual_managed_native_and_gateway_limits_agree(self):
        self.check_limits(self.sources, self.gateway_limits)

    def test_drift_in_each_boundary_is_detected(self):
        for source, before, after in [('plain', '32 * 1040', '32 * 1041'),
                                      ('native', '32 * 1040', '32 * 1041'),
                                      ('wire', '+ 4096', '+ 4097'),
                                      ('wire', '+ 2) / 3', '+ 2) / 2'),
                                      ('frame', '+ 4096', '+ 4097')]:
            self.assertIn(before, self.sources[source])
            changed = self.sources | {source: self.sources[source].replace(before, after, 1)}
            with self.subTest(source=source, change=after), self.assertRaises(AssertionError):
                self.check_limits(changed, self.gateway_limits)
        for index in range(len(self.gateway_limits)):
            changed = list(self.gateway_limits)
            changed[index] += 1
            with self.subTest(gateway_boundary=index), self.assertRaises(AssertionError):
                self.check_limits(self.sources, tuple(changed))


class ConfigurationTests(unittest.TestCase):
    def test_duplicate_and_nonfinite_json_rejected(self):
        for raw in ('{"a":1,"a":2}', '{"a":NaN}', '{"a":Infinity}'):
            with self.assertRaises(ValueError): gateway.strict_json(raw)

    def test_private_configuration_and_loopback_policy(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'config.json'
            config = {'socket': directory + '/rpc.sock', 'host': '127.0.0.1', 'port': 1000,
                      'wallet': 'prl1exampleaddress.worker', 'loopback_plaintext': True}
            path.write_text(json.dumps(config)); path.chmod(0o600)
            self.assertEqual(gateway.load_config(path), config)
            path.chmod(0o644)
            with self.assertRaises(ValueError): gateway.load_config(path)
            path.chmod(0o600)
            for update in ({'host': 'example.org'}, {'host': 'localhost'}, {'port': True},
                           {'wallet': 'prl1exampleaddress'}, {'loopback_plaintext': 'true'},
                           {'socket': 'relative.sock'}, {'extra': 'unrecognized'}):
                path.write_text(json.dumps(config | update))
                with self.subTest(update=update), self.assertRaises(ValueError): gateway.load_config(path)
            link = Path(directory) / 'link'; link.symlink_to(path)
            with self.assertRaises(OSError): gateway.load_config(link)


class SessionTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.session = gateway.Session({})
        self.session.reader_task = asyncio.create_task(asyncio.sleep(3600))
        self.session.notify(notification())
        self.calls = []
        async def call(method, params): self.calls.append((method, params))
        self.session.call = call

    async def asyncTearDown(self):
        await self.session.close()

    async def job(self):
        return await self.session.dispatch(request())

    async def submit(self, job, proof='AA=='):
        return await self.session.dispatch(request('submitPlainProof', {'mining_job': job, 'plain_proof': proof}))

    async def test_exact_256_bit_target_and_header(self):
        job = await self.job()
        self.assertEqual(job['target'], 2**252 + 123456789)
        self.assertEqual(base64.b64decode(job['incomplete_header_bytes']), b'\x01' * 76)
        self.assertTrue(await self.submit(job))
        self.assertEqual(self.calls, [('mining.submit', {'job_id': '0' * 32, 'plain_proof': 'AA=='})])

    async def test_modified_job_and_identity_injection_rejected(self):
        job = await self.job()
        for update in ({'target': job['target'] + 1}, {'target': float(job['target'])},
                       {'cert_version': True}, {'cert_version': 2}, {'incomplete_header_bytes': 'AA=='},
                       {'wallet': 'another.worker'}):
            with self.subTest(update=update), self.assertRaises(gateway.RpcError): await self.submit(job | update)
        self.assertEqual(self.calls, [])

    async def test_job_cannot_cross_session(self):
        other = gateway.Session({}); other.reader_task = self.session.reader_task
        job = await self.job()
        with self.assertRaises(gateway.RpcError):
            await other.dispatch(request('submitPlainProof', {'mining_job': job, 'plain_proof': 'AA=='}))

    async def test_no_early_or_false_success(self):
        gate = asyncio.Event()
        async def delayed(method, params):
            await gate.wait()
            raise gateway.RpcError(-32002, 'rejected')
        self.session.call = delayed
        task = asyncio.create_task(self.submit(await self.job()))
        await asyncio.sleep(.01); self.assertFalse(task.done())
        gate.set()
        with self.assertRaises(gateway.RpcError): await task

    async def test_rate_limit(self):
        job = await self.job(); await self.submit(job)
        with self.assertRaises(gateway.RpcError): await self.submit(job)
        self.assertEqual(len(self.calls), 1)

    async def test_age_and_reorg(self):
        job = await self.job(); key = next(iter(self.session.jobs))
        self.session.jobs[key] = ('0' * 32, time.monotonic() - 91)
        with self.assertRaises(gateway.RpcError): await self.submit(job)
        with self.assertRaises(gateway.RpcError): await self.job()
        self.session.notify(notification(1) | {'header': (b'\x02' * 76).hex()})
        with self.assertRaises(gateway.RpcError): await self.submit(job)
        self.assertEqual(len(self.session.jobs), 1)

    async def test_job_retention_bound(self):
        first = await self.job()
        for i in range(1, 20): self.session.notify(notification(i, 2**252 + i))
        self.assertEqual(len(self.session.jobs), 8)
        with self.assertRaises(gateway.RpcError): await self.submit(first)

    async def test_invalid_envelopes(self):
        for value in (None, [], request() | {'extra': 1}, request(identity=True),
                      request(identity='x' * 65), request(identity=2**54), request(params={'wallet': 'x'}),
                      request('unknown'), request() | {'params': []}):
            with self.subTest(value=value), self.assertRaises(gateway.RpcError): await self.session.dispatch(value)

    async def test_proof_size_bound(self):
        for proof in ('', None, 1, 'x' * (gateway.MAX_ENCODED_CHARACTERS + 1)):
            with self.assertRaises(gateway.RpcError): await self.submit(await self.job(), proof)
        self.assertEqual(self.calls, [])

    async def test_invalid_encoding_cannot_expand_upstream_frame(self):
        for proof in ('x', 'AA==\n', '\\' * 1024, '"' * 1024, '\u00e9' * 1024, 'A==='):
            with self.subTest(proof=proof[:8]), self.assertRaises(gateway.RpcError):
                await self.submit(await self.job(), proof)
        self.assertEqual(self.calls, [])

    async def test_encoded_boundary_and_decoded_wire_limit(self):
        # Both lengths fit the base64 ceiling; padding determines decoded length.
        oversized = base64.b64encode(bytes(gateway.MAX_WIRE_BYTES + 1)).decode()
        with self.assertRaises(gateway.RpcError): await self.submit(await self.job(), oversized)
        maximum = base64.b64encode(bytes(gateway.MAX_WIRE_BYTES)).decode()
        self.assertEqual(len(maximum), gateway.MAX_ENCODED_CHARACTERS)
        self.assertTrue(await self.submit(await self.job(), maximum))
        self.assertEqual(len(self.calls), 1)


class TransportTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.auth = []; self.submits = []; self.pool_writers = []
        async def pool(reader, writer):
            self.pool_writers.append(writer)
            try:
                auth = json.loads(await reader.readline()); self.auth.append(auth)
                await gateway.send(writer, {'method': 'mining.notify', 'params': notification()})
                await gateway.send(writer, {'id': auth['id'], 'result': True, 'error': None})
                while raw := await reader.readline():
                    value = json.loads(raw); self.submits.append(value)
                    if value['params']['plain_proof'] == 'dGltZW91dA==':
                        writer.close(); return
                    if value['params']['plain_proof'] == 'cmVqZWN0':
                        await gateway.send(writer, {'id': value['id'], 'error': {'code': 20, 'message': 'private diagnostic'}})
                    else:
                        await gateway.send(writer, {'id': value['id'], 'result': True, 'error': None})
            finally: writer.close()
        self.pool = await asyncio.start_server(pool, '127.0.0.1', 0, limit=gateway.MAX_FRAME)
        self.temp = tempfile.TemporaryDirectory(); self.path = Path(self.temp.name) / 'rpc.sock'
        self.config = {'socket': str(self.path), 'host': '127.0.0.1', 'port': self.pool.sockets[0].getsockname()[1],
                       'wallet': 'prl1exampleaddress.fixed', 'loopback_plaintext': True}
        self.adapter = gateway.Adapter(self.config)
        self.server_task = asyncio.create_task(self.adapter.run())
        for _ in range(100):
            if self.path.exists(): break
            await asyncio.sleep(.005)
        self.writers = []

    async def asyncTearDown(self):
        for writer in self.writers: writer.close(); await writer.wait_closed()
        self.server_task.cancel()
        await asyncio.gather(self.server_task, return_exceptions=True)
        for writer in self.pool_writers: writer.close()
        self.pool.close(); await self.pool.wait_closed(); self.temp.cleanup()

    async def connect(self):
        reader, writer = await asyncio.open_unix_connection(str(self.path))
        self.writers.append(writer)
        return reader, writer

    async def exchange(self, reader, writer, value):
        await gateway.send(writer, value)
        return json.loads(await asyncio.wait_for(reader.readline(), 2))

    async def test_wire_success_omits_error_and_binds_worker(self):
        reader, writer = await self.connect()
        response = await self.exchange(reader, writer, request())
        self.assertNotIn('error', response)
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o600)
        job = response['result']
        result = await self.exchange(reader, writer, request('submitPlainProof', {'mining_job': job, 'plain_proof': 'AA=='}))
        self.assertEqual(result, {'jsonrpc': '2.0', 'id': 1, 'result': True})
        self.assertEqual(self.auth[0]['params']['wallet'], self.config['wallet'])

    async def test_rejection_is_error_without_upstream_private_text(self):
        reader, writer = await self.connect()
        job = (await self.exchange(reader, writer, request()))['result']
        response = await self.exchange(reader, writer, request('submitPlainProof', {'mining_job': job, 'plain_proof': 'cmVqZWN0'}))
        self.assertIn('error', response); self.assertNotIn('result', response)
        self.assertNotIn('private diagnostic', json.dumps(response))

    async def test_disconnect_never_acknowledges(self):
        reader, writer = await self.connect()
        job = (await self.exchange(reader, writer, request()))['result']
        response = await self.exchange(reader, writer, request('submitPlainProof', {'mining_job': job, 'plain_proof': 'dGltZW91dA=='}))
        self.assertIn('error', response); self.assertNotIn('result', response)

    async def test_malformed_frame_closes(self):
        reader, writer = await self.connect()
        writer.write(b'{"id":1,"id":2}\n'); await writer.drain()
        self.assertEqual(await asyncio.wait_for(reader.read(), 2), b'')

    async def test_oversized_proof_rejected_locally_then_same_session_submits(self):
        reader, writer = await self.connect()
        job = (await self.exchange(reader, writer, request()))['result']
        response = await self.exchange(reader, writer, request('submitPlainProof', {
            'mining_job': job, 'plain_proof': 'A' * (gateway.MAX_ENCODED_CHARACTERS + 4)}))
        self.assertEqual(response['error'], {'code': -32602, 'message': 'Invalid proof size'})
        self.assertEqual(self.submits, [])
        maximum = base64.b64encode(bytes(gateway.MAX_WIRE_BYTES)).decode()
        response = await self.exchange(reader, writer, request('submitPlainProof', {
            'mining_job': job, 'plain_proof': maximum}))
        self.assertTrue(response['result'])
        self.assertEqual(len(self.auth), 1)
        self.assertEqual(len(self.submits), 1)
        self.assertLessEqual(len(json.dumps(self.submits[0], separators=(',', ':')).encode()) + 1,
                             gateway.MAX_FRAME)

    async def test_connection_limit(self):
        connections = [await self.connect() for _ in range(gateway.MAX_SESSIONS)]
        for reader, writer in connections: await self.exchange(reader, writer, request())
        reader, writer = await self.connect()
        self.assertEqual(await asyncio.wait_for(reader.read(), 2), b'')
        self.assertEqual(len(self.auth), gateway.MAX_SESSIONS)

    async def test_non_loopback_plaintext_refused_by_configuration(self):
        # Policy is enforced before any transport is opened.
        path = Path(self.temp.name) / 'config.json'; path.write_text(json.dumps(self.config | {'host': 'example.org'})); path.chmod(0o600)
        with self.assertRaises(ValueError): gateway.load_config(path)

    async def test_oversized_frame_closes_without_upstream_submit(self):
        reader, writer = await self.connect()
        # Exercise the actual StreamReader limit, not just a size helper.
        writer.write(b'x' * (gateway.MAX_FRAME + 1) + b'\n')
        try: await writer.drain()
        except ConnectionError: pass
        try: self.assertEqual(await asyncio.wait_for(reader.read(), 5), b'')
        except ConnectionResetError: pass
        self.assertEqual(self.submits, [])

    async def test_existing_socket_not_replaced(self):
        inode = self.path.stat().st_ino
        with self.assertRaises(ValueError): await gateway.Adapter(self.config).run()
        self.assertEqual(self.path.stat().st_ino, inode)

    async def test_public_socket_directory_rejected(self):
        directory = Path(self.temp.name) / 'public'; directory.mkdir(mode=0o755)
        directory.chmod(0o755)
        with self.assertRaises(ValueError):
            await gateway.Adapter(self.config | {'socket': str(directory / 'rpc.sock')}).run()


if __name__ == '__main__':
    unittest.main()
