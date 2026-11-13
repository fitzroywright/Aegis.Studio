/* Finance and Management use the same jobs, transactions, sessions and tasks. */
let studioPeriodDays=90;
const studioColors=['#1677ff','#26c58b','#9454ff','#ff9400','#f34865','#05a7b9'];
function studioFinancials(job){
    const events=job.financialEvents||[];
    const charges=events.filter(e=>/^(charge|adjustment|discount|POS Sale|POS Refund)$/i.test(e.kind)).reduce((n,e)=>n+Number(e.amount),0);
    const received=events.filter(e=>/^(payment|deposit|refund|reversal|POS Sale|POS Refund)$/i.test(e.kind)).reduce((n,e)=>n+Number(e.amount),0);
    return {charges,received,balance:Math.max(0,charges-received)};
}
function studioMetric(value,label,color='blue',detail='',drill=''){
    return `<article class="op-metric" ${drill?`role="button" tabindex="0" data-metric="${drill}"`:""}><span class="op-icon ${color}">${color==='green'?'$':color==='purple'?'▧':color==='orange'?'▣':'✓'}</span><span><b>${esc(value)}</b><span>${esc(label)}</span>${detail?`<small>${esc(detail)}</small>`:''}</span></article>`;
}
function studioCard(title,body,action=''){
    return `<section class="op-card"><header><h2>${esc(title)}</h2>${action}</header><div class="op-body">${body}</div></section>`;
}
function studioPeriod(){return `<select data-period aria-label="Reporting period"><option value="30" ${studioPeriodDays===30?'selected':''}>Last 30 days</option><option value="90" ${studioPeriodDays===90?'selected':''}>Last 90 days</option><option value="365" ${studioPeriodDays===365?'selected':''}>Last year</option></select>`;}
async function studioReadOperations(){
    const now=new Date(),start=new Date(now.getTime()-studioPeriodDays*86400000),end=new Date(now.getTime()+30*86400000);
    const [jobs,tasks,sessions,photographers,invoices]=await Promise.all([
        json('/api/jobs'),json('/api/operations/activities'),json('/api/schedule/entries?from='+encodeURIComponent(start.toISOString())+'&to='+encodeURIComponent(end.toISOString())),json('/api/photographers'),json('/api/accounts/invoices')
    ]);
    const transactions=jobs.flatMap(job=>(job.financialEvents||[]).map(e=>({...e,jobId:job.id,client:job.clientName,title:job.title}))).filter(e=>new Date(e.createdAt||e.createdAtUtc)>=start&&new Date(e.createdAt||e.createdAtUtc)<=now);
    const receipts=transactions.filter(e=>/^(payment|deposit|refund|reversal|POS Sale|POS Refund)$/i.test(e.kind));
    const balances=jobs.map(job=>({job,...studioFinancials(job)}));
    return {jobs,tasks,sessions,photographers,invoices,transactions,receipts,balances,now,start};
}
function studioRevenueChart(receipts){
    const days=Array.from({length:12},(_,i)=>new Date(Date.now()-(12-i)*studioPeriodDays/12*86400000));
    const totals=days.map((day,i)=>receipts.filter(e=>new Date(e.createdAt||e.createdAtUtc)>=day&&(i===11||new Date(e.createdAt||e.createdAtUtc)<days[i+1])).reduce((n,e)=>n+Number(e.amount),0));
    const max=Math.max(1,...totals),min=Math.min(0,...totals),scale=125/(max-min),zero=150+min*scale;
    const y=n=>zero-n*scale,points=totals.map((n,i)=>`${42+i*32},${y(n)}`).join(' ');
    return `<svg class="op-chart" viewBox="0 0 420 185" role="img" aria-label="Net receipts over the selected period"><path d="M40 15V150M40 ${zero}H407" fill="none" stroke="#dce2ea"/><text x="2" y="20">${esc(money(max))}</text><text x="16" y="${zero}">0</text>${totals.map((n,i)=>`<rect x="${42+i*32}" y="${Math.min(zero,y(n))}" width="20" height="${Math.abs(n*scale)}" fill="${n<0?'#f34865':'#1677ff'}"><title>${money(n)}</title></rect>`).join('')}<polyline points="${points}" fill="none" stroke="#9454ff" stroke-width="2"/><text x="42" y="173">${days[0].toLocaleDateString()}</text><text x="340" y="173">Today</text></svg>`;
}
function studioComposition(values,label){
    const total=values.reduce((n,x)=>n+x.value,0);let offset=0;
    const gradient=values.map((x,i)=>{const first=offset;offset+=total?x.value/total*100:0;return `${studioColors[i%studioColors.length]} ${first}% ${offset}%`;}).join(',');
    return `<div class="op-composition"><div class="op-donut" style="background:${total?'conic-gradient('+gradient+')':'#edf0f4'}"><span><b>${esc(total.toLocaleString())}</b><small>${esc(label)}</small></span></div><div class="op-legend">${values.map((x,i)=>`<span><i style="background:${studioColors[i%studioColors.length]}"></i>${esc(x.label)} <b>${total?Math.round(x.value/total*100):0}%</b></span>`).join('')}</div></div>`;
}
function studioTransactionsTable(transactions){
    return `<table class="studio-table"><thead><tr><th>Date</th><th>Client</th><th>Description / Method</th><th>Amount</th></tr></thead><tbody>${transactions.slice().sort((a,b)=>new Date(b.createdAt||b.createdAtUtc)-new Date(a.createdAt||a.createdAtUtc)).slice(0,100).map(e=>`<tr><td>${esc(new Date(e.createdAt||e.createdAtUtc).toLocaleDateString())}</td><td><button data-open-job="${e.jobId}">${esc(e.client)}</button></td><td>${esc(e.kind)}<small>${esc(e.note||'')} · ${esc(e.paymentMethod||'Method unrecorded')} · ${esc(e.recordedBy||'')}</small></td><td>${esc(money(e.amount))}</td></tr>`).join('')}</tbody></table>`;
}
function studioExportTransactions(transactions){
    const quote=value=>'"'+String(value??'').replace(/"/g,'""')+'"';
    const rows=[['Date','Client','Job','Type','Amount','Method','Recorded by','Note'],...transactions.map(e=>[e.createdAt||e.createdAtUtc,e.client,e.title,e.kind,e.amount,e.paymentMethod,e.recordedBy,e.note])];
    const url=URL.createObjectURL(new Blob([rows.map(row=>row.map(quote).join(',')).join('\r\n')],{type:'text/csv;charset=utf-8'}));
    const a=document.createElement('a');a.href=url;a.download='studio-transactions.csv';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
function studioBindOperations(data,refresh){
    StudioPage.queryAll(genericView,'[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));
    StudioPage.query(genericView,'[data-period]').onchange=e=>{studioPeriodDays=Number(e.target.value);refresh().catch(e=>alert(e.message));};
    StudioPage.query(genericView,'[data-export]').onclick=()=>studioExportTransactions(data.transactions);
    StudioPage.queryAll(genericView,'[data-metric]').forEach(b=>{b.onclick=()=>{const values=b.dataset.metric==='receipts'?data.receipts:data.tasks.filter(t=>b.dataset.metric==='blocked'?t.status==='Blocked':!['Completed','Cancelled'].includes(t.status)&&t.dueAtUtc&&new Date(t.dueAtUtc)<data.now);const d=studioDialog(b.dataset.metric==='receipts'?'Receipt history':'Work requiring attention',b.dataset.metric==='receipts'?studioTransactionsTable(values):values.slice(0,100).map(t=>`<button data-open-job="${t.jobId}">${esc(t.title)} · ${esc(t.blockedReason||t.status)}</button>`).join('')||'<p>No matching work.</p>');d.querySelectorAll('[data-open-job]').forEach(x=>x.onclick=()=>{d.close();openWorkspaceJob(x.dataset.openJob).catch(e=>alert(e.message));});};b.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();b.click();}};});
}

/* Compact reports use the existing authorized Studio projections; no invented totals. */
function studioReportNumber(value){return Number.isFinite(Number(value))?Number(value):0;}
function studioReportPanel(title,content,note=''){
 return `<section class="studio-report-panel"><header><h2>${esc(title)}</h2>${note?`<small>${esc(note)}</small>`:''}</header><div class="studio-report-content">${content}</div></section>`;
}
function studioReportStrip(items){
 return '<div class="studio-report-strip">'+items.map(([label,value])=>`<div><small>${esc(label)}</small><strong>${esc(value)}</strong></div>`).join('')+'</div>';
}
function studioReportBars(values,{currency=false,limit=12}={}){
 const rows=values.slice(0,limit),max=Math.max(1,...rows.map(x=>Math.abs(studioReportNumber(x.value))));
 return `<div class="studio-report-bars">${rows.map(x=>{const value=studioReportNumber(x.value),width=Math.max(0,Math.abs(value)/max*100);return `<div class="studio-report-bar-row"><span title="${esc(x.label)}">${esc(x.label)}</span><div class="studio-report-track"><i style="width:${width}%" class="${value<0?'is-negative':''}"></i></div><strong>${esc(currency?money(value):value.toLocaleString())}</strong></div>`}).join('')||'<p>No records for this period.</p>'}</div>`;
}
function studioReportTable(headings,rows){
 return `<div class="studio-report-table-scroll"><table class="studio-report-table"><thead><tr>${headings.map(h=>`<th scope="col">${esc(h)}</th>`).join('')}</tr></thead><tbody>${rows.join('')||`<tr><td colspan="${headings.length}">No matching records.</td></tr>`}</tbody></table></div>`;
}
function studioReportJobStages(jobs){
 const groups=new Map();
 for(const job of jobs){const name=String(job.status||'Unknown');groups.set(name,(groups.get(name)||0)+1);}
 return studioReportBars([...groups].map(([label,value])=>({label,value})).sort((a,b)=>b.value-a.value));
}
function studioReportSessionDays(sessions,now){
 const days=Array.from({length:14},(_,i)=>{const d=new Date(now);d.setHours(0,0,0,0);d.setDate(d.getDate()+i);return d;});
 return studioReportBars(days.map(d=>({label:d.toLocaleDateString('en-JM',{weekday:'short',day:'numeric',month:'short'}),value:sessions.filter(s=>String(s.status).toLowerCase()!=='cancelled'&&new Date(s.startsAtUtc).toDateString()===d.toDateString()).length})));
}
function studioReportAgeing(balances,now){
 const buckets=[{label:'Not yet due / no due date',value:0},{label:'1–30 days',value:0},{label:'31–60 days',value:0},{label:'61–90 days',value:0},{label:'Over 90 days',value:0}];
 for(const row of balances.filter(x=>x.balance>0)){
  const due=row.job.dueAtUtc||row.job.paymentDueAtUtc;
  const days=due?Math.floor((now-new Date(due))/86400000):0;
  const index=!Number.isFinite(days)||days<=0?0:days<=30?1:days<=60?2:days<=90?3:4;
  buckets[index].value+=row.balance;
 }
 return studioReportBars(buckets,{currency:true});
}
function studioReportWorkload(photographers,tasks,sessions){
 const rows=photographers.map(p=>{
  const assigned=tasks.filter(t=>t.assignedTo===p.userName&&!['Completed','Cancelled'].includes(t.status));
  const planned=sessions.filter(x=>(x.resourceIds||[]).includes(p.scheduleResourceId)&&String(x.status).toLowerCase()!=='cancelled');
  return {name:p.displayName||p.userName,open:assigned.length,blocked:assigned.filter(t=>t.status==='Blocked').length,shoots:planned.length};
 }).sort((a,b)=>b.open-a.open||b.shoots-a.shoots);
 return studioReportTable(['Photographer','Open tasks','Blocked','Sessions'],rows.map(x=>`<tr><td>${esc(x.name)}</td><td>${x.open}</td><td>${x.blocked}</td><td>${x.shoots}</td></tr>`));
}
function studioReportInvoiceStatuses(invoices){
 const counts=new Map();for(const x of invoices){const key=x.status||'Unknown';counts.set(key,(counts.get(key)||0)+1);}
 return studioReportBars([...counts].map(([label,value])=>({label,value})));
}
function studioReportMethods(receipts){
 const groups=new Map();for(const e of receipts){const k=e.paymentMethod||'Not recorded';groups.set(k,(groups.get(k)||0)+studioReportNumber(e.amount));}
 return studioReportBars([...groups].map(([label,value])=>({label,value})).sort((a,b)=>b.value-a.value),{currency:true});
}


/* Each report is its own view; no giant scrolling dashboard of mixed reports. */
const studioReportLayouts={
 finance:[
  ['daily','Daily Finance',['Daily Finance Report']],
  ['overview','Financial Overview',['Net receipts over time','Payment methods','Transaction mix']],
  ['transactions','Transactions',['Transactions — search, filter and export']],
  ['receivables','Receivables',['Outstanding by job','Receivables and contractual credit']],
  ['invoices','Invoices',['Invoice status','Invoices']]
 ],
 management:[
  ['overview','Executive Summary',['Receipt trend','Top clients by receipts','Invoice status']],
  ['workflow','Jobs & Workflow',['Jobs by stage','Task status','Pipeline summary']],
  ['schedule','Sessions & Staffing',['Upcoming sessions by day','Photographer workload','Upcoming sessions']],
  ['exceptions','Exceptions',['Exceptions requiring attention']]
 ]
};
const studioReportActive={finance:'overview',management:'overview'};
function studioOrganizeReports(type){
 const root=genericView.querySelector('.studio-report-sheet');
 if(!root)return;
 const layout=studioReportLayouts[type],panels=[...root.querySelectorAll('.studio-report-panel')];
 const strip=root.querySelector('.studio-report-strip');
 const nav=document.createElement('nav');nav.className='studio-report-nav';nav.setAttribute('aria-label',type==='finance'?'Finance reports':'Management reports');
 root.prepend(nav);
 const views=new Map();
 for(const [id,label,titles] of layout){
  const button=document.createElement('button');button.type='button';button.textContent=label;button.dataset.reportTarget=id;nav.append(button);
  const view=document.createElement('section');view.className='studio-report-view';view.dataset.reportView=id;view.setAttribute('aria-label',label);
  root.append(view);views.set(id,view);
  const chartPanels=panels.filter(panel=>titles.includes(panel.querySelector('header h2')?.textContent));
  if(chartPanels.length>1){
   const charts=document.createElement('div');charts.className='studio-report-chart-grid';view.append(charts);
   chartPanels.forEach(panel=>charts.append(panel));
  }else chartPanels.forEach(panel=>view.append(panel));
 }
 const originalGrid=root.querySelector('.studio-report-chart-grid');
 const originalDataGrid=root.querySelector('.studio-report-data-grid');
 // Empty layout wrappers are no longer required once their report panels move.
 if(originalGrid&&!originalGrid.querySelector('.studio-report-panel'))originalGrid.remove();
 if(originalDataGrid&&!originalDataGrid.querySelector('.studio-report-panel'))originalDataGrid.remove();
 if(strip)views.get('overview').prepend(strip);
 for(const panel of panels.filter(panel=>!panel.closest('[data-report-view]')))views.get('overview').append(panel);
 const activate=id=>{
  const chosen=views.has(id)?id:'overview';studioReportActive[type]=chosen;
  for(const [key,view] of views)view.hidden=key!==chosen;
  for(const button of nav.querySelectorAll('button')){const on=button.dataset.reportTarget===chosen;button.setAttribute('aria-current',on?'page':'false');button.setAttribute('aria-pressed',String(on));}
 };
 nav.querySelectorAll('button').forEach(button=>button.onclick=()=>activate(button.dataset.reportTarget));
 activate(studioReportActive[type]);
}


const studioDailyFinance={
 date:null,workspace:''
};
function studioJamaicaDate(value){
 const date=value instanceof Date?value:new Date(value);
 if(!Number.isFinite(date.getTime()))return '';
 const parts=Object.fromEntries(new Intl.DateTimeFormat('en-US',{timeZone:'America/Jamaica',year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(date).filter(x=>x.type!=='literal').map(x=>[x.type,x.value]));
 return parts.year+'-'+parts.month+'-'+parts.day;
}
const studioReceiptKinds=/^(payment|deposit|refund|reversal|POS Sale|POS Refund)$/i;
function studioDailyFinanceRows(jobs,date,workspace=''){
 return (jobs||[]).filter(job=>!workspace||job.workspaceId===workspace||(job.participatingWorkspaceIds||[]).includes(workspace)).flatMap(job=>
  (job.financialEvents||[]).filter(e=>studioJamaicaDate(e.createdAt||e.createdAtUtc)===date).map(e=>({
   ...e,jobId:job.id,client:job.clientName||'',title:job.title||'',workspaceId:job.workspaceId
  }))
 ).sort((a,b)=>new Date(a.createdAt||a.createdAtUtc)-new Date(b.createdAt||b.createdAtUtc));
}
function studioDailyFinanceDetail(rows,date){
 const receipts=rows.filter(e=>studioReceiptKinds.test(e.kind||''));
 const received=receipts.reduce((n,e)=>n+studioReportNumber(e.amount),0);
 const gross=receipts.filter(e=>studioReportNumber(e.amount)>0).reduce((n,e)=>n+studioReportNumber(e.amount),0);
 const reductions=receipts.filter(e=>studioReportNumber(e.amount)<0).reduce((n,e)=>n+Math.abs(studioReportNumber(e.amount)),0);
 const adjustments=rows.filter(e=>/^(charge|adjustment|discount)$/i.test(e.kind||''));
 const charges=adjustments.reduce((n,e)=>n+studioReportNumber(e.amount),0);
 const missingMethod=receipts.filter(e=>!e.paymentMethod);
 const missingCashier=receipts.filter(e=>!e.recordedBy);
 const types=new Map(),methods=new Map(),cashiers=new Map();
 for(const e of rows){const kind=e.kind||'Unclassified';const bucket=types.get(kind)||{label:kind,count:0,value:0};bucket.count++;bucket.value+=studioReportNumber(e.amount);types.set(kind,bucket);}
 for(const e of receipts){const method=e.paymentMethod||'Not recorded',cashier=e.recordedBy||'Not recorded',amount=studioReportNumber(e.amount);
  methods.set(method,(methods.get(method)||0)+amount);
  const staff=cashiers.get(cashier)||{label:cashier,count:0,value:0};staff.count++;staff.value+=amount;cashiers.set(cashier,staff);
 }
 const hourly=new Map();
 for(const e of receipts){const time=new Intl.DateTimeFormat('en-US',{timeZone:'America/Jamaica',hour:'2-digit',hourCycle:'h23'}).format(new Date(e.createdAt||e.createdAtUtc));hourly.set(time,(hourly.get(time)||0)+studioReportNumber(e.amount));}
 const summary=studioReportStrip([
  ['Business date',date],['Transactions',rows.length],['Receipt records',receipts.length],
  ['Gross positive receipts',money(gross)],['Refunds / reversals',money(reductions)],
  ['Net receipts',money(received)],['Charges / adjustments',money(charges)]
 ]);
 const details=studioReportTable(['Time (Jamaica)','Job / client','Type','Method','Cashier','Amount'],
  rows.map(e=>`<tr><td>${esc(new Date(e.createdAt||e.createdAtUtc).toLocaleTimeString('en-JM',{timeZone:'America/Jamaica',hour:'2-digit',minute:'2-digit'}))}</td>
  <td><button type="button" data-open-job="${esc(e.jobId)}">${esc(e.title)}</button><small class="studio-report-secondary">${esc(e.client)}</small></td>
  <td>${esc(e.kind)}</td><td>${esc(e.paymentMethod||'—')}</td><td>${esc(e.recordedBy||'—')}</td><td>${esc(money(e.amount))}</td></tr>`));
 const exceptions=studioReportTable(['Review item','Count'],[
  `<tr><td>Receipts without payment method</td><td>${missingMethod.length}</td></tr>`,
  `<tr><td>Receipts without recording staff</td><td>${missingCashier.length}</td></tr>`,
  `<tr><td>Negative receipt records</td><td>${receipts.filter(e=>studioReportNumber(e.amount)<0).length}</td></tr>`
 ]);
 return summary+'<div class="studio-report-chart-grid">'+
 studioReportPanel('Daily receipts by hour',studioReportBars([...hourly].sort(([a],[b])=>a.localeCompare(b)).map(([label,value])=>({label:label+':00',value})),{currency:true,limit:24}),'Jamaica local time')+
 studioReportPanel('Payment methods',studioReportBars([...methods].map(([label,value])=>({label,value})).sort((a,b)=>b.value-a.value),{currency:true}),'Net signed amounts')+
 studioReportPanel('Transaction types',studioReportBars([...types.values()].map(x=>({label:x.label,value:x.count}))),'Record counts')+
 studioReportPanel('Receipts by cashier',studioReportBars([...cashiers.values()].map(x=>({label:x.label,value:x.value})).sort((a,b)=>b.value-a.value),{currency:true}),'Net signed amounts')+
 '</div>'+studioReportPanel('Daily reconciliation checks',exceptions,'Flags for Finance review, not a verified cash close')+
 studioReportPanel('Daily transaction register',details,'Full selected-day register; export available');
}
function studioMountDailyFinance(jobs,workspaces){
 const host=genericView.querySelector('[data-daily-finance]');
 if(!host)return;
 const today=studioJamaicaDate(new Date());
 if(!studioDailyFinance.date)studioDailyFinance.date=today;
 const validDate=/^\d{4}-\d{2}-\d{2}$/.test(studioDailyFinance.date)?studioDailyFinance.date:today;
 host.innerHTML=`<div class="studio-daily-finance-tools">
 <label>Business date (Jamaica)<input data-daily-date type="date" max="${today}" value="${esc(validDate)}"></label>
 <label>Workspace<select data-daily-workspace><option value="">All workspaces</option>${(workspaces||[]).map(w=>`<option value="${esc(w.id)}">${esc(w.name)}</option>`).join('')}</select></label>
 <button type="button" data-daily-export>Export daily CSV</button>
 <span data-daily-status role="status"></span></div><div data-daily-result></div>
 <p class="studio-report-disclaimer">Based on recorded job financial events and their timestamps. This is an operational daily register, not a certified day-end cash or bank reconciliation. Late entries retain their recorded timestamps. Amounts use the application's existing JMD reporting convention.</p>`;
 const date=host.querySelector('[data-daily-date]'),workspace=host.querySelector('[data-daily-workspace]'),result=host.querySelector('[data-daily-result]'),status=host.querySelector('[data-daily-status]');
 workspace.value=(workspaces||[]).some(w=>w.id===studioDailyFinance.workspace)?studioDailyFinance.workspace:'';
 const render=()=>{
  if(!date.value||date.value>today){status.textContent='Choose a valid business date up to today.';result.replaceChildren();return [];}
  studioDailyFinance.date=date.value;studioDailyFinance.workspace=workspace.value;
  const rows=studioDailyFinanceRows(jobs,date.value,workspace.value);
  result.innerHTML=studioDailyFinanceDetail(rows,date.value);
  result.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));
  status.textContent=rows.length+' recorded events';
  return rows;
 };
 date.onchange=render;workspace.onchange=render;
 host.querySelector('[data-daily-export]').onclick=()=>{
  const rows=render();
  if(!date.value||date.value>today)return;
  const quote=value=>'"'+String(value??'').replace(/"/g,'""')+'"';
  const records=[['Business date (Jamaica)','Timestamp UTC','Job','Client','Type','Amount (JMD)','Payment method','Recorded by','Note'],
   ...rows.map(e=>[date.value,e.createdAt||e.createdAtUtc,e.title,e.client,e.kind,e.amount,e.paymentMethod,e.recordedBy,e.note])];
  const csv=records.map(row=>row.map(quote).join(',')).join('\r\n');
  const url=URL.createObjectURL(new Blob([csv],{type:'text/csv;charset=utf-8'}));
  const link=document.createElement('a');link.href=url;link.download='studio-daily-finance-'+date.value+'.csv';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
 };
 render();
}

async function showAccounts(){
    const current=StudioPage.begin('finance');
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const data=await studioReadOperations();data.workspaces=await json('/api/workspaces');
    const receivables=window.studioWorkflowCapabilities?.finance?await json('/api/finance/receivables'):[];
    if(!current())return;
    const net=data.receipts.reduce((n,e)=>n+Number(e.amount),0),charge=data.transactions.filter(e=>e.kind==='Charge').reduce((n,e)=>n+Number(e.amount),0),outstanding=data.balances.reduce((n,x)=>n+x.balance,0);
    const methods=[...new Set(data.receipts.map(e=>e.kind))].map(kind=>({label:kind,value:data.receipts.filter(e=>e.kind===kind).length}));
    genericView.innerHTML=StudioPage.html({title:'Finance',tools:`${studioPeriod()}<button data-export>Export</button><button class="primary" onclick="createInvoiceFromAccounts()">New Invoice</button>`,summary:StudioPage.metrics([['Net receipts',money(net)],['Outstanding',money(outstanding)],['Invoices',data.invoices.length]]),content:`
    <div class="studio-report-sheet">
      ${studioReportStrip([['Net receipts',money(net)],['Charges',money(charge)],['Outstanding',money(outstanding)],['Overdue invoices',data.invoices.filter(i=>i.status==='Overdue').length],['Invoices',data.invoices.length]])}
      <div class="studio-report-chart-grid">
        ${studioReportPanel('Net receipts over time',studioRevenueChart(data.receipts),'Signed receipts / refunds')}
        ${studioReportPanel('Payment methods',studioReportMethods(data.receipts),'Net amount')}
        ${studioReportPanel('Transaction mix',studioReportBars(methods),'Number of records')}
        ${studioReportPanel('Invoice status',studioReportInvoiceStatuses(data.invoices),'Invoice count')}
        ${studioReportPanel('Outstanding by job',studioReportBars(data.balances.filter(x=>x.balance>0).sort((a,b)=>b.balance-a.balance).map(x=>({label:x.job.title||x.job.id,value:x.balance})),{currency:true,limit:10}),'Current recorded balance')}
      </div>
      ${window.studioWorkflowCapabilities?.finance?studioReportPanel('Receivables and contractual credit',studioReportTable(['Job / client','Stage','Release','Balance'],receivables.map(x=>`<tr><td><button type="button" data-open-job="${esc(x.jobId)}">${esc(x.title)} · ${esc(x.client)}</button></td><td>${esc(x.stage)}</td><td>${x.decision.authorized?'Authorized':'Held'}</td><td>${esc(money(x.decision.balance))}</td></tr>`))):''}
      ${studioReportPanel('Daily Finance Report','<div data-daily-finance></div>','Date-based operational register')}
      ${studioReportPanel('Transactions — search, filter and export','<div data-finance-filter></div><div data-finance-results></div>','Up to 100 displayed; export includes all matches')}
      ${studioReportPanel('Invoices',studioReportTable(['Invoice','Client','Status','Amount','Actions'],data.invoices.map(i=>`<tr><td><button data-open-job="${esc(i.jobId)}">${esc(i.invoiceNumber)}</button></td><td>${esc(i.clientName)}</td><td>${esc(i.status)}</td><td>${esc(money(i.totalAmount))}</td><td>${i.status==='Draft'?`<button onclick="setInvoiceStatus('${i.id}','Issued')">Issue</button>`:''} <button onclick="issueClientPortal('${i.jobId}')">Client Portal</button></td></tr>`)))}
    </div>`});
    StudioPage.bind(genericView,current);
    studioOrganizeReports('finance');
    studioMountDailyFinance(data.jobs,data.workspaces);
    studioBindOperations(data,showAccounts);
    const controls=StudioPage.query(genericView,'[data-finance-filter]'),results=StudioPage.query(genericView,'[data-finance-results]');
    controls.innerHTML=`<label>Find transaction<input data-finance-search type="search" placeholder="Client, job, reference or cashier"></label><label>From<input type="date" data-finance-from value="${data.start.toISOString().slice(0,10)}"></label><label>Through<input type="date" data-finance-to value="${data.now.toISOString().slice(0,10)}"></label><label>Workspace<select data-finance-workspace><option value="">All workspaces</option>${data.workspaces.map(w=>`<option value="${w.id}">${esc(w.name)}</option>`).join('')}</select></label><label>Type<select data-finance-kind><option value="">All types</option>${[...new Set(data.transactions.map(e=>e.kind))].map(k=>`<option>${esc(k)}</option>`).join('')}</select></label><label>Method<select data-finance-method><option value="">All methods</option>${[...new Set(data.receipts.map(e=>e.paymentMethod||'Unrecorded'))].map(k=>`<option>${esc(k)}</option>`).join('')}</select></label><button data-export-filter>Export matching</button><p data-reconcile></p>`;
    let filtered=[];function render(){const term=controls.querySelector('input').value.toLowerCase(),kind=controls.querySelector('[data-finance-kind]').value,method=controls.querySelector('[data-finance-method]').value,workspace=controls.querySelector('[data-finance-workspace]').value,from=controls.querySelector('[data-finance-from]').value,to=controls.querySelector('[data-finance-to]').value;filtered=data.transactions.filter(e=>(!term||[e.client,e.title,e.note,e.recordedBy].join(' ').toLowerCase().includes(term))&&(!kind||e.kind===kind)&&(!method||(e.paymentMethod||'Unrecorded')===method)&&(!workspace||data.jobs.some(j=>j.id===e.jobId&&(j.workspaceId===workspace||(j.participatingWorkspaceIds||[]).includes(workspace))))&&(!from||new Date(e.createdAt||e.createdAtUtc)>=new Date(from+'T00:00:00'))&&(!to||new Date(e.createdAt||e.createdAtUtc)<=new Date(to+'T23:59:59.999')));results.innerHTML=studioTransactionsTable(filtered)+`<p>${Math.min(filtered.length,100)} of ${filtered.length} transactions shown. Export includes all matches.</p>`;results.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));const receipts=filtered.filter(e=>/^(payment|deposit|refund|reversal|POS Sale|POS Refund)$/i.test(e.kind));const sum=receipts.reduce((n,e)=>n+Number(e.amount),0);StudioPage.setCount(filtered.length+' transactions');controls.querySelector('[data-reconcile]').textContent=`${receipts.length} receipt/refund records · net ${money(sum)} · ${receipts.filter(e=>!e.paymentMethod).length} missing payment methods`;}controls.querySelectorAll('input').forEach(x=>x.oninput=render);controls.querySelectorAll('select').forEach(x=>x.onchange=render);controls.querySelector('[data-export-filter]').onclick=()=>studioExportTransactions(filtered);render();
}
async function showManagement(){
    const current=StudioPage.begin('management');
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const data=await studioReadOperations();const {jobs,tasks,sessions,photographers,now}=data;
    const snapshots=await json('/api/production/media-summary');
    if(!current())return;
    const photos=snapshots.reduce((n,s)=>n+Number(s.assetCount),0),overdue=tasks.filter(t=>!['Completed','Cancelled'].includes(t.status)&&t.dueAtUtc&&new Date(t.dueAtUtc)<now),blocked=tasks.filter(t=>t.status==='Blocked');
    const today=sessions.filter(s=>new Date(s.startsAtUtc).toLocaleDateString()===now.toLocaleDateString()&&s.status!=='Cancelled');
    if(!current())return;
    const net=data.receipts.reduce((n,e)=>n+Number(e.amount),0);
    const pipeline=pipelineStages.map(([label,statuses])=>({label,value:jobs.filter(j=>statuses.includes(j.status)).length})).filter(x=>x.value);
    const clientTotals=[...new Set(jobs.map(j=>j.clientId||j.clientName))].map(id=>{const related=jobs.filter(j=>(j.clientId||j.clientName)===id);return {job:related[0],amount:data.receipts.filter(e=>related.some(j=>j.id===e.jobId)).reduce((n,e)=>n+Number(e.amount),0)};}).sort((a,b)=>b.amount-a.amount);
    const upcoming=sessions.filter(s=>new Date(s.startsAtUtc)>=now&&s.status!=='Cancelled').sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
    genericView.innerHTML=StudioPage.html({title:'Management Dashboard',tools:`${studioPeriod()}<button data-export>Export</button>`,summary:StudioPage.metrics([['Jobs',jobs.length],['Sessions today',today.length],['Net receipts',money(net)]]),content:`
    <div class="studio-report-sheet">
      ${studioReportStrip([['Jobs',jobs.length],['Net receipts',money(net)],['Sessions today',today.length],['Upcoming sessions',upcoming.length],['Photos catalogued',photos],['Overdue tasks',overdue.length],['Blocked tasks',blocked.length]])}
      <div class="studio-report-chart-grid">
        ${studioReportPanel('Receipt trend',studioRevenueChart(data.receipts),'Selected period')}
        ${studioReportPanel('Jobs by stage',studioReportJobStages(jobs),'All current jobs')}
        ${studioReportPanel('Upcoming sessions by day',studioReportSessionDays(sessions,now),'Next 14 days')}
        ${studioReportPanel('Top clients by receipts',studioReportBars(clientTotals.slice(0,10).map(x=>({label:x.job.clientName||x.job.title,value:x.amount})),{currency:true,limit:10}),'Selected period')}
        ${studioReportPanel('Task status',studioReportBars([...new Set(tasks.map(t=>t.status||'Unknown'))].map(label=>({label,value:tasks.filter(t=>(t.status||'Unknown')===label).length}))),'Current tasks')}
        ${studioReportPanel('Invoice status',studioReportInvoiceStatuses(data.invoices),'Current invoices')}
      </div>
      <div class="studio-report-data-grid">
        ${studioReportPanel('Photographer workload',studioReportWorkload(photographers,tasks,sessions),'Scheduled sessions in loaded window')}
        ${studioReportPanel('Pipeline summary',studioReportTable(['Stage','Jobs'],pipeline.map(x=>`<tr><td><button type="button" data-stage="${esc(x.label)}">${esc(x.label)}</button></td><td>${x.value}</td></tr>`)))}
      </div>
      ${studioReportPanel('Upcoming sessions',studioReportTable(['Starts','Session','Location','Status'],upcoming.slice(0,30).map(x=>`<tr><td>${esc(new Date(x.startsAtUtc).toLocaleString())}</td><td><button data-open-job="${esc(x.jobId)}">${esc(x.title)}</button></td><td>${esc(x.location||'Not assigned')}</td><td>${esc(x.status)}</td></tr>`)),'Next 30 scheduled sessions')}
      ${studioReportPanel('Exceptions requiring attention',studioReportTable(['Job task','Status','Responsible','Due / reason'],[...overdue,...blocked].filter((t,i,a)=>a.findIndex(x=>x.id===t.id)===i).slice(0,40).map(t=>`<tr><td><button data-open-job="${esc(t.jobId)}">${esc(t.title)}</button></td><td>${esc(t.status)}</td><td>${esc(t.assignedTo||'Unassigned')}</td><td>${esc(t.blockedReason||t.dueAtUtc&&new Date(t.dueAtUtc).toLocaleDateString()||'Not recorded')}</td></tr>`)),'Overdue and blocked work')}
    </div>`});
    StudioPage.bind(genericView,current);
    studioOrganizeReports('management');
    studioBindOperations(data,showManagement);
    StudioPage.queryAll(genericView,'[data-stage]').forEach(b=>b.onclick=()=>{pipelineSelectedStage=b.dataset.stage;studioNavigate('pipeline');});
}

