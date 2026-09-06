async function api(url){const response=await fetch(url);if(!response.ok)throw new Error(await response.text());return response.json()}
function escapeHtml(value){return String(value??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function money(value,currency){return new Intl.NumberFormat('en-JM',{style:'currency',currency:currency||'JMD',maximumFractionDigits:0}).format(value||0)}
function directionLabel(direction){return ['Customer → Bryan Studios','Bryan Studios → Customer','Internal Bryan Studios'][direction]||'Message'}
function event(type,stage,title,detail,meta){return {type,stage,title,detail,meta}}
function messageEvent(message){const type=message.direction===2?'internal':'email';return event(type,directionLabel(message.direction),message.subject,message.body,`${message.fromAddress} → ${message.toAddress}`)}
function renderEvent(item){return `<div class="job-event ${item.type}"><div class="event-stage">${escapeHtml(item.stage)}</div><div><strong>${escapeHtml(item.title)}</strong><p>${escapeHtml(item.detail)}</p><small>${escapeHtml(item.meta||'')}</small></div></div>`}
async function load(){try{
  const demo=await api('/demo-job.json');
  jobReference.textContent=demo.jobReference;jobTitle.textContent=demo.title;
  const workspaces=await api('/api/workspaces');const weddings=workspaces.find(x=>x.slug==='weddings');if(!weddings)throw new Error('Weddings workspace not found.');
  const conversations=await api(`/api/workspaces/${weddings.id}/conversations?folder=Inbox&search=DEMO%20FLOW`);let conversation=conversations.find(x=>(x.subject||'').includes('DEMO FLOW'));
  if(!conversation){const open=await api(`/api/workspaces/${weddings.id}/conversations?folder=Open&search=DEMO%20FLOW`);conversation=open.find(x=>(x.subject||'').includes('DEMO FLOW'))}
  if(!conversation)throw new Error('Seeded demo conversation not found. Restart Studio once so the demo seeder can run.');
  const details=await api(`/api/conversations/${conversation.id}`);const messages=await api(`/api/conversations/${conversation.id}/messages`);
  let booking=null;if(details.bookingId){booking=await api(`/api/bookings/${details.bookingId}`)}
  jobFacts.innerHTML=`<div><span>Workspace</span><strong>${escapeHtml(weddings.name)}</strong></div><div><span>Customer</span><strong>${escapeHtml(demo.customerName)}</strong></div><div><span>Photographer</span><strong>${escapeHtml(demo.photographer)}</strong></div><div><span>Event date</span><strong>${new Date(demo.eventDate).toLocaleDateString('en-JM',{dateStyle:'medium'})}</strong></div><div><span>Booking</span><strong>${booking?escapeHtml(booking.reference):'Linked'}</strong></div>`;
  moneyStrip.innerHTML=`<div><span>Package</span><strong>${money(demo.total,demo.currency)}</strong></div><div class="paid"><span>Deposit received</span><strong>${money(demo.deposit,demo.currency)}</strong></div><div class="paid"><span>Final payment</span><strong>${money(demo.finalPayment,demo.currency)}</strong></div><div class="balance"><span>Balance</span><strong>${money(demo.balance,demo.currency)}</strong></div>`;
  stageStrip.innerHTML=demo.stages.map((s,i)=>`<div class="stage"><small>${String(i+1).padStart(2,'0')}</small><strong>${escapeHtml(s)}</strong></div>`).join('');
  jobContext.innerHTML=`<dl><div><dt>Job</dt><dd>${escapeHtml(demo.jobReference)} · ${escapeHtml(demo.title)}</dd></div><div><dt>Customer</dt><dd>${escapeHtml(demo.customerName)}<br>${escapeHtml(demo.customerEmail)}</dd></div><div><dt>Service</dt><dd>${escapeHtml(demo.packageName)}</dd></div><div><dt>Register</dt><dd>${escapeHtml(demo.register)}</dd></div><div><dt>Accounting batch</dt><dd>${escapeHtml(demo.accountingBatch)}<br>${escapeHtml(demo.accountingStatus)}</dd></div></dl>`;

  const galleries=await api('/api/galleries');const demoGalleries=galleries.filter(g=>(g.name||'').startsWith('DEMO - Morgan Wedding - Full Workflow'));
  const galleryOrder=['Photographer Uploads','Production Edits','Customer Proofs'];demoGalleries.sort((a,b)=>galleryOrder.findIndex(x=>a.name.endsWith(x))-galleryOrder.findIndex(x=>b.name.endsWith(x)));
  const galleryData=[];for(const gallery of demoGalleries){const media=await api(`/api/galleries/${gallery.id}/media`);const records=[];for(const item of media){records.push({item,activity:await api(`/api/media/${item.id}/activity`)})}galleryData.push({gallery,records})}

  const timeline=[];
  if(messages[0])timeline.push(messageEvent(messages[0]));
  if(messages[1])timeline.push(messageEvent(messages[1]));
  if(messages[2])timeline.push(messageEvent(messages[2]));
  timeline.push(event('internal','Job Created',`${demo.jobReference} created in Weddings Workspace`,`Front desk converted the confirmed inquiry into a Job and linked Danielle, the booking, the conversation and the photographer.`,`Workspace: ${weddings.name}`));
  timeline.push(event('money','Deposit Paid',`Deposit received — ${money(demo.deposit,demo.currency)}`,`Front desk collected the booking deposit against ${demo.jobReference}. The payment is now part of the Job and register session.`,demo.register));
  if(messages[3])timeline.push(messageEvent(messages[3]));
  if(messages[4])timeline.push(messageEvent(messages[4]));
  if(messages[5])timeline.push(messageEvent(messages[5]));
  const originals=galleryData.find(x=>x.gallery.name.endsWith('Photographer Uploads'))?.records||[];
  timeline.push(event('media','Images Uploaded',`${originals.length} photographer selections attached to Job`,`Ryan's original work products are visible inside the Job and can now be assigned to production.`,originals.map(x=>x.item.fileName).join(', ')));
  const production=galleryData.find(x=>x.gallery.name.endsWith('Production Edits'))?.records||[];
  timeline.push(event('media','Production',`${production.length} production masters created`,`Retouching, color correction and QA activity are traceable from the original assets to the edited versions.`,`Production team: Keisha / Andre / Michelle QA`));
  if(messages[6])timeline.push(messageEvent(messages[6]));
  const proofs=galleryData.find(x=>x.gallery.name.endsWith('Customer Proofs'))?.records||[];
  timeline.push(event('media','Proofing',`${proofs.length} customer proofs published`,`The proof assets remain linked to the same Job, including customer views, requested changes and approvals.`,`Customer portal / proof workflow`));
  if(messages[7])timeline.push(messageEvent(messages[7]));
  if(messages[8])timeline.push(messageEvent(messages[8]));
  timeline.push(event('media','Print',`Approved prints sent to fulfillment`,`2 × 8x10 couple portrait and 1 × 11x14 family group are queued from the approved proofs.`,`Production / print queue`));
  timeline.push(event('money','Final Payment',`Balance collected — ${money(demo.finalPayment,demo.currency)}`,`Front desk collected the remaining balance when the finished order was ready. Job balance is now ${money(demo.balance,demo.currency)}.`,demo.register));
  timeline.push(event('accounting','Accounting',`Batch ${demo.accountingBatch}`,`The Job's deposit and final payment are included in the daily accounting batch, ready for the Peachtree adapter and reconciliation.`,demo.accountingStatus));
  jobTimeline.innerHTML=timeline.map(renderEvent).join('');

  const sections=[];for(const group of galleryData){const stage=group.gallery.name.split(' - ').pop();const cards=group.records.map(({item,activity})=>`<article class="media-card"><img src="/api/media/${item.id}/preview" alt="${escapeHtml(item.fileName)}"><div class="media-card-body"><strong>${escapeHtml(item.fileName)}</strong><span>${escapeHtml(item.uploadedBy)} · Version ${item.version}</span><div class="media-activity">${activity.history.slice().reverse().map(h=>`<p><b>${escapeHtml(h.userId)}</b> · ${escapeHtml(h.action)}<br>${escapeHtml(h.detail)}</p>`).join('')}</div></div></article>`).join('');sections.push(`<section class="media-stage"><div class="media-stage-head"><strong>${escapeHtml(stage)}</strong><span>${group.records.length} assets</span></div><div class="media-grid">${cards}</div></section>`)}
  jobMedia.innerHTML=sections.join('')||'<p>No seeded work products found. Restart Studio so the demo seeder can run.</p>';
}catch(error){jobTimeline.innerHTML=`<p class="demo-error">${escapeHtml(error.message)}</p>`;jobMedia.innerHTML='<p class="demo-error">The Job dataset could not be loaded.</p>'}}
load();