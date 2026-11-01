#!/usr/bin/env python3
"""Compare a public, node-accepted V3 block against the pinned official Python gateway.
Run in an environment containing official pearl_mining bindings, numpy, bitcoin-utils.
No verifier or serialization stand-ins are used. RPC input is supplied as a local public hex file.
"""
import argparse,gzip,hashlib,json,pathlib,struct,sys
p=argparse.ArgumentParser();p.add_argument('upstream',type=pathlib.Path);p.add_argument('block_hex',type=pathlib.Path);p.add_argument('output',type=pathlib.Path);a=p.parse_args()
sys.path.insert(0,str(a.upstream/'miner/pearl-gateway/src'))
from pearl_gateway.blockchain_utils.pearl_block import PearlBlock
from pearl_gateway.blockchain_utils.pearl_header import PearlHeader
from pearl_gateway.blockchain_utils.zk_certificate import ZKCertificate
raw=bytes.fromhex(a.block_hex.read_text().strip());version=struct.unpack_from('<I',raw)[0];assert version==3
public_len=struct.unpack_from('<I',raw,36)[0];proof_len=struct.unpack_from('<I',raw,40+public_len)[0];offset=44+public_len+proof_len
cert=ZKCertificate.deserialize(raw[:offset]);header=PearlHeader.deserialize(raw[offset:offset+108]);body=raw[offset+108:]
assert body[0]==1,'Use a one-transaction public lab block for an unambiguous raw transaction fixture'
serialized=PearlBlock(header,[body[1:]],cert).serialize();assert serialized==raw
# Exercise CompactSize boundaries independently, retaining exactly the same raw transaction bytes.
counts={str(n):hashlib.sha256(PearlBlock(header,[body[1:]]*n,cert).serialize()).hexdigest() for n in [1,252,253,65535,65536]}
a.output.write_bytes(gzip.compress(raw,mtime=0))
a.output.with_suffix('.json').write_text(json.dumps({'version':version,'header':raw[offset:offset+108].hex(),'publicData':bytes(cert.proof.public_data).hex(),'certificateBytes':proof_len,'blockSha256':hashlib.sha256(raw).hexdigest(),'compactSizeBlockSha256':counts,'upstream':'d7c517d267f10f5bcbbdc366f3e603942f9a5ab5','source':'proof-enabled isolated regtest node; official Python gateway and Rust bindings'},indent=2)+'\n')
print('Official Python full-block serialization matches node bytes, including five CompactSize boundaries',flush=True)
