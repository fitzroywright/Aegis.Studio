# Runs the real application against a disposable test database; build Debug first.
import os,subprocess,time,urllib.request,urllib.error,json,http.cookiejar
from pathlib import Path
import shutil
root=str(Path(__file__).resolve().parents[1]/'Aegis.Studio.Web')
dotnet=os.environ.get('DOTNET_EXE') or shutil.which('dotnet')
if not dotnet:raise RuntimeError('Set DOTNET_EXE to the .NET SDK executable')
postgres=os.environ['STUDIO_SMOKE_POSTGRES']
env=os.environ.copy();env.update({'ASPNETCORE_ENVIRONMENT':'Development','ASPNETCORE_URLS':'http://127.0.0.1:45100','ConnectionStrings__Studio':postgres,'Storage__Local__RootPath':'/tmp/studio-smoke-storage','Aegis__Diagnostics__StorePath':'/tmp/studio-smoke-diag.db','Aegis__Registration__IdentityFile':'/tmp/studio-smoke-registration.json','Aegis__Configuration__Url':'','Aegis__Operations__Url':'','Aegis__Diagnostics__Url':''})
log=open('/tmp/studio-app-smoke.log','w');p=subprocess.Popen([dotnet,root+'/bin/Debug/net10.0/Aegis.Studio.Web.dll'],cwd=root,env=env,stdout=log,stderr=log)
cookies=http.cookiejar.CookieJar();client=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookies))
def call(path,body=None,method=None,token=None):
 headers={'Content-Type':'application/json'}
 if token:headers['X-Studio-Pos-Launch']=token
 req=urllib.request.Request('http://127.0.0.1:45100'+path,data=json.dumps(body).encode() if body is not None else None,method=method,headers=headers)
 try:
  with client.open(req,timeout=15) as r:
   data=r.read();return r.status,json.loads(data) if data and 'json' in r.headers.get('Content-Type','') else data.decode()
 except urllib.error.HTTPError as e:return e.code,e.read().decode()
try:
 for i in range(100):
  if p.poll() is not None:raise RuntimeError('Application startup exited')
  try:
   status,data=call('/api/auth/options');break
  except urllib.error.URLError:time.sleep(.3)
 else:raise RuntimeError('Startup timeout')
 assert status==200,(status,data)
 time.sleep(3)
 for attempt in range(3):
  status,data=call('/api/auth/login',{'userName':'studio.admin','password':'test'})
  if status==200:break
  time.sleep(.5)
 assert status==200,(status,data)
 print('Login and application migration/startup succeeded')
 for path in ['/api/admin/commercial/templates/Job','/api/admin/commercial/templates/WorkOrder','/api/production/work-orders','/api/pos/catalog']:
  status,data=call(path);assert status==200,(path,status,data);print(path,status)
 status,data=call('/api/frontdesk/pos/launch',{'action':'CloseRegister'});assert status==200,(status,data);token=data['token'];print('Front Desk launch issued',status)
 assert call('/api/frontdesk/pos/launch/'+token)[0]==200
 status,data=call('/api/pos/register/open',{'terminalId':'StudioWeb','openingCash':0});assert status==403,(status,data);print('Financial write without launch rejected',status)
 status,data=call('/api/frontdesk/pos/launch',{'action':'OpenPos'});assert status==400,(status,data);print('Generic POS launch rejected',status)
 def ok(path,body,token=None):
  status,data=call(path,body,token=token);assert status in (200,201,204),(path,status,data);return data
 workspace=ok('/api/admin/workspaces/',{'name':'Acceptance Studio','emailAddress':None,'routeKey':'acceptance','isEnabled':True,'sortOrder':1})
 template=ok('/api/admin/commercial/templates/WorkOrder',{'definition':{'name':'Acceptance frame','description':'Print frame and inspect','steps':[{'key':'print','title':'Print frame','activityType':'Print','area':'Printing'},{'key':'qc','title':'Inspect frame','activityType':'QC','area':'Quality Control','dependsOn':['print'],'qualityControl':True}]}})
 product=ok('/api/admin/pos/catalog/',{'code':'ACCEPT-FRAME','name':'Acceptance frame','category':'Frames','unitPrice':100,'estimatedCost':30,'requiresProduction':True,'workOrderTemplateId':template['id']})
 jobtemplate=ok('/api/admin/commercial/templates/Job',{'definition':{'name':'Acceptance printing','description':'In house','steps':[{'key':'review','title':'Review order'}],'products':[{'catalogItemId':product['id'],'quantity':3,'specifications':'8x10 wood'}]}})
 booking=ok('/api/frontdesk/booking-requests',{'workspaceId':workspace['id'],'clientName':'Acceptance Customer','clientEmail':'acceptance@example.test','title':'Three frames','eventDate':'2026-10-11','requiredDeposit':0,'currency':'JMD','notes':None,'jobTemplateId':jobtemplate['id']})
 job=booking['jobId'];assert job
 launch=ok('/api/frontdesk/pos/launch',{'action':'NewSale','jobId':job});token=launch['token']
 import uuid
 request={'lines':[{'catalogItemId':product['id'],'code':product['code'],'description':product['name'],'quantity':3,'unitPrice':100,'specifications':'8x10 wood'}],'clientRequestId':str(uuid.uuid4())}
 sale=ok('/api/jobs/'+job+'/pos/sales',request,token);assert ok('/api/jobs/'+job+'/pos/sales',request,token)['id']==sale['id']
 complete={'tenders':[{'kind':'Cash','amount':300,'reference':'Acceptance'}],'terminalId':'StudioWeb'}
 ok('/api/pos/sales/'+sale['id']+'/complete',complete,token);ok('/api/pos/sales/'+sale['id']+'/complete',complete,token)
 status,orders=call('/api/production/work-orders?jobId='+job);assert status==200 and len(orders)==1,(status,orders)
 assert orders[0]['quantity']==3 and len(orders[0]['tasks'])==2,orders
 print('HTTP catalog -> Work Order Template -> Job Template -> Front Desk job -> contextual sale -> completion replay -> one work order passed')
 status,assets=call('/api/jobs/'+job+'/commercial/approved-sources');assert status==200 and assets==[],(status,assets)
 assert call('/API/pos/register/open',{'terminalId':'StudioWeb','openingCash':0})[0]==403
 print('HTTP smoke passed')
finally:p.terminate();p.wait(timeout=15);log.close()