function renderManagementGantt(jobs, scheduleEntries, activities) {
    const activeJobs=(jobs||[]).filter(job=>job.status!=='Completed');
    if(!activeJobs.length)return '<p class="muted">No active jobs.</p>';

    const DAY=86400000;
    const today=new Date(); today.setHours(0,0,0,0);
    const dated=[];
    for(const job of activeJobs){
        const schedule=(scheduleEntries||[]).filter(item=>item.jobId===job.id);
        const tasks=(activities||[]).filter(item=>item.jobId===job.id);
        const candidates=[];
        if(job.eventDate)candidates.push(new Date(job.eventDate+'T12:00:00'));
        schedule.forEach(item=>{candidates.push(new Date(item.startsAtUtc));candidates.push(new Date(item.endsAtUtc));});
        tasks.forEach(item=>{if(item.scheduledAtUtc)candidates.push(new Date(item.scheduledAtUtc));if(item.dueAtUtc)candidates.push(new Date(item.dueAtUtc));});
        candidates.push(new Date(job.createdAt));
        const valid=candidates.filter(date=>Number.isFinite(+date));
        const start=new Date(Math.min(...valid.map(date=>+date)));
        const end=new Date(Math.max(...valid.map(date=>+date)));
        if(end<start)end.setTime(start.getTime());
        dated.push({job,start,end,tasks});
    }

    const earliest=new Date(Math.min(+today,...dated.map(x=>+x.start)));
    const latestLimit=new Date(today.getTime()+90*DAY);
    const latest=new Date(Math.max(+latestLimit,...dated.map(x=>+x.end)));
    earliest.setHours(0,0,0,0); latest.setHours(0,0,0,0);
    const totalDays=Math.max(1,Math.round((latest-earliest)/DAY)+1);
    const weekCount=Math.ceil(totalDays/7);
    const width=Math.max(840,weekCount*112);
    const weeks=Array.from({length:weekCount},(_,index)=>{
        const date=new Date(earliest.getTime()+index*7*DAY);
        return date.toLocaleDateString(undefined,{month:'short',day:'numeric'});
    });
    const todayPct=Math.max(0,Math.min(100,((today-earliest)/DAY)/totalDays*100));

    const rows=dated.sort((a,b)=>a.start-b.start).map(({job,start,end,tasks})=>{
        const left=Math.max(0,((start-earliest)/DAY)/totalDays*100);
        const span=Math.max(1,((end-start)/DAY+1)/totalDays*100);
        const production=tasks.filter(item=>item.status!=='Cancelled');
        const completed=production.filter(item=>item.status==='Completed').length;
        const progress=production.length?Math.round(completed/production.length*100):0;
        return `<div class="management-gantt-row">
            <button class="management-gantt-job" type="button" onclick="openWorkspaceJob('${job.id}')"><strong>${esc(job.title)}</strong><small>${esc(job.clientName||'Client not linked')} · ${esc(job.status)}</small></button>
            <div class="management-gantt-track" style="--gantt-width:${width}px">
                <div class="management-gantt-today" style="left:${todayPct}%"></div>
                <button class="management-gantt-bar" type="button" onclick="openWorkspaceJob('${job.id}')" style="left:${left}%;width:${span}%">
                    <span style="width:${progress}%"></span><b>${progress}%</b>
                </button>
            </div>
        </div>`;
    }).join('');

    return `<div class="management-gantt-shell">
        <div class="management-gantt-scroll">
            <div class="management-gantt-grid" style="--gantt-width:${width}px">
                <div class="management-gantt-head">
                    <div class="management-gantt-job-head">Job</div>
                    <div class="management-gantt-axis">${weeks.map(label=>`<span>${esc(label)}</span>`).join('')}</div>
                </div>
                ${rows}
            </div>
        </div>
    </div>`;
}

