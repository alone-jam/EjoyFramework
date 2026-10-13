#!/usr/bin/env python3
"""Three real child processes and real UDP, with deterministic loopback-only impairment relays."""
import argparse,heapq,ipaddress,json,os,selectors,socket,subprocess,time
from pathlib import Path
parser=argparse.ArgumentParser();parser.add_argument('--dotnet');parser.add_argument('--dll');parser.add_argument('--player');parser.add_argument('--output',required=True,type=Path);parser.add_argument('--impaired',action='store_true');parser.add_argument('--authority',action='store_true');parser.add_argument('--tick-rate',type=int,default=60);parser.add_argument('--rounds',type=int,default=1,choices=[1,2]);parser.add_argument('--prediction-faults',action='store_true');parser.add_argument('--drop-disconnects',action='store_true');parser.add_argument('--authority-blackout',action='store_true');parser.add_argument('--timeout',type=float);parser.add_argument('--first-client-role',type=int,choices=[1,2]);parser.add_argument('--player-extra-args',nargs=argparse.REMAINDER,default=[]);a=parser.parse_args();a.output.mkdir(parents=True,exist_ok=True)
sel=selectors.DefaultSelector();pending=[];serial=0;stats={'received':0,'dropped':0,'delayed':0,'duplicated':0,'bindings':[]};children=[];logs=[];relays=[];first_client_ready=False
def launch(name,*args):
 log=(a.output/(name+'.log')).open('w');logs.append(log)
 command=[a.dotnet,a.dll,*map(str,args)]
 if a.player:
  server=args[0].endswith('server');command=[a.player,'-batchmode','-nographics','--fps-mode','server' if server else 'client','--fps-port',str(args[1]),'--fps-report',str(args[2]),'--fps-role','0' if server else str(args[3]),'--fps-tick-rate',str(a.tick_rate),'--fps-rounds',str(a.rounds),'--fps-prediction-faults',str(a.prediction_faults).lower(),'-logFile',str(a.output/(name+'-unity.log'))]
 if a.player:command.extend(a.player_extra_args)
 p=subprocess.Popen(command,stdout=log,stderr=subprocess.STDOUT);children.append(p);return p
def udp():
 s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.bind(('127.0.0.1',0));s.setblocking(False);stats['bindings'].append(s.getsockname());return s
def socket_rows():
 rows={}
 for protocol in ['udp','tcp']:
  for line in Path('/proc/net/'+protocol).read_text().splitlines()[1:]:
   fields=line.split()
   if protocol=='tcp' and fields[3]!='0A':continue
   iphex,porthex=fields[1].split(':');port=int(porthex,16)
   if not port:continue
   address=socket.inet_ntop(socket.AF_INET,int(iphex,16).to_bytes(4,'little'))
   rows[(protocol,fields[9])]={'protocol':protocol,'address':address,'port':port}
 return rows
baseline=set(socket_rows());last_audit=0
stats['socketAuditScope']='New namespace IPv4 UDP bindings/TCP listeners relative to pre-launch baseline; per-PID fd inspection is not permitted.'
def audit_sockets():
 global last_audit
 if time.monotonic()-last_audit<.1:return
 last_audit=time.monotonic()
 for key,record in socket_rows().items():
  if key in baseline:continue
  if record not in stats.setdefault('newSocketObservations',[]):stats['newSocketObservations'].append(record)
  if not ipaddress.ip_address(record['address']).is_loopback:raise RuntimeError('New non-loopback binding observed: '+str(record))
