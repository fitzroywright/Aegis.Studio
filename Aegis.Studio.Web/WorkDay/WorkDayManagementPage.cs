namespace Aegis.Studio.Web.WorkDay;

public static class WorkDayManagementPage
{
    public static string Render(bool locationTrackingEnabled) => $"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Studio WorkDay Management</title>
<style>
body{margin:0;background:#071522;color:#eef6ff;font:15px system-ui,Segoe UI,sans-serif}
header{padding:18px 22px;border-bottom:1px solid #28597b}main{padding:20px;display:grid;gap:18px}
.panel{background:#0d2030;border:1px solid #28597b;border-radius:14px;padding:16px}
table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:9px;border-bottom:1px solid #17394f}
button{background:#102c40;color:white;border:1px solid #50c8ff;border-radius:7px;padding:8px 11px;cursor:pointer}
.good{color:#20d789}.warn{color:#ffad31}.muted{color:#9fb7c8}
#map{min-height:220px;display:grid;place-items:center;background:#081824;border-radius:10px;padding:12px}
a{color:#50c8ff}
</style>
</head>
<body>
<header><h1>Aegis Studio WorkDay</h1><div class="muted">Authorized work-session and field-presence evidence</div></header>
<main>
<section class="panel"><h2>Fleet</h2><div id="fleet">Loading...</div></section>
<section class="panel"><h2>Recent Work Sessions</h2><div id="sessions">Loading...</div></section>
<section class="panel"><h2>Selected Timeline</h2><div id="timeline" class="muted">Select a work session.</div><div id="map">Precise location appears only in this authorized view.</div></section>
</main>
<script>
const locationTrackingEnabled={{(locationTrackingEnabled ? "true" : "false")}};
function esc(v){return String(v==null?'':v).replace(/[&<>"']/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];});}
async function getJson(u){var r=await fetch(u,{credentials:'same-origin'});if(!r.ok)throw new Error('HTTP '+r.status);return r.json();}
async function load(){
 try{
  var f=await getJson('/api/workday/fleet');
  document.getElementById('fleet').innerHTML='Clients <b>'+f.clients+'</b> · Recently online <b>'+f.recentlyOnline+'</b> · Offline/stale <b>' + f.offlineOrStale + '</b><br>Pending sync '+f.pendingSync+' · Pending uploads '+f.pendingUploads+' · Failed uploads '+f.failedUploads;
 }catch(err){document.getElementById('fleet').textContent='Fleet view unavailable: '+err.message;}
 try{
  var rows=await getJson('/api/workday/management/sessions?count=100');
  var html='<table><thead><tr><th>User</th><th>Started</th><th>Ended</th><th>Source</th><th></th></tr></thead><tbody>';
  rows.forEach(function(x){
    html+='<tr><td>'+esc(x.userName)+'</td><td>'+new Date(x.startedAtUtc).toLocaleString()+'</td><td>'+(x.endedAtUtc?new Date(x.endedAtUtc).toLocaleString():'ACTIVE')+'</td><td>'+esc(x.source)+'</td><td><button class="view" data-id="'+x.id+'" data-user="'+encodeURIComponent(x.userName)+'">View</button> <button class="correct" data-id="'+x.id+'">Correct</button></td></tr>';
  });
  html+='</tbody></table>';
  document.getElementById('sessions').innerHTML=html;
  document.querySelectorAll('#sessions button.view').forEach(function(b){b.onclick=function(){show(b.dataset.id,b.dataset.user);};});
  document.querySelectorAll('#sessions button.correct').forEach(function(b){b.onclick=function(){correctSession(b.dataset.id);};});
 }catch(err){document.getElementById('sessions').textContent='Sessions unavailable: '+err.message;}
}
async function correctSession(id){
 var start=prompt('Corrected sign-in time (ISO 8601), or leave blank to keep current:','');
 if(start===null)return;
 var end=prompt('Corrected sign-out time (ISO 8601), or leave blank to keep current:','');
 if(end===null)return;
 var reason=prompt('Correction reason (required):','');
 if(!reason||!reason.trim())return;
 var payload={correctedStartedAtUtc:start.trim()?new Date(start).toISOString():null,correctedEndedAtUtc:end.trim()?new Date(end).toISOString():null,reason:reason.trim()};
 try{
  var r=await fetch('/api/workday/management/sessions/'+id+'/corrections',{method:'POST',headers:{'Content-Type':'application/json'},credentials:'same-origin',body:JSON.stringify(payload)});
  if(!r.ok)throw new Error('HTTP '+r.status);
  await load();
 }catch(err){alert('Correction was not applied: '+err.message);}
}
async function show(id,user){
 try{
  var t=await getJson('/api/workday/work-sessions/'+id+'/timeline?userName='+user);
  var html='<b>'+esc(t.userName)+'</b><br>';
  if(t.locationEvidence.length){
    t.locationEvidence.forEach(function(x){html+=new Date(x.observedAtUtc).toLocaleString()+' · accuracy '+(x.accuracyMetres==null?'unknown':x.accuracyMetres)+'m · '+esc(x.source||'Device')+'<br>';});
  }else html+='No location evidence recorded.';
  document.getElementById('timeline').innerHTML=html;
  if(!locationTrackingEnabled){
    document.getElementById('map').textContent='GPS tracking is disabled by Studio policy.';
  }else if(t.locationEvidence.length){
    var pts=t.locationEvidence;
    var minLat=Math.min.apply(null,pts.map(x=>x.latitude)),maxLat=Math.max.apply(null,pts.map(x=>x.latitude));
    var minLon=Math.min.apply(null,pts.map(x=>x.longitude)),maxLon=Math.max.apply(null,pts.map(x=>x.longitude));
    var w=640,h=260,pad=24,latSpan=Math.max(maxLat-minLat,0.00001),lonSpan=Math.max(maxLon-minLon,0.00001);
    var xy=pts.map(x=>({
      x:pad+((x.longitude-minLon)/lonSpan)*(w-pad*2),
      y:h-pad-((x.latitude-minLat)/latSpan)*(h-pad*2),
      value:x
    }));
    var poly=xy.map(p=>p.x.toFixed(1)+','+p.y.toFixed(1)).join(' ');
    var dots=xy.map((p,i)=>'<circle cx="'+p.x.toFixed(1)+'" cy="'+p.y.toFixed(1)+'" r="5"><title>'+esc(new Date(p.value.observedAtUtc).toLocaleString())+'</title></circle>').join('');
    var links=pts.map((p,i)=>'<a target="_blank" rel="noopener" href="https://www.openstreetmap.org/?mlat='+p.latitude+'&mlon='+p.longitude+'#map=17/'+p.latitude+'/'+p.longitude+'">Observation '+(i+1)+'</a>').join(' · ');
    document.getElementById('map').innerHTML='<div style="width:100%"><b>Field presence trail</b><br><span class="muted">'+pts.length+' observation(s), chronological order. Device evidence only; not proof of employee conduct.</span><svg viewBox="0 0 '+w+' '+h+'" style="width:100%;max-height:300px;margin-top:10px;background:#06111b;border:1px solid #214b67;border-radius:8px"><polyline points="'+poly+'" fill="none" stroke="#50c8ff" stroke-width="3"/>'+dots+'</svg><div style="margin-top:8px">'+links+'</div></div>';
  }else document.getElementById('map').textContent='No location evidence available.';
 }catch(err){document.getElementById('timeline').textContent='Timeline unavailable: '+err.message;}
}
load();
</script></body></html>
""";
}