function renderEscalations(values) {
    if (!values.length) return '<p class="muted">No open escalations.</p>';
    return values.map(value => `<div class="financial-row"><div><b>L${value.level} · ${esc(value.subject)}</b><small>${esc(value.detail)} · opened ${new Date(value.openedAtUtc).toLocaleString()}</small></div><button class="action" onclick="ackEscalation('${value.id}')">Acknowledge</button></div>`).join('');
}

function renderBuckets(values, amount) {
    if (!values.length) return '<p class="muted">No data.</p>';
    const max = Math.max(...values.map(x => amount ? Number(x.amount) : Number(x.count)), 1);
    return `<div class="report-bars">${values.map(value => { const numeric = amount ? Number(value.amount) : Number(value.count); return `<div class="report-bar-row"><span>${esc(value.label)}</span><div class="report-track"><div class="report-fill" style="width:${Math.max(2,numeric/max*100)}%"></div></div><strong>${amount ? money(numeric) : numeric}</strong></div>`; }).join('')}</div>`;
}

async function scanEscalations(){ await json('/api/escalations/scan',{method:'POST'}); await showManagement(); }
async function ackEscalation(id){ await json(`/api/escalations/${id}/acknowledge`,{method:'POST'}); await showManagement(); }
async function setInvoiceStatus(id,status){ await json(`/api/accounts/invoices/${id}/status`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({status})}); await showAccounts(); }

