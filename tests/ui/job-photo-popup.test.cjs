const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const read=file=>fs.readFileSync(path.join(root,file),'utf8');
const settle=()=>new Promise(resolve=>setImmediate(resolve));
async function setup(t,readOnly=false,configure){
 const {w,fixture}=require('./job-detail-fixture.cjs').environment(t);
 for(const file of ['studio-page.js','notifications.js','job-workflow-display.js','job-workflow-graph.js'])w.eval(read(file));
 fixture.canEdit=!readOnly;
 if(configure)configure(w,fixture);
 await w.openWorkspaceJob('job');await settle();await settle();
 const tile=w.document.querySelector('[data-jd-asset]');
 tile.dispatchEvent(new w.MouseEvent('dblclick',{bubbles:true}));
 return {w,fixture,tile,dialog:w.document.querySelector('dialog.jd-photo-viewer')};
}
test('image popup separates the full preview from file details without duplicating its title',async t=>{
 const {w,dialog}=await setup(t);assert.ok(dialog);
 const image=dialog.querySelector('.jd-selected-preview img');
 assert.match(image.src,/\/preview$/);assert.doesNotMatch(image.src,/thumbnail/);
 assert.equal(dialog.querySelector('.jd-selected-preview').tagName,'FIGURE');
 assert.equal(dialog.querySelectorAll('h2').length,1);
 assert.match(dialog.querySelector('.jd-preview-mode').textContent,/Fit to window/);
 assert.equal(dialog.querySelector('form').parentElement.className,'jd-photo-inspector');
 assert.ok(dialog.querySelector('.jd-photo-inspector .jd-file-links'));
 assert.match(dialog.querySelector('[download]').href,/\/content$/);
 assert.equal(dialog.querySelector('[name=subjects]').size,5);
 dialog.querySelector('[data-close]').click();assert.equal(w.document.querySelector('dialog.jd-photo-viewer'),null);
});
test('photo details still save tags and classification through the existing media API',async t=>{
 const {w,fixture,dialog}=await setup(t);
 const input=dialog.querySelector('[data-tag-input]');input.value='Graduation, Approved';
 input.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));
 const form=dialog.querySelector('form');form.elements.collection.value='Unclassified';
 await form.onsubmit({preventDefault(){}});
 const saved=fixture.calls.find(c=>c.options?.method==='PUT'&&/\/media\/assets\//.test(c.url));assert.ok(saved);
 const payload=JSON.parse(saved.options.body);assert.deepEqual(payload.tags,['Graduation','Approved']);assert.equal(payload.collection,'Unclassified');
 assert.ok(dialog.querySelector('.jd-selected-preview img'));
 dialog.querySelector('[data-photo-next]').click();await settle();dialog.querySelector('[data-photo-previous]').click();await settle();
 assert.match(dialog.querySelector('.jd-tag-tokens').textContent,/Graduation.*Approved/);
});
test('keyboard opens the full preview and read-only users retain download and history access',async t=>{
 const {w,dialog,tile}=await setup(t,true);
 assert.equal(dialog.querySelector('[type=submit]'),null);
 assert.equal(dialog.querySelector('[name=collection]').disabled,true);
 assert.equal(dialog.querySelector('[data-retry-preview]'),null);
 assert.ok(dialog.querySelector('[data-versions]'));assert.ok(dialog.querySelector('[download]'));
 dialog.querySelector('[data-close]').click();
 tile.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));
 assert.ok(w.document.querySelector('dialog.jd-photo-viewer'));
});
test('popup CSS fits images without cropping and preserves a visible stacked layout on small screens',()=>{
 const css=read('css/pages/job-details.css');
 const image=css.match(/\.jd-selected-preview img \{([^}]+)\}/)[1];
 assert.match(image,/object-fit:contain/);assert.doesNotMatch(image,/cover/);
 const preview=css.match(/\.jd-selected-preview \{([^}]+)\}/)[1];assert.doesNotMatch(preview,/clamp\(76px/);
 assert.doesNotMatch(css,/\.job-detail-selected-file\s*\{\s*display:none/);
 assert.match(css,/@media\(max-width:760px\)[^\n]+flex-direction:column/);
 const shared=read('css/components/studio-dialog.css');assert.match(shared,/width:var\(--studio-dialog-width/);assert.match(shared,/padding:var\(--studio-dialog-body-padding/);
});

test('viewer arrows and keyboard move between photos and disable the job boundaries',async t=>{
 const {w,dialog}=await setup(t);await settle();
 const title=()=>dialog.querySelector('.studio-dialog-heading h2').textContent;
 assert.equal(title(),'Photo 0.jpg');assert.equal(dialog.querySelector('[data-photo-previous]').disabled,true);
 dialog.querySelector('[data-photo-next]').click();await settle();assert.equal(title(),'Photo 1.jpg');
 assert.equal(w.document.activeElement,dialog.querySelector('[data-photo-next]'));
 dialog.dispatchEvent(new w.KeyboardEvent('keydown',{key:'ArrowLeft',bubbles:true}));await settle();assert.equal(title(),'Photo 0.jpg');
 dialog.querySelector('[data-tag-input]').dispatchEvent(new w.KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));await settle();assert.equal(title(),'Photo 0.jpg');
});
test('viewer crosses API pages, skips documents and fetches only the open job',async t=>{
 const requests=[];
 const {dialog}=await setup(t,false,(w)=>{
  const original=w.json;
  const assets=Array.from({length:103},(_,i)=>({id:'photo-'+String(i).padStart(3,'0'),assetKey:'photo-'+i,versionId:'version-'+i,fileName:'Frame '+i+'.jpg',contentType:i===100?'application/pdf':'image/jpeg',subjects:[],collection:'Selections',tags:[],status:'Ready'}));
  w.json=async(url,options)=>{
   if(url.startsWith('/api/jobs/job/media?')){
    requests.push(url);const q=new URL(url,'https://studio.test').searchParams;
    if(q.get('limit')==='12')return {assets:[assets[99]],subjects:[],collections:['Selections'],totalAssets:103,totalSubjects:0,hasMore:false};
    const matching=assets.filter(a=>!q.get('after')||a.id>q.get('after'));return {assets:matching.slice(0,100),subjects:[],collections:['Selections'],totalAssets:103,totalSubjects:0,hasMore:matching.length>100};
   }
   return original(url,options);
  };
 });await settle();
 const title=()=>dialog.querySelector('.studio-dialog-heading h2').textContent;
 assert.equal(title(),'Frame 99.jpg');dialog.querySelector('[data-photo-next]').click();await settle();
 assert.equal(title(),'Frame 101.jpg');assert.ok(requests.some(url=>url.includes('after=photo-099')));
 dialog.querySelector('[data-photo-next]').click();await settle();assert.equal(title(),'Frame 102.jpg');assert.equal(dialog.querySelector('[data-photo-next]').disabled,true);
 dialog.querySelector('[data-photo-previous]').click();await settle();assert.equal(title(),'Frame 101.jpg');
 assert.equal(requests.filter(url=>url.includes('limit=100')).length,2);
 assert.match(dialog.querySelector('[data-photo-position]').textContent,/of 102/);
});
test('unsaved changes are retained when navigation is cancelled and changing jobs closes the viewer',async t=>{
 const {w,dialog}=await setup(t);await settle();let prompts=0;w.confirm=()=>{prompts++;return false;};
 const input=dialog.querySelector('[data-tag-input]');input.value='unsaved';input.dispatchEvent(new w.Event('input',{bubbles:true}));
 dialog.querySelector('[data-photo-next]').click();await settle();
 assert.equal(prompts,1);assert.equal(dialog.querySelector('[data-tag-input]').value,'unsaved');assert.match(dialog.querySelector('.studio-dialog-heading h2').textContent,/Photo 0/);
 w.confirm=()=>true;dialog.querySelector('[data-photo-next]').click();await settle();assert.match(dialog.querySelector('.studio-dialog-heading h2').textContent,/Photo 1/);
 await w.openWorkspaceJob('job2');await settle();assert.equal(w.document.querySelector('.jd-photo-viewer'),null);
});
test('closing the popup ignores a late photo-navigation response',async t=>{
 let release;
 const {w,dialog}=await setup(t,false,w=>{
  const original=w.json;
  w.json=(url,options)=>url.includes('/media?limit=100')?new Promise(resolve=>{release=resolve;}):original(url,options);
 });
 assert.equal(dialog.querySelector('[data-photo-next]').disabled,true);
 dialog.querySelector('[data-close]').click();
 release({assets:[],hasMore:false});await settle();
 assert.equal(w.document.querySelector('.jd-photo-viewer'),null);
});

test('Gallery opens the job collection grid and switches to full images and back',async t=>{
 const {w,dialog:photo}=await setup(t);photo.close();
 w.document.querySelector('[data-open-gallery]').click();await settle();
 const dialog=w.document.querySelector('.jd-photo-viewer');
 assert.equal(dialog.dataset.galleryMode,'grid');
 assert.equal(dialog.querySelector('.studio-dialog-heading h2').textContent,'Wedding selects review');
 assert.equal(dialog.querySelectorAll('.jd-gallery-tile').length,40);
 assert.equal(dialog.querySelector('.job-detail-selected-content').hidden,true);
 dialog.querySelector('[data-gallery-collection="Selections"]').click();
 assert.equal(dialog.querySelectorAll('.jd-gallery-tile').length,40);
 dialog.querySelector('[data-gallery-photo="image-2"]').click();
 assert.equal(dialog.dataset.galleryMode,'photo');
 assert.equal(dialog.querySelector('.jd-photo-inspector').hidden,true);
 assert.equal(dialog.querySelectorAll('.jd-photo-filmstrip button').length,40);
 assert.equal(dialog.querySelector('.jd-photo-filmstrip [aria-current=true]').dataset.galleryPhoto,'image-2');
 dialog.querySelector('[data-photo-details]').click();assert.equal(dialog.querySelector('.jd-photo-inspector').hidden,false);
 dialog.querySelector('[data-photo-favourite]').click();
 dialog.querySelector('[data-photo-grid]').click();dialog.querySelector('[data-gallery-favourites]').click();
 assert.equal(dialog.querySelectorAll('.jd-gallery-tile').length,1);
 assert.equal(dialog.querySelector('.jd-gallery-tile').dataset.galleryPhoto,'image-2');
 assert.equal(dialog.querySelector('.jd-photo-filmstrip').hidden,true);
});

test('gallery selection exports exact assets through the existing API and exposes errors',async t=>{
 const {w,dialog:photo}=await setup(t);photo.close();
 w.document.querySelector('[data-open-gallery]').click();await settle();
 const dialog=w.document.querySelector('.jd-photo-viewer'),exports=[];
 w.fetch=async(url,options)=>{exports.push({url,body:JSON.parse(options.body)});return{ok:false,text:async()=>'Permission denied'};};
 dialog.querySelector('[data-gallery-select]').click();
 for(const id of ['image-1','image-2'])dialog.querySelector(`[data-gallery-photo="${id}"]`).click();
 assert.equal(dialog.dataset.galleryMode,'grid');assert.equal(dialog.querySelector('[data-gallery-selected]').textContent,'2 selected');
 dialog.querySelector('[data-gallery-download]').click();await settle();
 assert.equal(exports[0].url,'/api/jobs/job/media/export');assert.deepEqual(exports[0].body.assets,['image-1','image-2']);
 assert.match(dialog.querySelector('[data-gallery-status]').textContent,/Permission denied/);
 assert.equal(dialog.querySelector('[data-gallery-download]').disabled,false);
});

test('read-only gallery keeps browsing and individual download without bulk editing actions',async t=>{
 const {w,dialog:photo}=await setup(t,true);photo.close();
 w.document.querySelector('[data-open-gallery]').click();await settle();
 const dialog=w.document.querySelector('.jd-photo-viewer');
 assert.equal(dialog.querySelector('[data-gallery-select]'),null);
 assert.equal(dialog.querySelector('[data-gallery-download]'),null);
 dialog.querySelector('[data-gallery-photo]').click();
 assert.match(dialog.querySelector('[data-photo-download]').href,/\/content$/);
});

test('gallery handles empty jobs, load failure and retry without stale content',async t=>{
 const {w,dialog:photo}=await setup(t);photo.close();const original=w.json;let fail=true;
 w.json=async(url,options)=>{if(url.includes('/media?limit=100')){if(fail)throw Error('Offline');return{assets:[],hasMore:false};}return original(url,options);};
 w.document.querySelector('[data-open-gallery]').click();await settle();
 const dialog=w.document.querySelector('.jd-photo-viewer');
 assert.match(dialog.querySelector('[data-gallery-status]').textContent,/Offline/);
 fail=false;dialog.querySelector('[data-gallery-retry]').click();await settle();
 assert.match(dialog.querySelector('.jd-gallery-mosaic').textContent,/No photographs/);
 assert.equal(dialog.querySelector('[data-gallery-more]').hidden,true);
});

test('Clients and Job Details can open the same gallery without mounting another page renderer',async t=>{
 const {w,dialog:photo,fixture}=await setup(t);photo.close();
 const dialog=await w.StudioJobDetails.openGallery(fixture.jobs[0]);await settle();
 assert.equal(dialog.dataset.galleryMode,'grid');assert.equal(dialog.querySelectorAll('.jd-gallery-tile').length,40);
 assert.equal(w.document.querySelectorAll('.jd-photo-viewer').length,1);
 dialog.querySelector('[data-gallery-photo="image-3"]').click();
 assert.match(dialog.querySelector('.studio-dialog-heading h2').textContent,/Photo 3/);
});

test('repeated standalone gallery opening keeps only the latest popup',async t=>{
 const {w,dialog:photo,fixture}=await setup(t);photo.close();
 const first=await w.StudioJobDetails.openGallery(fixture.jobs[0]);await settle();
 const second=await w.StudioJobDetails.openGallery(fixture.jobs[0]);await settle();
 assert.equal(first.isConnected,false);assert.equal(second.isConnected,true);
 assert.equal(w.document.querySelectorAll('.jd-photo-viewer').length,1);
});

test('Session Plan opens from the job media toolbar rather than the Actions card',async t=>{
 const {w,dialog}=await setup(t);dialog.close();const routes=[];
 w.studioNavigate=async(view,options)=>routes.push({view,options});
 assert.equal(w.document.querySelector('#studioContextRail [data-job-action="session-plan"]'),null);
 w.document.querySelector('.jd-gallery-toolbar [data-job-session-plan]').click();
 assert.deepEqual(JSON.parse(JSON.stringify(routes)),[{view:'session-plan',options:{jobId:'job'}}]);
});
