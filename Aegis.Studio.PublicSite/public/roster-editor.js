(function(root){
 'use strict';
 const fields=['name','reference','collection','contactName','contactEmail','contactPhone','scheduledAtUtc','scheduleLocation','notes','pickupNotification'];
 const labels=['Name','Reference','Collection','Contact name','Email','Phone','Schedule ISO timestamp','Location','Notes','Pickup notification'];
 function csv(text){const rows=[];let row=[],value='',quote=false;for(let i=0;i<text.length;i++){const c=text[i];if(c==='"'){if(quote&&text[i+1]==='"'){value+='"';i++;}else quote=!quote;}else if(!quote&&[',','\n','\r','\t'].includes(c)){row.push(value);value='';if(c==='\n'||c==='\r'){if(row.some(Boolean))rows.push(row);row=[];if(c==='\r'&&text[i+1]==='\n')i++;}}else value+=c;}if(quote)throw Error('Unclosed quoted field.');row.push(value);if(row.some(Boolean))rows.push(row);return rows;}
 function validate(rows){const seen=new Set();return rows.flatMap((r,i)=>{const e=[];if(!r.name?.trim())e.push('Name required');if(!r.reference?.trim())e.push('Reference required');if(r.name?.toLowerCase()==='unclassified')e.push('Unclassified is reserved');if(seen.has(r.reference?.toLowerCase()))e.push('Duplicate reference');seen.add(r.reference?.toLowerCase());if(r.contactEmail&&!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(r.contactEmail))e.push('Invalid email');if(r.pickupNotification==='Email'&&!r.contactEmail)e.push('Email required for notifications');if(r.pickupNotification==='WhatsApp'&&!/^\+\d{8,15}$/.test(r.contactPhone||''))e.push('International phone required');return e.map(x=>`Row ${i+1}: ${x}`);});}
 function mapRows(cells,mapping){return cells.slice(1).filter(r=>r.some(Boolean)).map(c=>{const r={};for(const k of fields)if(mapping[k]!==undefined&&mapping[k]!=='')r[k]=String(c[Number(mapping[k])]??'');r.reference||=crypto.randomUUID();r.pickupNotification||='None';return r;});}
 function mergeRows(existing,incoming){const rows=existing.map(r=>({...r})),seen=new Set(),index=new Map();for(const row of rows){const key=row.reference.toLowerCase();if(!index.has(key))index.set(key,row);}for(const r of incoming){const key=r.reference.toLowerCase(),old=index.get(key);if(old&&!seen.has(key))Object.assign(old,r);else{const row={...r};rows.push(row);if(!index.has(key))index.set(key,row);}seen.add(key);}return rows;}
 function open(host,options={}){
  let rows=(options.rows||[]).map(r=>({...r})),revision=options.revision||0,dirty=false;
  const toolbar=document.createElement('div'),table=document.createElement('table'),wrap=document.createElement('div'),status=document.createElement('p');status.setAttribute('role','status');wrap.style.cssText='overflow:auto;max-height:50vh';wrap.append(table);
  const add=document.createElement('button');add.type='button';add.textContent='Add subject';
  const file=document.createElement('input');file.type='file';file.accept='.csv,.xlsx';file.setAttribute('aria-label','Import Session Plan file');
  const paste=document.createElement('textarea');paste.placeholder='Paste spreadsheet cells, including column headers';paste.setAttribute('aria-label','Paste Session Plan');
  const preview=document.createElement('button');preview.type='button';preview.textContent='Preview pasted rows';
  const save=document.createElement('button');save.type='button';save.textContent='Validate and save Session Plan';
  toolbar.append(add,file,paste,preview,save);host.replaceChildren(toolbar,wrap,status);
  const errors=()=>validate(rows);
  function render(){
   table.replaceChildren();const head=document.createElement('thead'),tr=document.createElement('tr');for(const label of [...labels,'Preview']){const th=document.createElement('th');th.textContent=label;th.style.cssText='position:sticky;top:0;background:var(--panel,#fff)';tr.append(th);}head.append(tr);table.append(head);const body=document.createElement('tbody');
   rows.forEach((r,i)=>{const tr=document.createElement('tr');fields.forEach(k=>{const td=document.createElement('td'),input=document.createElement(k==='pickupNotification'?'select':'input');if(k==='pickupNotification')for(const v of ['None','Email','WhatsApp']){const o=document.createElement('option');o.textContent=v;o.value=v;input.append(o);}input.disabled=Boolean(options.readOnly);input.value=r[k]|| (k==='pickupNotification'?'None':'');input.setAttribute('aria-label',`${labels[fields.indexOf(k)]}, row ${i+1}`);input.onchange=()=>{r[k]=input.value;dirty=true;status.textContent=errors().join(' · ')||'Ready to save';};td.append(input);tr.append(td);});const td=document.createElement('td');td.textContent=options.existingReferences?.includes(r.reference)?'Update':'Insert';const remove=document.createElement('button');remove.type='button';remove.textContent='Remove';remove.disabled=Boolean(options.readOnly);if(options.existingReferences?.includes(r.reference)){remove.disabled=true;remove.title='Existing subjects are preserved; edit their details here.';}remove.onclick=()=>{rows.splice(i,1);dirty=true;render();};td.append(remove);tr.append(td);body.append(tr);});table.append(body);status.textContent=errors().join(' · ')||`${rows.length} subjects. Preview changes before saving.`;
  }
  function mapping(cells){if(cells.length<2)throw Error('Supply a header and at least one row.');const dialog=document.createElement('dialog'),form=document.createElement('form');const selectors={};fields.forEach((k,i)=>{const label=document.createElement('label');label.textContent=labels[i];const s=document.createElement('select');s.append(new Option('Not supplied',''));cells[0].forEach((h,j)=>s.append(new Option(h,String(j))));const normalized=x=>String(x).replace(/[^a-z]/gi,'').toLowerCase();const match=cells[0].findIndex(h=>normalized(h)===normalized(k)||normalized(h)===normalized(labels[i])||k==='reference'&&normalized(h)==='subjectid');if(match>=0)s.value=String(match);selectors[k]=s;label.append(s);form.append(label);});const b=document.createElement('button');b.textContent='Add to preview';form.append(b);const cancel=document.createElement('button');cancel.type='button';cancel.textContent='Cancel';cancel.onclick=()=>dialog.close();form.append(cancel);dialog.append(form);document.body.append(dialog);dialog.onclose=()=>dialog.remove();form.onsubmit=e=>{e.preventDefault();const incoming=mapRows(cells,Object.fromEntries(fields.map(k=>[k,selectors[k].value])));const merged=mergeRows(rows,incoming);if(merged.length>5000){status.textContent='Maximum 5000 rows.';dialog.close();return;}rows=merged;dirty=true;dialog.close();render();};dialog.showModal();}
  add.onclick=()=>{rows.push({name:'',reference:crypto.randomUUID(),pickupNotification:'None'});dirty=true;render();};
  preview.onclick=()=>{try{mapping(csv(paste.value));}catch(e){status.textContent=e.message;}};
  file.onchange=async()=>{if(!file.files[0])return;try{if(options.readFile)mapping(await options.readFile(file.files[0]));else if(file.files[0].name.endsWith('.csv'))mapping(csv(await file.files[0].text()));else throw Error('XLSX upload is unavailable. Export as CSV.');}catch(e){status.textContent=e.message;}};
  save.onclick=async()=>{const e=errors();if(e.length){status.textContent=e.join(' · ');return;}save.disabled=true;status.textContent='Saving…';try{const result=await options.save?.({revision,rows});if(result?.revision!==undefined)revision=result.revision;dirty=false;status.textContent='Session Plan saved.';}catch(e){status.textContent=e.message;}finally{save.disabled=false;}};
  if(options.readOnly)for(const control of [add,file,paste,preview,save])control.disabled=true;
  render();return {getRows:()=>{const e=errors();if(e.length)throw Error(e.join(' · '));return rows.map(r=>({...r}));},isDirty:()=>dirty};
 }
 // Session Plan imports keep unmapped fields, match stable identities, and expose ambiguous matches.
 const sessionFields=[...fields,'scheduleDate','scheduleTime'];
 const sessionLabels=[...labels,'Session date','Session time'];
 const identity=value=>String(value||'').normalize('NFKC').trim().replace(/\s+/g,' ').toLocaleLowerCase();
 function plannedTime(value,time='',order='DMY'){
  value=String(value||'').trim();time=String(time||'').trim();
  if(!value){if(time)throw Error('Supply a date with the session time.');return '';}
  if(/^\d{4}-\d\d-\d\dT\d\d:\d\d(?::\d\d(?:\.\d+)?)?(?:Z|[+-]\d\d:\d\d)$/.test(value)){
   const calendar=new Date(value.slice(0,10)+'T00:00:00Z');if(!Number.isFinite(+calendar)||calendar.toISOString().slice(0,10)!==value.slice(0,10))throw Error('Invalid session date.');const d=new Date(value);if(!Number.isFinite(+d))throw Error('Invalid session date or time.');return d.toISOString();
  }
  let y,m,d,h=0,min=0,sec=0,clock=time;
  if(/^\d+(?:\.\d+)?$/.test(value)){
   const serial=Number(value);if(serial<1||serial>2958465)throw Error('Invalid Excel date.');
   const date=new Date(Date.UTC(1899,11,30)+Math.floor(serial)*86400000);y=date.getUTCFullYear();m=date.getUTCMonth()+1;d=date.getUTCDate();
   if(!clock&&serial%1)clock=String(serial%1);
  }else{
   const parts=value.split(/[T ]+/);if(!clock&&parts.length>1)clock=parts.slice(1).join(' ');
   const iso=/^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(parts[0]),local=/^(\d{1,2})[\/-](\d{1,2})[\/-](\d{4})$/.exec(parts[0]);
   if(iso)[y,m,d]=iso.slice(1).map(Number);
   else if(local){const [a,b,year]=local.slice(1).map(Number);[y,m,d]=order==='MDY'?[year,a,b]:[year,b,a];}
   else throw Error('Use YYYY-MM-DD or the selected day/month date format.');
  }
  if(!clock)throw Error('Supply a session time; a date alone is not a time slot.');
  if(/^0(?:\.\d+)?$/.test(clock)){let total=Math.round(Number(clock)*86400);h=Math.floor(total/3600);min=Math.floor(total%3600/60);sec=total%60;}
  else {const match=/^(\d{1,2}):(\d{2})(?::(\d{2}))?\s*(am|pm)?$/i.exec(clock);if(!match)throw Error('Use a time such as 09:30 or 2:30 PM.');h=Number(match[1]);min=Number(match[2]);sec=Number(match[3]||0);if(match[4]){if(h<1||h>12)throw Error('Invalid 12-hour time.');h=h%12+(match[4].toLowerCase()==='pm'?12:0);}}
  const result=new Date(y,m-1,d,h,min,sec);
  if(y<1900||h>23||min>59||sec>59||result.getFullYear()!==y||result.getMonth()!==m-1||result.getDate()!==d||result.getHours()!==h||result.getMinutes()!==min)throw Error('Invalid date or time in your local time zone.');
  return result.toISOString();
 }
 function planImport(cells,mapping,existing,choices={},dateOrder='DMY'){
  const errors=[],changes=[],used=new Set(),unnumbered=new Set(),updates=[];
  if(mapping.name===undefined||mapping.name==='')errors.push('Map the subject name column.');
  const key=r=>identity(r.name)+'|'+identity(r.collection||'General');
  const referenceIndex=new Map(),nameIndex=new Map();
  for(const row of existing){for(const [index,value] of [[referenceIndex,identity(row.reference)],[nameIndex,identity(row.name)]]){if(!index.has(value))index.set(value,[]);index.get(value).push(row);}}
  for(const [index,c] of cells.slice(1).filter(r=>r.some(v=>String(v).trim())).entries()){
   const row={};for(const field of sessionFields)if(mapping[field]!==undefined&&mapping[field]!=='')row[field]=String(c[Number(mapping[field])]??'').trim();
   const sourceReference=row.reference||'',byReference=sourceReference?referenceIndex.get(identity(sourceReference))||[]:[];
   const named=nameIndex.get(identity(row.name))||[],sameGroup=named.filter(x=>key(x)===key(row)),samePerson=sameGroup.length?sameGroup:named;let target=null,candidates=[],unresolved=false;
   if(byReference.length===1)target=byReference[0];
   else if(byReference.length>1){errors.push(`Row ${index+1}: existing reference is ambiguous.`);unresolved=true;}
   else if(samePerson.length){
    candidates=samePerson;const choice=choices[index];
    if(choice==='new')target=null;
    else if(choice)target=samePerson.find(x=>x.reference===choice)||null;
    else if(samePerson.length===1&&!sourceReference&&(!row.collection||sameGroup.length))target=samePerson[0];
    else unresolved=true;
   }
   if(unresolved)errors.push(`Row ${index+1}: choose the existing person or confirm a new person.`);
   if(!sourceReference){if(unnumbered.has(key(row)))errors.push(`Row ${index+1}: duplicate name and group without stable references. Supply distinct references to distinguish these people.`);unnumbered.add(key(row));}
   const reference=target?.reference||sourceReference||crypto.randomUUID();
   row.reference=reference;if(target?.subjectId)row.subjectId=target.subjectId;
   // An alternate reference must not silently replace an existing stable identity.
   if(target&&sourceReference&&identity(sourceReference)!==identity(target.reference))row.reference=target.reference;
   try{if(row.scheduledAtUtc||row.scheduleDate||row.scheduleTime)row.scheduledAtUtc=plannedTime(row.scheduledAtUtc||row.scheduleDate,row.scheduleTime,dateOrder);}catch(e){errors.push(`Row ${index+1}: ${e.message}`);}
   delete row.scheduleDate;delete row.scheduleTime;
   if(used.has(identity(row.reference)))errors.push(`Row ${index+1}: the same subject occurs more than once in this import.`);used.add(identity(row.reference));
   changes.push({index,kind:unresolved?'Review match':target?'Update':'New',row,candidates,unresolved,sourceReference});updates.push(row);
  }
  const rows=mergeRows(existing,updates);
  errors.push(...validate(rows));if(rows.length>5000)errors.push('Maximum 5000 subjects.');
  return {rows,changes,errors};
 }
 root.StudioRosterEditor={open,csv,fields,labels,validate,mapRows,mergeRows,sessionFields,sessionLabels,plannedTime,planImport};
})(window);

