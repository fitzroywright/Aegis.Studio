const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{randomUUID}=require('node:crypto'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.PublicSite/public');
const assignment='44444444-4444-4444-8444-444444444444',other='66666666-6666-4666-8666-666666666666';
const wait=async predicate=>{for(let i=0;i<100;i++){if(predicate())return;await new Promise(r=>setTimeout(r,10));}assert.fail('UI did not reach expected state');};
function setup(t,state={user:'alice',photos:new Map()}){
  const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://web.bryanstudiosltd.com/#workday',runScripts:'outside-only'}),w=dom.window;
  t.after(()=>w.close());const revoked=[],posts=[],requests=[];
  Object.defineProperty(w.crypto,'randomUUID',{value:randomUUID});
  w.URL.createObjectURL=()=> 'blob:'+randomUUID();w.URL.revokeObjectURL=url=>revoked.push(url);
  const reply=body=>({ok:true,headers:{get:()=> 'application/json'},json:async()=>body});
  w.fetch=async(url,options={})=>{
    requests.push(String(url));
    if(url.endsWith('/auth/me'))return reply({userName:state.user,displayName:state.user});
    if(url.endsWith('/workday/bootstrap'))return reply({today:[{id:assignment,title:'Portraits'}],upcoming:[{id:other,title:'Wedding'}]});
    if(url.endsWith('/auth/login')||url.endsWith('/auth/logout'))return reply({});
    const match=String(url).match(/assignments\/([^/]+)\/uploads/);
    if(match)return reply({items:[...state.photos.values()].filter(x=>x.owner===state.user&&x.assignmentId===match[1]),nextCursor:null});
    throw Error('Unexpected fetch '+url);
  };
  class Xhr{
    constructor(){this.upload={};}
    open(method,url){this.url=url;}
    send(form){
      const item={uploadId:form.get('uploadId'),fileName:form.get('file').name,assignmentId:this.url.split('/')[5],state:'Pending',owner:state.user};
      posts.push(item);this.item=item;state.lastXhr=this;
      if(state.stall)return;
      queueMicrotask(()=>{
        if(state.failOnce){state.failOnce=false;this.onerror();return;}
        this.upload.onprogress?.({lengthComputable:true,loaded:1,total:2});
        state.photos.set(item.uploadId,item);this.status=202;this.responseText=JSON.stringify({uploadId:item.uploadId,storageStatus:'Pending'});this.onload();
      });
    }
    abort(){this.aborted=true;this.onabort?.();}
  }
  w.XMLHttpRequest=Xhr;
  w.eval(fs.readFileSync(path.join(root,'workday-web.js'),'utf8'));
  const get=id=>w.document.getElementById(id);
  const choose=async id=>{get('workday-assignment').value=id;get('workday-assignment').dispatchEvent(new w.Event('change'));await wait(()=>get('workday-gallery-message').textContent==='');};
  const drop=files=>{const event=new w.Event('drop',{bubbles:true,cancelable:true});Object.defineProperty(event,'dataTransfer',{value:{files}});get('workday-dropzone').dispatchEvent(event);assert.equal(event.defaultPrevented,true);};
  const file=(name,type='image/png')=>new w.File(['test photograph'],name,{type,lastModified:1});
  return {w,get,choose,drop,file,state,revoked,posts,requests};
}
test('drop zone and multi-file picker share one selection and reject invalid or duplicate files',async t=>{
  const f=setup(t);await wait(()=>!f.get('workday-panel').hidden);
  assert.equal(f.get('workday-browse').disabled,true);await f.choose(assignment);
  const portrait=f.file('portrait.png');
  f.drop([portrait,portrait,f.file('document.txt','text/plain')]);
  assert.equal(f.get('workday-photo-grid').children.length,1);
  const originalImage=f.get('workday-photo-grid').querySelector('img');
  assert.match(f.get('workday-status').textContent,/2 skipped/);
  const input=f.get('workday-photos');
  assert.equal(input.multiple,true);assert.equal(input.required,false);
  let opened=0;input.addEventListener('click',()=>opened++);f.get('workday-browse').click();assert.equal(opened,1);
  Object.defineProperty(input,'files',{value:[f.file('second.png'),f.file('third.jpg','image/jpeg')],configurable:true});
  input.dispatchEvent(new f.w.Event('change'));
  assert.equal(f.get('workday-photo-grid').children.length,3);
  assert.equal(f.get('workday-photo-grid').querySelector('img'),originalImage);
  assert.equal(f.get('workday-upload').disabled,false);
  f.get('workday-clear').click();assert.equal(f.get('workday-photo-grid').children.length,0);assert.equal(f.revoked.length,3);
});
test('multi-file upload continues after a failure and Retry reuses the same operation',async t=>{
  const state={user:'alice',photos:new Map(),failOnce:true},f=setup(t,state);await wait(()=>!f.get('workday-panel').hidden);await f.choose(assignment);
  f.drop([f.file('first.png'),f.file('second.png')]);f.get('workday-upload-form').dispatchEvent(new f.w.Event('submit',{cancelable:true}));
  await wait(()=>f.posts.length===2&&f.get('workday-assignment').disabled===false);
  assert.equal(f.get('workday-photo-grid').children.length,2);
  const retry=[...f.get('workday-photo-grid').querySelectorAll('button')].find(b=>b.textContent==='Retry');assert.ok(retry);retry.click();
  await wait(()=>f.posts.length===3&&f.get('workday-assignment').disabled===false);
  assert.equal(f.posts[0].uploadId,f.posts[2].uploadId);
  assert.equal(f.state.photos.size,2);
  for(const photo of state.photos.values())photo.state='Acknowledged';
  f.get('workday-gallery-refresh').click();await wait(()=>f.get('workday-photo-grid').textContent.includes('Acknowledged by Studio'));
  assert.equal(f.get('workday-photo-grid').querySelectorAll('progress').length,0);
  await f.choose(other);assert.equal(f.get('workday-photo-grid').children.length,0);
  await f.choose(assignment);assert.equal(f.get('workday-photo-grid').children.length,2);
  const reloaded=setup(t,state);await wait(()=>!reloaded.get('workday-panel').hidden);await reloaded.choose(assignment);
  assert.equal(reloaded.get('workday-photo-grid').children.length,2);
});
test('sign out aborts an upload and clears thumbnails before another account signs in',async t=>{
  const f=setup(t,{user:'alice',photos:new Map(),stall:true});await wait(()=>!f.get('workday-panel').hidden);await f.choose(assignment);
  f.drop([f.file('private.png')]);f.get('workday-upload-form').dispatchEvent(new f.w.Event('submit',{cancelable:true}));
  await wait(()=>f.posts.length===1);f.get('workday-signout').click();await wait(()=>f.get('workday-panel').hidden);
  assert.equal(f.state.lastXhr.aborted,true);assert.equal(f.get('workday-photo-grid').children.length,0);assert.equal(f.revoked.length,1);
  f.state.user='bob';const form=f.get('workday-signin');form.elements.userName.value='bob';form.elements.password.value='test-only-passphrase';
  form.dispatchEvent(new f.w.Event('submit',{cancelable:true}));await wait(()=>!f.get('workday-panel').hidden);await f.choose(assignment);
  assert.equal(f.get('workday-photo-grid').children.length,0);assert.equal(f.get('workday-user').textContent,'bob');
});
