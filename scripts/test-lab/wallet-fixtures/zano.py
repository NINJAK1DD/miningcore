#!/usr/bin/env python3
"""Private, disposable fixture; launched by with-wallets.py. Never use production data."""
import json,os,pathlib,socket,subprocess,time,urllib.request
root=pathlib.Path(os.environ['MININGCORE_WALLET_LAB_ROOT']).resolve()
run=pathlib.Path(os.environ['MININGCORE_WALLET_RUN']);run.mkdir(mode=0o700)
def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
dp,wp=port(),port();processes=[]
bins=root/'zano-testnet-source/build-testnet-gcc12/src'
def start(name,args):
    p=subprocess.Popen(args,cwd=run,stdout=(run/(name+'.log')).open('w'),stderr=subprocess.STDOUT,stdin=subprocess.DEVNULL);processes.append(p);return p
def rpc(p,method,params=None):
    data={'jsonrpc':'2.0','id':'lab','method':method}
    if params is not None:data['params']=params
    req=urllib.request.Request(f'http://127.0.0.1:{p}/json_rpc',json.dumps(data).encode(),{'Content-Type':'application/json'})
    with urllib.request.urlopen(req,timeout=30) as r:response=json.load(r)
    if response.get('error'):raise RuntimeError(response['error'])
    return response.get('result')
def wait(call):
    end=time.monotonic()+60
    while time.monotonic()<end:
        try:return call()
        except (OSError,RuntimeError):time.sleep(.25)
    raise RuntimeError('readiness timeout')
def varint(n):
    b=bytearray()
    while n>=128:b.append((n&127)|128);n>>=7
    b.append(n);return b
try:
    data=run/'chain';data.mkdir()
    start('daemon',[str(bins/'zanod'),'--data-dir',str(data),'--log-dir',str(data),'--offline-mode','--no-console','--no-predownload','--disable-upnp','--disable-ntp','--rpc-ignore-offline','--rpc-bind-ip','127.0.0.1','--rpc-bind-port',str(dp),'--p2p-bind-ip','127.0.0.1','--p2p-bind-port',str(port())])
    wait(lambda:rpc(dp,'getinfo',{}))
    common=[str(bins/'simplewallet'),'--password','Lab-only-strong-password-291','--daemon-address',f'http://127.0.0.1:{dp}','--log-file',str(run/'wallet-internal.log'),'--no-white-list','--disable-tor-relay']
    subprocess.run(common+['--generate-new-wallet',str(run/'recipient.wallet'),'address'],stdout=(run/'recipient-create.log').open('w'),stderr=subprocess.STDOUT,timeout=30)
    assert (run/'recipient.wallet').exists()
    recipient_port=port()
    recipient_process=start('recipient',common+['--wallet-file',str(run/'recipient.wallet'),'--rpc-bind-ip','127.0.0.1','--rpc-bind-port',str(recipient_port),'--unsecure-no-auth'])
    recipient=wait(lambda:rpc(recipient_port,'getaddress'))['address']
    recipient_process.terminate();recipient_process.wait(timeout=20)
    subprocess.run(common+['--generate-new-wallet',str(run/'pool.wallet'),'address'],stdout=(run/'wallet-create.log').open('w'),stderr=subprocess.STDOUT,timeout=30)
    assert (run/'pool.wallet').exists()
    start('wallet',common+['--wallet-file',str(run/'pool.wallet'),'--rpc-bind-ip','127.0.0.1','--rpc-bind-port',str(wp),'--unsecure-no-auth'])
    address=wait(lambda:rpc(wp,'getaddress'))['address']
    base=int(time.time())-86400
    for height in range(1,31):
        template=rpc(dp,'getblocktemplate',{'wallet_address':address,'extra_text':'issue291-lab','pos_block':False})
        blob=bytearray.fromhex(template['blocktemplate_blob'])
        assert template['height']==height
        offset=41
        while blob[offset]&128:offset+=1
        offset+=1;end=offset
        while blob[end]&128:end+=1
        blob[offset:end+1]=varint(base+height*120)
        for nonce in range(1000):
            blob[1:9]=nonce.to_bytes(8,'little')
            try:response=rpc(dp,'submitblock',[blob.hex()])
            except RuntimeError:continue
            if response.get('status')=='OK':break
        else:raise RuntimeError('Could not mine private block')
        print('Zano height',height,'difficulty',template['difficulty'],flush=True)
    assert recipient!=address
    manifest={'zano':{'daemonPort':dp,'walletPort':wp,'address':address,'recipient':recipient}}
    (run/'manifest.json').write_text(json.dumps(manifest,indent=2));os.chmod(run/'manifest.json',0o600)
    print('MANIFEST',run/'manifest.json',flush=True)
    end=time.monotonic()+14400
    while time.monotonic()<end and not (run/'STOP').exists():time.sleep(1)
finally:
    for p in reversed(processes):
        if p.poll() is None:p.terminate()
    for p in processes:
        try:p.wait(timeout=15)
        except subprocess.TimeoutExpired:p.kill();p.wait()
