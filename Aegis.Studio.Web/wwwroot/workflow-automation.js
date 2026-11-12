/* Workflow Automation page; existing workflow APIs and editor retain ownership. */
window.StudioWorkflowAutomation=(()=>{
 'use strict';
 const esc=StudioPage.escape;
 async function render(){
  const current=StudioPage.begin('workflow-automation');
  commercialPage('Workflow Automation','<div class="commercial-grid" data-template-automation aria-label="Workflow Automation"><p role="status">Checking access…</p></div>','');
  const host=genericView.querySelector('[data-template-automation]'),live=()=>current()&&host.isConnected;
  try{
   const me=await json('/api/auth/me');if(!live())return;
   if((me?.user||me)?.isAdministrator!==true){host.innerHTML='<p>Administrator access required to manage workflow automation.</p>';return;}
   host.innerHTML=renderWorkflowSection();await refreshWorkflowList();if(live())StudioPage.setCount(host.querySelectorAll('#workflowList .job-row').length+' workflows');
  }catch(error){if(live()){host.innerHTML='<p role="alert">'+esc(error.message)+'</p><button type="button" class="action" data-automation-retry>Retry</button>';host.querySelector('[data-automation-retry]').onclick=render;}}
 }
 return {render};
})();
