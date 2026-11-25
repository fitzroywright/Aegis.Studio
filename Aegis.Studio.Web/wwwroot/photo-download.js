'use strict';
(async()=>{
 const token=location.hash.slice(1),status=document.getElementById('status'),list=document.getElementById('files');
 history.replaceState(null,'',location.pathname);
 if(!/^[A-F0-9]{64}$/.test(token)){status.textContent='This download link is invalid.';return;}
 async function request(versionId){
  const r=await fetch('/api/photo-download',{method:'POST',credentials:'omit',cache:'no-store',redirect:'error',headers:{'Content-Type':'application/json'},body:JSON.stringify({token,versionId})});
  if(!r.ok)throw Error('This photograph or link is no longer available.');
  return r;
 }
 try{
  const items=await (await request(null)).json();
  status.textContent='Choose a photograph to download. Do not forward your private link.';
  for(const item of items){
   const li=document.createElement('li'),button=document.createElement('button');button.textContent='Download '+item.fileName;li.append(button);list.append(li);
   button.onclick=async()=>{button.disabled=true;try{const r=await request(item.versionId),url=URL.createObjectURL(await r.blob()),a=document.createElement('a');a.href=url;a.download=item.fileName;a.click();setTimeout(()=>URL.revokeObjectURL(url),60000);status.textContent='Download requested.';}catch(e){status.textContent=e.message;}finally{button.disabled=false;}};
  }
 }catch(e){status.textContent=e.message;}
})();