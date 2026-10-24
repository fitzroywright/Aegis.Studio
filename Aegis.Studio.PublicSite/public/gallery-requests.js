const $=id=>document.getElementById(id);let csrf='',requestId=crypto.randomUUID();
async function api(path,options={}){
  const r=await fetch('/api/'+path,{credentials:'same-origin',...options,headers:{'Content-Type':'application/json','X-CSRF-Token':csrf,...options.headers}});
  const p=await r.json();if(r.status===401){$('account').hidden=true;$('access').hidden=false;$('identity').textContent='';$('logout').hidden=true;}if(!r.ok)throw new Error(p.message||'Request failed.');return p;
}
function notice(text){$('notice').textContent=text;}
async function refresh(){
  const c=await api('client-access/session');$('identity').textContent=c.name;$('access').hidden=true;$('account').hidden=false;$('logout').hidden=false;
  const requests=await api('client-access/requests');$('requests').replaceChildren();
  for(const r of requests.items){const li=document.createElement('li');li.textContent=r.id+' · '+r.status+(r.reason?' · '+r.reason:'');$('requests').append(li);}
  const galleries=await api('client-access/galleries');$('galleries').replaceChildren();
  for(const g of galleries.items){
    const section=document.createElement('section'),heading=document.createElement('h3');heading.textContent=g.title+' · Version '+g.version;section.append(heading);
    const grid=document.createElement('div');grid.className='client-image-grid';section.append(grid);let shown=0;
    const more=document.createElement('button');more.textContent='Show more photographs';
    function page(){
      for(const file of g.items.slice(shown,shown+50)){
        const link=document.createElement('a');link.href='/api/client-access/galleries/'+g.galleryId+'/files/'+file.versionId;link.target='_blank';link.rel='noopener';
        const image=document.createElement('img');image.src=link.href;image.alt=file.fileName;image.loading='lazy';image.decoding='async';
        link.append(image,document.createTextNode(file.fileName));grid.append(link);
      }
      shown+=50;more.hidden=shown>=g.items.length;
    }
    more.onclick=page;section.append(more);page();
    $('galleries').append(section);
  }
}
for(const name of ['login','register'])$(name).onsubmit=async e=>{e.preventDefault();try{await api('client-access/'+name,{method:'POST',body:JSON.stringify(Object.fromEntries(new FormData(e.target)))});e.target.reset();await refresh();notice('Signed in.');}catch(error){notice(error.message);}};
$('request').onsubmit=async e=>{
  e.preventDefault();const button=e.target.querySelector('button');button.disabled=true;
  try{const p=Object.fromEntries(new FormData(e.target));await api('client-access/requests',{method:'POST',body:JSON.stringify({id:requestId,...p})});notice('Request saved. Brian’s staff will review and verify it.');requestId=crypto.randomUUID();e.target.reset();await refresh();}
  catch(error){notice(error.message);}finally{button.disabled=false;}
};
$('logout').onclick=async()=>{await api('client-access/logout',{method:'POST',body:'{}'});location.reload();};
$('refresh').onclick=()=>refresh().catch(e=>notice(e.message));
(async()=>{csrf=(await api('intake-session')).csrf;try{await refresh();}catch{notice('Sign in or create a website account.');}})();
