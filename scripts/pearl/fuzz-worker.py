#!/usr/bin/env python3
"""Reproducible bounded native mutation campaign, frame sizing and cold/warm-OS-cache benchmark.
Use only the pinned worker and public SRBMiner fixtures. Does not need wallet access.
"""
import argparse,base64,gzip,hashlib,json,os,pathlib,random,struct,subprocess,time,zlib
p=argparse.ArgumentParser();p.add_argument('worker',type=pathlib.Path);p.add_argument('--output',type=pathlib.Path,required=True);p.add_argument('--cases',type=int,default=2048);p.add_argument('--benchmark',action='store_true');p.add_argument('--threads',type=int,nargs='+',choices=[1,2,4],default=[1,2,4,4]);a=p.parse_args()
worker=str(a.worker.resolve(strict=True));root=pathlib.Path(__file__).resolve().parents[2];fixtures=root/'src/Miningcore.Tests/Fixtures/Pearl'
rng=random.Random(244256);records=[];frames=[];seeds=[];environment=dict(os.environ,RAYON_NUM_THREADS='4');start=time.monotonic()
def packet(meta,proof,operation=0):return b'PRLJOB02'+bytes([operation,3,0,0])+bytes.fromhex(meta['header'])+bytes.fromhex(meta['target'])+struct.pack('<I',len(proof))+proof
for name in ['srb','share-only']:
 meta=json.loads((fixtures/(name+'-proof.json')).read_text());raw=gzip.decompress((fixtures/meta['proofFile']).read_bytes());seeds.append((meta,raw))
 for encoding,blob in [('plain',raw),('gzip',gzip.compress(raw,mtime=0)),('zlib',zlib.compress(raw))]:
  encoded=base64.b64encode(blob).decode();frame=json.dumps({'id':1,'method':'mining.submit','params':{'job_id':'0'*32,'plain_proof':encoded}},separators=(',',':')).encode()+b'\n'
  frames.append({'fixture':name,'encoding':encoding,'plainBytes':len(raw),'encodedBytes':len(encoded),'jsonFrameBytes':len(frame),'sha256':hashlib.sha256(raw).hexdigest()})
status={'accepted':0,'rejected':0}
for i in range(a.cases):
 meta,seed=seeds[i%len(seeds)];raw=bytearray(seed);kind=i%5
 if kind==0:raw=raw[:rng.randrange(len(raw))]
 elif kind==1:
  offset=rng.choice([0,8,16,24,32,40]);raw[offset:offset+8]=rng.choice([0,1,2**63,2**64-1]).to_bytes(8,'little')
 elif kind==2:
  for _ in range(rng.randrange(1,9)):
   offset=rng.randrange(len(raw));raw[offset]^=1<<rng.randrange(8)
 elif kind==3:raw.extend(rng.randbytes(rng.randrange(2,65)))
 else:
  offset=rng.randrange(len(raw)-8);raw[offset:offset+8]=rng.randbytes(8)
 result=subprocess.run([worker],input=packet(meta,raw),capture_output=True,timeout=5,env=environment)
 assert result.returncode in (0,1),(i,'unexpected process failure',result.returncode)
 assert not result.stderr,(i,'unexpected stderr')
 if result.returncode==1:
  assert result.stdout==b'PRLRES02\1\0\0\0',(i,'bad rejection');status['rejected']+=1
 else:
  assert len(result.stdout)==180 and result.stdout[:9]==b'PRLRES02\0',(i,'bad success');status['accepted']+=1
# Real parent-death behavior: helper waits for stdin while its disposable parent is killed.
parent=subprocess.Popen(['python3','-c','import subprocess,sys,time; p=subprocess.Popen([sys.argv[1]],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL); print(p.pid,flush=True); time.sleep(30)',worker],stdout=subprocess.PIPE,text=True)
child=int(parent.stdout.readline());time.sleep(.2);parent.kill();parent.wait()
for _ in range(100):
 path=pathlib.Path(f'/proc/{child}/stat')
 try:
  if path.read_text().split()[2]=='Z':break
 except (FileNotFoundError, ProcessLookupError):break
 time.sleep(.01)
else:raise AssertionError('orphan worker survived parent death')
report={'seed':244256,'cases':a.cases,'outcomes':status,'mutationSeconds':round(time.monotonic()-start,3),'parentDeathVerified':True,'workerSha256':hashlib.sha256(pathlib.Path(worker).read_bytes()).hexdigest(),'frames':frames,'benchmarks':[]}
if a.benchmark:
 meta,raw=seeds[0]
 for threads in a.threads:
  start=time.monotonic();timing=a.output.with_suffix('.time');env=dict(os.environ,RAYON_NUM_THREADS=str(threads))
  result=subprocess.run(['/usr/bin/time','-f','%M %U %S','-o',str(timing),worker],input=packet(meta,raw,1),capture_output=True,timeout=180,env=env)
  assert result.returncode==0,(threads,'prove failed',result.returncode)
  assert result.stdout[:10]==b'PRLRES02\0\1';length=struct.unpack_from('<I',result.stdout,176)[0];assert len(result.stdout)==180+length
  rss,user,system=timing.read_text().split();report['benchmarks'].append({'threads':threads,'wallSeconds':round(time.monotonic()-start,3),'peakRssKiB':int(rss),'userSeconds':float(user),'systemSeconds':float(system),'certificateBytes':length})
  print('benchmark',report['benchmarks'][-1],flush=True)
a.output.write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report),flush=True)