async function createInvoiceFromAccounts() {
    const jobs = await json('/api/jobs');
    if (!jobs.length) return alert('There are no jobs to bill.');
    const choices = jobs.map((x,i)=>`${i+1}. ${x.title} — ${x.clientName}`).join('\n');
    const answer = prompt(`Choose job number:\n${choices}`, '1');
    const index = Number(answer)-1;
    if (!Number.isInteger(index)||index<0||index>=jobs.length) return;
    const description = prompt('Invoice item', 'Photography services'); if (!description) return;
    const amount = Number(prompt('Amount (JMD)', '0.00')); if (!Number.isFinite(amount)||amount<0) return alert('Enter a valid amount.');
    const tax = Number(prompt('Tax rate %', '0')) || 0;
    const due = new Date(); due.setDate(due.getDate()+14);
    await json(`/api/jobs/${jobs[index].id}/invoices`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({lines:[{description,quantity:1,unitPrice:amount}],taxRatePercent:tax,dueAtUtc:due.toISOString(),currency:'JMD'})});
    await showAccounts();
}

async function issueClientPortal(jobId) {
    const result = await json(`/api/jobs/${jobId}/client-portal`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expiresInDays:30})});
    await navigator.clipboard?.writeText(result.publicUrl).catch(()=>{});
    prompt('Client portal URL (copied when browser permission allows)', result.publicUrl);
}


