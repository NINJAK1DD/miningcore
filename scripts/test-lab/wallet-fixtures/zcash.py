#!/usr/bin/env python3
"""Private, disposable fixture; launched by with-wallets.py. Never use production data."""
import json,os,pathlib,socket,subprocess,time,urllib.request,urllib.error,base64,threading
root=pathlib.Path(os.environ['MININGCORE_WALLET_LAB_ROOT']).resolve()
run=pathlib.Path(os.environ['MININGCORE_WALLET_RUN']);run.mkdir(mode=0o700)
with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
config=run/'zcash.conf';config.write_text('rpcuser=lab\nrpcpassword=Lab-only-291\nrpcallowip=127.0.0.1\ni-am-aware-zcashd-will-be-replaced-by-zebrad-and-zallet-in-2025=1\nnuparams=5ba81b19:1\nnuparams=76b809bb:2\nallowdeprecated=z_getbalance\n');os.chmod(config,0o600)
args=[str(root/'zcash-6.20.0/usr/bin/zcashd'),'-regtest','-listen=0','-connect=0','-dnsseed=0','-discover=0','-server=1','-datadir='+str(run),'-conf='+str(config),'-rpcbind=127.0.0.1','-rpcport='+str(port),'-walletrequirebackup=false','-allowdeprecated=getnewaddress','-allowdeprecated=z_getnewaddress','-allowdeprecated=legacy_privacy','-printtoconsole=1']
process=subprocess.Popen(args,cwd=run,stdout=(run/'daemon.log').open('w'),stderr=subprocess.STDOUT)
stop=threading.Event()
def rpc(method,params=None):
    req=urllib.request.Request(f'http://127.0.0.1:{port}/',json.dumps({'jsonrpc':'1.0','id':'lab','method':method,'params':params or []}).encode(),{'Content-Type':'application/json','Authorization':'Basic '+base64.b64encode(b'lab:Lab-only-291').decode()})
    try:
        with urllib.request.urlopen(req,timeout=90) as response:r=json.load(response)
    except urllib.error.HTTPError as error:r=json.load(error)
    if r.get('error'):raise RuntimeError(f'{method}: {r["error"]}')
    return r.get('result')
try:
    end=time.monotonic()+120
    while True:
        if process.poll() is not None:raise RuntimeError('zcashd exited; inspect daemon.log')
        try:info=rpc('getblockchaininfo');break
        except (OSError,RuntimeError):
            if time.monotonic()>end:raise
            time.sleep(.25)
    assert info['chain']=='regtest',info['chain']
    address=rpc('getnewaddress');recipient=rpc('getnewaddress');shielded=rpc('z_getnewaddress',['sapling'])
    rpc('generate',[120])
    address=rpc('listunspent')[0]['address']
    def mine():
        while not stop.wait(2):
            try:rpc('generate',[1])
            except Exception as error:print('Mining error',type(error).__name__,flush=True);return
    threading.Thread(target=mine,daemon=True).start()
    manifest={'zcash':{'daemonPort':port,'walletPort':port,'address':address,'recipient':recipient,'zAddress':shielded,'user':'lab','password':'Lab-only-291'}}
    (run/'manifest.json').write_text(json.dumps(manifest,indent=2));os.chmod(run/'manifest.json',0o600)
    print('Zcash funded; MANIFEST',run/'manifest.json',flush=True)
    end=time.monotonic()+14400
    while time.monotonic()<end and not (run/'STOP').exists():time.sleep(1)
finally:
    stop.set()
    if process.poll() is None:process.terminate()
    try:process.wait(timeout=20)
    except subprocess.TimeoutExpired:process.kill();process.wait()
