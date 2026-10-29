let frontDeskPosWindow=null;
const frontDeskPosWindows=new Set();
async function launchFrontDeskPos(action,context={}){
    if(document.body.dataset.studioView!=='frontdesk')throw Error('Financial actions are available from Front Desk.');
    const reusable=frontDeskPosWindow&&!frontDeskPosWindow.closed&&!frontDeskPosWindow.studioPosDirty;
    const target=reusable?frontDeskPosWindow:window.open('about:blank',`StudioPos-${crypto.randomUUID()}`,`popup=yes,width=${screen.availWidth},height=${screen.availHeight},left=0,top=0`);
    if(!target)throw Error('Allow popups for Studio to open this financial operation.');
    try{const launch=await json('/api/frontdesk/pos/launch',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,...context})});frontDeskPosWindow=target;frontDeskPosWindows.add(target);target.location.href=launch.url;target.focus();}
    catch(error){if(!reusable)target.close();throw error;}
}
async function pickFrontDeskFinancialAction(action){
    if(document.body.dataset.studioView!=='frontdesk')return alert('Use Front Desk for financial actions.');
    if(action==='CloseRegister')return launchFrontDeskPos(action);
    if(action==='RecordDeposit'){
        const bookings=frontDeskRequests.filter(b=>!['Confirmed','Cancelled'].includes(b.status));
        const d=studioDialog('Record Deposit',bookings.map(b=>`<button class="action" data-booking="${b.id}">${esc(b.request.clientName)} · ${esc(b.request.title)}</button>`).join('')||'<p>No pending bookings.</p>');
        d.querySelectorAll('[data-booking]').forEach(b=>b.onclick=async()=>{await launchFrontDeskPos(action,{bookingId:b.dataset.booking});d.close();});return;
    }
    const jobs=frontDeskJobs.filter(j=>!frontDeskSelectedClientId||j.clientId===frontDeskSelectedClientId);
    const d=studioDialog(action==='TakePayment'?'Take Payment':action==='IssueRefund'?'Issue Refund':'New Sale',`<form class="subject-form"><p>Choose the client Job for this ${action==='TakePayment'?'payment':'sale'}.</p><label>Job / Client<select data-job-picker name="jobId" required><option value="">Choose a job…</option>${StudioSubjectPicker.jobOptions(jobs)}</select></label><button class="primary">Continue</button><p role="alert"></p></form>`);
    const form=d.querySelector('form');form.onsubmit=async e=>{e.preventDefault();const id=form.elements.jobId.value;if(!id)return;const button=form.querySelector('.primary');button.disabled=true;try{if(['IssueRefund','ViewReceipt'].includes(action)){await pickFrontDeskSale(action,id);}else{await launchFrontDeskPos(action,{jobId:id});}d.close();}catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}};
}
async function pickFrontDeskSale(action,jobId){
    const sales=await json(`/api/jobs/${jobId}/pos/sales`);const d=studioDialog(action==='IssueRefund'?'Issue Refund':'View Receipt',sales.filter(s=>s.status==='Completed').map(s=>`<button class="action" data-sale="${s.id}">${esc(new Date(s.createdAtUtc).toLocaleString())} · ${money(s.total)} · ${esc(s.id.slice(0,8))}</button>`).join('')||'<p>No completed sales.</p>');
    d.querySelectorAll('[data-sale]').forEach(b=>b.onclick=async()=>{await launchFrontDeskPos(action,{jobId,saleId:b.dataset.sale});d.close();});
}
window.addEventListener('message',async event=>{
    if(event.origin!==location.origin||!frontDeskPosWindows.has(event.source)||event.data?.type!=='studio-pos-completed')return;
    if(document.body.dataset.studioView==='delivery'){await StudioWorkflowViews.showDelivery();return;}
    if(document.body.dataset.studioView!=='frontdesk')return;
    const positions=[...genericView.querySelectorAll('*')].filter(el=>el.scrollTop||el.scrollLeft).map(el=>({id:el.id,cls:el.className,top:el.scrollTop,left:el.scrollLeft}));
    try{const [jobs,requests]=await Promise.all([json('/api/jobs'),json('/api/frontdesk/booking-requests')]);frontDeskJobs=jobs;frontDeskRequests=requests;frontDeskDetails.clear();renderFrontDesk();for(const p of positions){const el=p.id?document.getElementById(p.id):[...genericView.querySelectorAll('*')].find(e=>e.className===p.cls);if(el){el.scrollTop=p.top;el.scrollLeft=p.left;}}}catch(error){console.error('Front Desk update failed',error);}
});