function managementScheduleConflictCount(entries) {
    let count=0;
    const values=(entries||[]).filter(x=>String(x.status).toLowerCase()!=='cancelled');
    for(let i=0;i<values.length;i++)for(let j=i+1;j<values.length;j++){
        const a=values[i],b=values[j];
        if(new Date(a.startsAtUtc)<new Date(b.endsAtUtc)&&new Date(b.startsAtUtc)<new Date(a.endsAtUtc)
            &&(a.resourceIds||[]).some(id=>(b.resourceIds||[]).includes(id))) count++;
    }
    return count;
}

function renderManagementPipelineFlow(jobs, activities, workflowStages, scheduleEntries) {
    const active=(jobs||[]).filter(job=>String(job.status).toLowerCase()!=='completed');
    const ordered=(workflowStages||[]).filter(stage=>String(stage).toLowerCase()!=='completed');
    if(!ordered.length) return '<p class="muted">Workflow stages are not configured.</p>';
    const stageFor=status=>{
        const value=String(status||'').toLowerCase();
        const exact=ordered.find(stage=>stage.toLowerCase()===value);
        if(exact)return exact;
        if(['new','confirmed','inquiry','needs scheduling'].includes(value))return ordered.find(stage=>stage.toLowerCase()==='needs scheduling')||ordered[0];
        return ordered.find(stage=>stage.toLowerCase()==='production'&&value.includes('production'))||null;
    };
    const now=Date.now(), scheduledJobs=new Set((scheduleEntries||[]).filter(x=>x.jobId).map(x=>x.jobId));
    return '<div class="management-flow">'+ordered.map((stage,index)=>{
        const stageJobs=active.filter(job=>stageFor(job.status)===stage);
        const stageIds=new Set(stageJobs.map(x=>x.id));
        const overdue=(activities||[]).filter(a=>stageIds.has(a.jobId)&&!['Completed','Cancelled'].includes(a.status)&&a.dueAtUtc&&new Date(a.dueAtUtc).getTime()<now);
        const stuck=new Set(overdue.map(x=>x.jobId)).size;
        const unassigned=stageJobs.filter(job=>!job.assignedTo).length;
        const missingDate=stageJobs.filter(job=>!job.eventDate&&!scheduledJobs.has(job.id)).length;
        return `<button class="management-flow-stage" type="button" onclick="studioNavigate('jobs')"><span>${index+1}</span><strong>${esc(stage)}</strong><b>${stageJobs.length} jobs</b><small>${stuck} stuck · ${overdue.length} overdue tasks · ${unassigned} unassigned · ${missingDate} missing dates</small></button>`;
    }).join('<span class="management-flow-arrow">›</span>')+'</div>';
}

