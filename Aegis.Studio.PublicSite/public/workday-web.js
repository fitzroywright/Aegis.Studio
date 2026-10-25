const el=id=>document.getElementById(id);
const status=message=>{el('workday-status').textContent=message;};
async function call(path,options={}){
  const response=await fetch(path,{credentials:'same-origin',...options});
  const contentType=response.headers.get('content-type')||'';
  const body=contentType.includes('json')?await response.json():null;
  if(!response.ok)throw Error(body?.message||body?.error||'Request failed ('+response.status+').');
  return body;
}
function assignmentLabel(item){
  const date=item.startsAtUtc||item.StartsAtUtc||'';
  return (item.title||item.Title||'Assigned session')+(date?' · '+new Date(date).toLocaleString('en-JM'):'');
}
let currentAssignments=[];
async function loadWorkday(){
  try{
    const me=await call('/api/v1/auth/me');
    const bootstrap=await call('/api/v1/workday/bootstrap');
    el('workday-login').hidden=true;
    el('workday-panel').hidden=false;
    el('workday-user').textContent=me.displayName||me.userName;
    currentAssignments=[...(bootstrap.today||bootstrap.Today||[]),...(bootstrap.upcoming||bootstrap.Upcoming||[])];
    const select=el('workday-assignment');select.replaceChildren();
    select.add(new Option('Choose an assigned Job',''));
    for(const assignment of currentAssignments){
      if(!assignment.id&&!assignment.Id)continue;
      select.add(new Option(assignmentLabel(assignment),assignment.id||assignment.Id));
    }
    const list=el('workday-jobs');list.replaceChildren();
    for(const assignment of currentAssignments){
      const li=document.createElement('li');
      li.textContent=assignmentLabel(assignment);
      list.append(li);
    }
    status(currentAssignments.length+' assigned sessions available. Uploads synchronize to Studio for approval.');
  }catch(error){el('workday-login').hidden=false;el('workday-panel').hidden=true;status('Sign in to WorkDay to see your assigned Jobs.');}
}
el('workday-signin').addEventListener('submit',async event=>{
  event.preventDefault();
  const form=event.currentTarget,submit=form.querySelector('[type=submit]');
  submit.disabled=true;status('Signing in…');
  try{
    await call('/api/v1/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({userName:form.elements.userName.value,password:form.elements.password.value})});
    form.elements.password.value='';
    await loadWorkday();
  }catch(error){status(error.message);}finally{submit.disabled=false;}
});
el('workday-signout').addEventListener('click',async()=>{
  try{await call('/api/v1/auth/logout',{method:'POST'});}finally{el('workday-panel').hidden=true;el('workday-login').hidden=false;status('Signed out.');}
});
el('workday-upload-form').addEventListener('submit',async event=>{
  event.preventDefault();
  const form=event.currentTarget,assignment=form.elements.assignmentId.value,files=Array.from(form.elements.photos.files||[]);
  if(!assignment||!currentAssignments.some(x=>String(x.id||x.Id)===assignment)){status('Choose one of your assigned Jobs.');return;}
  if(!files.length){status('Choose photographs to upload.');return;}
  const button=form.querySelector('[type=submit]');button.disabled=true;
  let completed=0;
  for(const file of files){
    if(!/image\/(jpeg|png|webp)/.test(file.type)||file.size>25000000){
      status('Unsupported image or file too large: '+file.name);break;
    }
    const id=crypto.randomUUID(),body=new FormData();
    body.append('file',file,file.name);body.append('uploadId',id);
    try{
      await new Promise((resolve,reject)=>{
        const xhr=new XMLHttpRequest();
        xhr.open('POST','/api/v1/workday/assignments/'+encodeURIComponent(assignment)+'/uploads');
        xhr.withCredentials=true;
        xhr.upload.onprogress=e=>{if(e.lengthComputable)status('Uploading '+file.name+' — '+Math.round(100*e.loaded/e.total)+'%');};
        xhr.onload=()=>xhr.status===202?resolve():reject(Error('Upload rejected ('+xhr.status+').'));
        xhr.onerror=()=>reject(Error('Connection failed. Check upload status before retrying.'));
        xhr.send(body);
      });
      completed++;status(completed+' of '+files.length+' photographs queued for Studio synchronization.');
    }catch(error){status('Upload '+file.name+' could not be confirmed: '+error.message);break;}
  }
  button.disabled=false;
  if(completed===files.length)form.elements.photos.value='';
});
loadWorkday();
