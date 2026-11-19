#!/usr/bin/env python3
"""Private, disposable fixture; launched by with-wallets.py. Never use production data."""
import json, os, pathlib, socket, subprocess, sys, time, urllib.request, urllib.error, signal
root = pathlib.Path(os.environ['MININGCORE_WALLET_LAB_ROOT']).resolve()
run=pathlib.Path(os.environ['MININGCORE_WALLET_RUN']);run.mkdir(mode=0o700)
processes = []
def binary(package, name):
    return str(next((root/package).rglob(name)))
def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0)); return sock.getsockname()[1]
def start(name, args):
    log = (run/(name+'.log')).open('w')
    process = subprocess.Popen(args, cwd=run, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.PIPE, text=True)
    processes.append(process)
    return process
def request(p, method, params=None, path='/json_rpc'):
    data = {'jsonrpc':'2.0','id':'lab','method':method}
    if params is not None: data['params'] = params
    payload = json.dumps(data if path == '/json_rpc' else params or {}).encode()
    req = urllib.request.Request(f'http://127.0.0.1:{p}{path}',payload,{'Content-Type':'application/json'})
    with urllib.request.urlopen(req,timeout=30) as response: return json.load(response)
def wait(call, seconds=30):
    end = time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            value=call()
            if value: return value
        except (OSError, ValueError): pass
        time.sleep(.25)
    raise RuntimeError('RPC readiness timeout')
def result(p, method, params=None):
    response=request(p,method,params)
    if response.get('error'): raise RuntimeError(f'{method}: {response["error"]}')
    return response.get('result')
manifest={}
try:
    dp,wp=port(),port(); data=run/'monero';data.mkdir()
    start('monerod',[binary('monero-0.18.5.4','monerod'),'--regtest','--offline','--fixed-difficulty','1','--data-dir',str(data),'--rpc-bind-port',str(dp),'--no-igd','--non-interactive','--no-zmq','--max-concurrency','2'])
    wait(lambda: result(dp,'get_info'))
    start('monero-wallet',[binary('monero-0.18.5.4','monero-wallet-rpc'),'--wallet-dir',str(data),'--disable-rpc-login','--allow-mismatched-daemon-version','--daemon-address',f'http://127.0.0.1:{dp}','--rpc-bind-port',str(wp),'--log-file',str(data/'wallet.log'),'--shared-ringdb-dir',str(data/'ringdb')])
    wait(lambda: request(wp,'get_version'))
    result(wp,'create_wallet',{'filename':'recipient','password':'lab-only','language':'English'})
    recipient=result(wp,'get_address')['address'];result(wp,'close_wallet')
    result(wp,'create_wallet',{'filename':'pool','password':'lab-only','language':'English'})
    address=result(wp,'get_address')['address']
    result(dp,'generateblocks',{'wallet_address':address,'amount_of_blocks':160})
    result(wp,'refresh')
    manifest['monero']={'daemonPort':dp,'walletPort':wp,'address':address,'recipient':recipient,'balance':result(wp,'get_balance')}
    print('Monero funded',flush=True)

    dp,wp=port(),port();data=run/'conceal';data.mkdir()
    daemonArgs=[binary('conceal-6.7.6','conceal-daemon'),'--testnet','--data-dir',str(data),'--rpc-bind-ip','127.0.0.1','--rpc-bind-port',str(dp),'--p2p-bind-ip','127.0.0.1','--p2p-bind-port',str(port()),'--add-exclusive-node',f'127.0.0.1:{port()}']
    daemon=start('conceal-daemon',daemonArgs)
    wait(lambda: request(dp,'',path='/getinfo'))
    args=[binary('conceal-6.7.6','conceal-walletd'),'--testnet','--container-file',str(data/'wallet'),'--container-password','lab-only','--data-dir',str(data)]
    completed=subprocess.run(args+['--generate-container'],cwd=run,stdout=(run/'conceal-generate.log').open('w'),stderr=subprocess.STDOUT,timeout=30)
    if completed.returncode: raise RuntimeError('Conceal wallet generation failed')
    start('conceal-wallet',args+['--bind-address','127.0.0.1','--bind-port',str(wp),'--daemon-address','127.0.0.1','--daemon-port',str(dp)])
    wait(lambda: request(wp,'getAddresses'))
    address=result(wp,'getAddresses')['addresses'][0]
    recipient=result(wp,'createAddress')['address']
    # Ordinary consensus validation, with private-chain timestamps spaced at the
    # target interval. Difficulty stays one; no wallet anonymity policy is relaxed.
    def varint(value):
        encoded=bytearray()
        while value>=128:encoded.append((value&127)|128);value>>=7
        encoded.append(value);return encoded
    base=int(time.time())-86400
    for height in range(1,41):
        template=result(dp,'getblocktemplate',{'wallet_address':address,'reserve_size':0})
        assert template['difficulty']==1, 'Unexpected private-chain difficulty'
        blob=bytearray.fromhex(template['blocktemplate_blob'])
        offset=0
        for _ in range(2):
            while blob[offset]&128:offset+=1
            offset+=1
        end=offset
        while blob[end]&128:end+=1
        blob[offset:end+1]=varint(base+height*120)
        result(dp,'submitblock',[blob.hex()])
    wait(lambda: result(wp,'getBalance')['availableBalance'] >= 12000010000000, seconds=120)
    manifest['conceal']={'daemonPort':dp,'walletPort':wp,'address':address,'recipient':recipient}
    print('Conceal funded with mature decoy outputs',flush=True)

    (run/'manifest.json').write_text(json.dumps(manifest,indent=2));os.chmod(run/'manifest.json',0o600)
    print('MANIFEST',run/'manifest.json',flush=True)
    end=time.monotonic()+14400
    while time.monotonic()<end and not (run/'STOP').exists(): time.sleep(1)
finally:
    for p in reversed(processes):
        if p.poll() is None: p.terminate()
    for p in processes:
        try:p.wait(timeout=15)
        except subprocess.TimeoutExpired:p.kill();p.wait()