function renderPhotographerWorkload(jobs, entries, activities, capacity) {
    if(!(capacity||[]).length) return '<p class="muted">Photographer capacity is unavailable for this account or no active photographers are configured.</p>';
    const now=new Date(), weekEnd=new Date(now.getTime()+7*86400000);
    const rows=capacity.map(person=>{
        const scheduled=(entries||[]).filter(e=>(e.resourceIds||[]).includes(person.scheduleResourceId)&&new Date(e.endsAtUtc)>=now);
        const week=scheduled.filter(e=>new Date(e.startsAtUtc)<=weekEnd);
        const linked=new Set(scheduled.map(e=>e.jobId).filter(Boolean));
        const activeJobs=(jobs||[]).filter(j=>linked.has(j.id)&&String(j.status).toLowerCase()!=='completed');
        const overdue=(activities||[]).filter(a=>!['Completed','Cancelled'].includes(a.status)&&a.dueAtUtc&&new Date(a.dueAtUtc)<now&&String(a.assignedTo||'').toLowerCase()===String(person.userName||'').toLowerCase()).length;
        const conflicts=managementScheduleConflictCount(scheduled);
        return {person,active:activeJobs.length,week:week.length,overdue,conflicts};
    }).sort((a,b)=>b.person.utilizationPercent-a.person.utilizationPercent);
    window.managementWorkloadContext={jobs,entries};
    return `<div class="management-workload-table"><div class="management-workload-head"><span>Photographer</span><span>Active jobs</span><span>Next 7 days</span><span>Committed</span><span>Available</span><span>Utilization</span><span>Overdue</span><span>Conflicts</span></div>${rows.map(x=>`<button type="button" class="management-workload-row" onclick="showPhotographerWorkload('${x.person.scheduleResourceId}','${esc(x.person.displayName).replace(/'/g,"&#39;")}')"><strong>${esc(x.person.displayName)}</strong><span>${x.active}</span><span>${x.week}</span><span>${(x.person.scheduledMinutes/60).toFixed(1)} h</span><span>${(x.person.remainingMinutes/60).toFixed(1)} h</span><span class="${x.person.state==='OverCapacity'?'risk':''}">${x.person.utilizationPercent.toFixed(1)}% · ${esc(x.person.state)}</span><span class="${x.overdue?'risk':''}">${x.overdue}</span><span class="${x.conflicts?'risk':''}">${x.conflicts}</span></button>`).join('')}</div><p class="muted">Capacity and utilization come from configured daily capacity, actual schedule duration and recorded Unavailable/Leave time.</p>`;
}

