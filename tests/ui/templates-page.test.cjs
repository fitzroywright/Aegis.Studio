const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=file=>fs.readFileSync(path.join(root,file),'utf8'),settle=()=>new Promise(resolve=>setImmediate(resolve));
function setup(t,url='https://studio.test/#studio=templates'){
 const dom=new JSDOM('<body data-studio-view="templates"><div id="genericView"></div><div id="conversationView"></div></body>',{url,runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());const calls=[];
 Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),money:String,renderWorkspaceMenu(){}});
 w.json=async(url,options)=>{calls.push({url,options});if(url==='/api/admin/commercial/templates/Job')return[{id:'job',version:2,active:true,definition:{name:'School photography',description:'Job workflow',steps:[],products:[]}}];if(url==='/api/admin/commercial/templates/WorkOrder')return[{id:'order',version:1,active:true,definition:{name:'Photo printing',description:'Print workflow',steps:[],products:[],requiredRoles:[],skills:[]}}];return[];};
 for(const file of ['studio-page.js','dagre.min.js','workflow-graph.js','workflow-template-editor.js','commercial.js'])w.eval(read(file));w.esc=w.StudioPage.escape;
 w.studioDialog=(title,html)=>{const dialog=w.document.createElement('dialog');dialog.innerHTML=html;dialog.close=()=>dialog.remove();w.document.body.append(dialog);return dialog;};
 return{w,calls};
}
test('Templates lists jobs and work orders in the same page and uses each existing editor',async t=>{
 const {w,calls}=setup(t);w.studioNavigate=async(view,options)=>{assert.equal(view,'templates');return w.showCommercialTemplates(options.kind);};
 await w.showCommercialTemplates('Job');assert.equal(w.document.querySelector('h1').textContent,'Templates');assert.equal(w.document.querySelectorAll('[data-template-kind]').length,2);assert.match(w.document.querySelector('[data-template]').textContent,/School photography/);
 w.document.querySelector('[data-template-kind=WorkOrder]').click();await settle();assert.equal(w.document.querySelector('h1').textContent,'Templates');assert.match(w.document.querySelector('[data-template-row]').textContent,/Photo printing/);assert.equal(w.history.state.studioTemplateKind,'WorkOrder');w.document.querySelector('[data-edit]').click();await settle();assert.ok(w.document.querySelector('dialog .commercial-form'));w.document.querySelector('dialog').close();
 w.document.querySelector('[data-template-kind=Job]').click();await settle();assert.equal(w.history.state.studioTemplateKind,'Job');w.document.querySelector('[data-template]').click();await settle();assert.match(w.document.querySelector('h1').textContent,/Job Template Designer/);assert.ok(w.document.querySelector('[data-canvas]'));assert.ok(calls.some(call=>call.url==='/api/admin/workflow-templates/drafts'));
});
test('old Work Order Template bookmarks and Job Template links select the correct unified tab',async t=>{
 const {w}=setup(t,'https://studio.test/#studio=work-order-templates');w.eval(read('studio-navigation.js'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();assert.equal(w.document.body.dataset.studioView,'templates');assert.equal(w.history.state.studioTemplateKind,'WorkOrder');assert.match(w.document.querySelector('[data-template-row]').textContent,/Photo printing/);
 await w.studioNavigate('job-templates');assert.equal(w.document.body.dataset.studioView,'templates');assert.equal(w.history.state.studioTemplateKind,'Job');assert.ok(w.document.querySelector('[data-template]'));
 w.history.back();await new Promise(resolve=>setTimeout(resolve,35));assert.equal(w.history.state.studioTemplateKind,'WorkOrder');assert.ok(w.document.querySelector('[data-template-row]'));
});
