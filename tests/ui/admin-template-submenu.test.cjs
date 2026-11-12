const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const source=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-admin.js'),'utf8');
const settle=()=>new Promise(r=>setImmediate(r));
async function setup(t,{admin=true,pending=false}={}){
 const dom=new JSDOM('<div id="host"></div>',{runScripts:'outside-only',pretendToBeVisual:true});t.after(()=>dom.window.close());const w=dom.window,navigation=[];
 w.studioSignedInUserReady=Promise.resolve({isAdministrator:admin});w.StudioAccess={pending:()=>pending};w.studioNavigate=(view,options)=>navigation.push({view,options});w.eval(source);const host=w.document.getElementById('host');w.StudioAdmin.mountMenu(host);await settle();return {w,host,navigation,menu:host.querySelector('.studio-admin-menu'),group:host.querySelector('.studio-admin-submenu')};
}
test('Admin Templates contains exactly three template destinations; automation is a separate entry',async t=>{
 const {host,group,navigation}=await setup(t);assert.equal(group.querySelector('summary span').textContent,'Templates');
 assert.deepEqual([...group.querySelectorAll('button')].map(b=>b.textContent),['Job Templates','Work Order Templates','Session Plan Templates']);
 assert.equal(host.querySelector('[data-studio-admin-view="workflows-admin"]').closest('.studio-admin-submenu'),null);
 const job=group.querySelector('[data-studio-admin-view="templates"]');job.click();assert.equal(navigation.at(-1).view,'templates');assert.equal(navigation.at(-1).options.kind,'Job');
 group.querySelector('[data-studio-admin-view="work-order-templates"]').click();assert.equal(navigation.at(-1).view,'work-order-templates');
 group.querySelector('[data-studio-admin-view="session-plan-templates"]').click();assert.equal(navigation.at(-1).view,'session-plan-templates');
 host.querySelector('[data-studio-admin-view="workflows-admin"]').click();assert.equal(navigation.at(-1).view,'workflows-admin');
});
test('keyboard opens, traverses and closes the nested group without closing Admin first',async t=>{
 const {w,menu,group}=await setup(t);menu.open=true;await settle();const summary=group.querySelector('summary');summary.focus();
 const key=k=>w.document.activeElement.dispatchEvent(new w.KeyboardEvent('keydown',{key:k,bubbles:true,cancelable:true}));
 key('ArrowRight');assert.equal(group.open,true);assert.equal(w.document.activeElement.textContent,'Job Templates');key('ArrowDown');assert.equal(w.document.activeElement.textContent,'Work Order Templates');key('Escape');assert.equal(group.open,false);assert.equal(menu.open,true);assert.equal(w.document.activeElement,summary);
 key('ArrowDown');assert.equal(w.document.activeElement.textContent,'Workflow Automation');key('Escape');assert.equal(menu.open,false);
});
test('non-administrators and pending accounts cannot see the template group or automation',async t=>{
 for(const pending of [false,true]){const {host,group}=await setup(t,{admin:false,pending});assert.equal(group.hidden,true);assert.equal(host.querySelector('[data-studio-admin-view="workflows-admin"]').hidden,true);assert.equal(host.querySelector('[data-studio-admin-view="about"]').hidden,false);assert.equal(host.querySelector('[data-studio-admin-view="workspace-management"]').hidden,pending);}
});
test('navigation and outside clicks collapse both levels',async t=>{
 const {w,menu,group}=await setup(t);menu.open=true;group.open=true;await settle();w.dispatchEvent(new w.Event('studio-view-changed'));assert.equal(menu.open,false);assert.equal(group.open,false);menu.open=true;group.open=true;await settle();w.document.body.click();assert.equal(menu.open,false);assert.equal(group.open,false);
});