function showPhotographerWorkload(resourceId, displayName) {
    const context=window.managementWorkloadContext||{jobs:[],entries:[]};
    const now=new Date();
    const work=context.entries.filter(e=>(e.resourceIds||[]).includes(resourceId)&&new Date(e.endsAtUtc)>=now).sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
    const dialog=studioDialog(displayName+' — scheduled work',`<button type="button" class="primary" data-management-photographer-schedule>View Schedule</button><div class="management-person-work">${work.map(entry=>{const job=context.jobs.find(j=>j.id===entry.jobId);return `<article class="front-desk-row"><div><strong>${esc(entry.title)}</strong><small>${esc(new Date(entry.startsAtUtc).toLocaleString())} – ${esc(new Date(entry.endsAtUtc).toLocaleString())} · ${esc(entry.location||'Location not set')}</small></div>${job?`<button type="button" class="action" data-management-job="${job.id}">Open job</button>`:''}</article>`;}).join('')||'<p class="muted">No upcoming scheduled work.</p>'}</div>`);
    dialog.querySelector('[data-management-photographer-schedule]')?.addEventListener('click',()=>{dialog.close();window.studioNavigate('schedule',{resourceId});});
    dialog.querySelectorAll('[data-management-job]').forEach(button=>button.onclick=()=>{dialog.close();openWorkspaceJob(button.dataset.managementJob);});
}