try:
 report=a.output/'server.json';server=launch('server','authority-server' if a.authority else 'server',0,report,*([a.tick_rate] if a.authority else []));deadline=time.monotonic()+10
 while True:
  if server.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Server did not become ready')
  try:
   port=json.loads(Path(str(report)+'.ready').read_text())['port'];break
  except (FileNotFoundError,json.JSONDecodeError):pass
  audit_sockets();time.sleep(.02)
 for role in [1,2]:
  down,up=udp(),udp();relay={'role':role,'down':down,'up':up,'client':None,'server':('127.0.0.1',port),'count':0,'handshakes':set(),'dataStarted':None};relays.append(relay)
  sel.register(down,selectors.EVENT_READ,(relay,False));sel.register(up,selectors.EVENT_READ,(relay,True))
  launch('client'+str(role),'authority-client' if a.authority else 'client',down.getsockname()[1],a.output/('client'+str(role)+'.json'),role,*([a.tick_rate] if a.authority else []))
 deadline=time.monotonic()+(a.timeout if a.timeout is not None else (55 if a.rounds==2 else 30))
 while any(p.poll() is None for p in children) and time.monotonic()<deadline:
  now=time.monotonic()
  audit_sockets()
  while pending and pending[0][0]<=now:
   _,_,sock,dest,data=heapq.heappop(pending);sock.sendto(data,dest)
  for key,_ in sel.select(.002):
   relay,reverse=key.data
   try:data,source=key.fileobj.recvfrom(65536)
   except BlockingIOError:continue
   if reverse:
    if source!=relay['server'] or relay['client'] is None:continue
    target,dest=relay['down'],relay['client']
   else:
    if relay['client'] is None or (a.rounds==2 and len(data)>=40 and data[5]==1 and ipaddress.ip_address(source[0]).is_loopback):relay['client']=source
    if source!=relay['client']:continue
    target,dest=relay['up'],relay['server']
   stats['received']+=1;relay['count']+=1;n=relay['count'];kind=data[5] if len(data)>=40 else 0
   if a.first_client_role and not first_client_ready:
    if relay['role']==a.first_client_role and reverse and kind==4:first_client_ready=True
    elif relay['role']!=a.first_client_role:
     stats['startupGateDrops']=stats.get('startupGateDrops',0)+1;continue
   if a.drop_disconnects and kind==8:
    stats['dropped']+=1;stats['disconnectDrops']=stats.get('disconnectDrops',0)+1;continue
   if kind==5 and relay['dataStarted'] is None:relay['dataStarted']=time.monotonic()
   age=0 if relay['dataStarted'] is None else time.monotonic()-relay['dataStarted']
   if a.authority_blackout and kind==5 and reverse and 2.5<=age<6.5:
    stats['dropped']+=1;stats['authorityBlackoutDrops']=stats.get('authorityBlackoutDrops',0)+1;continue
   if a.prediction_faults and kind==5 and not reverse and 1.5<=age<2.4:
    stats['dropped']+=1;stats['inputBlackoutDrops']=stats.get('inputBlackoutDrops',0)+1;continue
   handshake=(reverse,kind)
   if a.impaired and (n%7==0 or (kind in (1,2) and handshake not in relay['handshakes'])):
    relay['handshakes'].add(handshake);stats['dropped']+=1;continue
   delay=(.012+(n%5)*.009) if a.impaired else 0
   if a.prediction_faults and kind==5 and reverse and 2.6<=age<4.4:
    delay+=1.7;stats['historyDelayPackets']=stats.get('historyDelayPackets',0)+1
   serial+=1;heapq.heappush(pending,(time.monotonic()+delay,serial,target,dest,data));stats['delayed']+=int(delay>0)
   if a.impaired and kind==5 and n%13==0:
    serial+=1;heapq.heappush(pending,(time.monotonic()+delay+.006,serial,target,dest,data));stats['duplicated']+=1
 for p in children:
  if p.poll() is None:p.kill()
  p.wait()
 reports={name:json.loads((a.output/(name+'.json')).read_text()) for name in ['server','client1','client2'] if (a.output/(name+'.json')).exists()}
 passed=len(reports)==3 and all(v['passed'] for v in reports.values()) and len({v['pid'] for v in reports.values()})==3 and all(p.returncode==0 for p in children)
 stats.update(passed=passed,impaired=a.impaired,firstClientRole=a.first_client_role,reports=reports,exitCodes=[p.returncode for p in children])
 (a.output/'result.json').write_text(json.dumps(stats,indent=2)+'\n');print(json.dumps(stats));raise SystemExit(0 if passed else 1)
finally:
 for p in children:
  if p.poll() is None:p.kill();p.wait()
 for relay in relays:relay['down'].close();relay['up'].close()
 for log in logs:log.close()
 sel.close()
