namespace Aegis.Studio.Web.WorkDay;

public static class WorkspaceWorkDayManagementPage
{
    public static string Render(Guid workspaceId) => $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Workspace WorkDay Management</title>
<style>
body{margin:0;background:#071522;color:#eef6ff;font:15px system-ui,Segoe UI,sans-serif}
header{padding:18px 22px;border-bottom:1px solid #28597b}main{padding:20px;display:grid;gap:18px}
.panel{background:#0d2030;border:1px solid #28597b;border-radius:14px;padding:16px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:12px}
.card{background:#081824;border:1px solid #214b67;border-radius:12px;padding:14px}
.good{color:#20d789}.warn{color:#ffad31}.bad{color:#ff6b6b}.muted{color:#9fb7c8}
.bar{height:8px;background:#17394f;border-radius:999px;overflow:hidden}.bar span{display:block;height:100%;background:#50c8ff}
a{color:#50c8ff}
</style></head>
<body>
<header><h1>Workspace WorkDay</h1><div class="muted">Photographer activity, capacity and assigned job progress</div></header>
<main>
<section class="panel"><div id="summary">Loading workspace activity…</div></section>
<section class="panel"><h2>Photographers</h2><div id="people" class="grid"></div></section>
<section class="panel"><h2>Job Flow</h2><p class="muted">Live JobFlow tracking lights are published to Aegis Operations. Open Operations → Flows and filter <b>Studio Job Flow</b> for the detailed stage path.</p></section>
</main>
<script>
const workspaceId='{{workspaceId:D}}';
function e(v){return String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
function cls(state){return state==='Available'||state==='Online'?'good':state==='OverCapacity'||state==='Offline'?'bad':'warn';}
async function load(){
 const r=await fetch('/api/workday/workspaces/'+workspaceId+'/activity',{credentials:'same-origin'});
 if(!r.ok){summary.textContent='Activity unavailable: HTTP '+r.status;return;}
 const d=await r.json();
 summary.innerHTML='Photographers <b>'+d.photographers.length+'</b> · Updated '+new Date(d.generatedAtUtc).toLocaleString();
 people.innerHTML=d.photographers.map(p=>{
   const c=p.client,cap=p.capacity,a=p.assignments||[],t=p.timeSummary;
   const util=cap?Math.min(100,Math.max(0,cap.utilizationPercent)):0;
   return '<article class="card"><h3>'+e(p.displayName)+'</h3>'+
    '<div class="'+cls(c?.connectivity||'Unknown')+'">'+e(c?.connectivity||'No recent client telemetry')+'</div>'+
    '<p>Sync: '+e(c?.synchronizationState||'Unknown')+' · Pending '+e(c?.pendingOperations??0)+' · Uploads '+e(c?.pendingUploads??0)+'</p>'+
    '<p>Work session: '+(p.activeWorkSession?'ACTIVE since '+new Date(p.activeWorkSession.startedAtUtc).toLocaleTimeString():'Signed out')+'</p>'+
    (cap?'<p>Capacity: <b class="'+cls(cap.state)+'">'+e(cap.state)+'</b> · '+cap.utilizationPercent+'%</p><div class="bar"><span style="width:'+util+'%"></span></div><p class="muted">'+Math.round(cap.scheduledMinutes/60*10)/10+'h scheduled · '+Math.round(cap.remainingMinutes/60*10)/10+'h remaining</p>':'')+
    (t?'<p><b>Time (14d)</b> · Regular '+(t.regularMinutes/60).toFixed(1)+'h · Overtime '+(t.overtimeMinutes/60).toFixed(1)+'h · Weekend '+(t.weekendMinutes/60).toFixed(1)+'h</p>':'')+
    '<p><b>'+a.length+'</b> assigned job(s) in this workspace window</p>'+
    a.slice(0,6).map(x=>'<div class="muted">'+new Date(x.startsAtUtc).toLocaleString()+' · '+e(x.title)+' · '+e(x.status)+'</div>').join('')+
    '</article>';
 }).join('');
}
load();setInterval(load,30000);
</script></body></html>
""";
}
