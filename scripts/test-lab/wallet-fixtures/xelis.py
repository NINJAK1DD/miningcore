#!/usr/bin/env python3
"""Private, disposable fixture; launched by with-wallets.py. Never use production data."""
import json, os, pathlib, socket, subprocess, time, urllib.request, urllib.error, signal, base64, sys
root=pathlib.Path(os.environ['MININGCORE_WALLET_LAB_ROOT']).resolve()
run=pathlib.Path(os.environ['MININGCORE_WALLET_RUN']);run.mkdir(mode=0o700)
processes=[]
def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
def start(name,args):
    p=subprocess.Popen(args,cwd=run,stdout=(run/(name+'.log')).open('w'),stderr=subprocess.STDOUT,stdin=subprocess.PIPE,text=True);processes.append(p);return p
def rpc(p,method,params=None,auth=None,path='/json_rpc'):
    req=urllib.request.Request(f'http://127.0.0.1:{p}{path}',json.dumps({'jsonrpc':'2.0','id':'lab','method':method,'params':params}).encode(),{'Content-Type':'application/json'})
    req.add_header('Authorization','Basic '+base64.b64encode((auth or 'lab:Lab-only-291').encode()).decode())
    try:
        with urllib.request.urlopen(req,timeout=30) as response:r=json.load(response)
    except urllib.error.HTTPError as error:
        r=json.load(error)
    if r.get('error'):raise RuntimeError(f'{method}: {r["error"]}')
    return r.get('result')
def wait(call,seconds=90):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            v=call()
            if v is not None:return v
        except (OSError,ValueError,RuntimeError):pass
        time.sleep(.25)
    raise RuntimeError('RPC timeout')
manifest={}
try:
    dp,wp=port(),port();data=run/'xelis';data.mkdir()
    # The pinned upstream release already installed by the documented lab.
    bins=root/'xelis-1.25.0/x86_64-unknown-linux-gnu'
    print(subprocess.check_output([str(bins/'xelis_daemon'),'--version'],text=True).strip(),flush=True)
    start('xelis-daemon',[str(bins/'xelis_daemon'),'--network','devnet','--skip-pow-verification','--p2p-bind-address',f'127.0.0.1:{port()}','--exclusive-nodes',f'127.0.0.1:{port()}','--rpc-bind-address',f'127.0.0.1:{dp}','--dir-path',str(data)+'/', '--disable-interactive-mode','--disable-file-logging','--disable-log-color'])
    wait(lambda:rpc(dp,'get_info'))
    start('xelis-wallet',[str(bins/'xelis_wallet'),'--network','devnet','--wallet-path',str(data/'pool'),'--password','Lab-only-291','--daemon-address',f'http://127.0.0.1:{dp}','--rpc-bind-address',f'127.0.0.1:{wp}','--rpc-username','lab','--rpc-password','Lab-only-291','--disable-interactive-mode','--disable-file-logging','--disable-log-color','--precomputed-tables-l1','20','--precomputed-tables-path',str(data)+'/'])
    address=wait(lambda:rpc(wp,'get_address',{}),seconds=600)
    print('Xelis wallet ready',flush=True)
    for i in range(40):
        template=rpc(dp,'get_block_template',{'address':address})
        rpc(dp,'submit_block',{'block_template':template['template']})
    manifest['xelis']={'daemonPort':dp,'walletPort':wp,'address':address,'recipient':'xet:06t6uhz9gxk08fpm64ufhmdy2krw9rzzwvenw0ukctwz8l9yvafsqdltctp','user':'lab','password':'Lab-only-291'}
    print('Xelis funded',flush=True)
    (run/'manifest.json').write_text(json.dumps(manifest,indent=2));os.chmod(run/'manifest.json',0o600)
    end=time.monotonic()+14400
    while time.monotonic()<end and not (run/'STOP').exists():time.sleep(1)

finally:
    for p in reversed(processes):
        if p.poll() is None:p.terminate()
    for p in processes:
        try:p.wait(timeout=15)
        except subprocess.TimeoutExpired:p.kill();p.wait()
