// Server contracts for shared navigation, permissions and conversation regressions.
const now=new Date('2026-10-10T12:00:00Z'),iso=(h,d=0)=>new Date(new Date().setHours(h,0,0,0)+d*86400000).toISOString();
const permissions={accessWorkspace:true,view:true,editJob:true,changeStatus:true,manageSchedule:true,manageProduction:true,recordFinancialActivity:true,viewFinancials:true,viewConversation:true,viewAttachments:true,addAttachments:true,replyRequester:true,manageMembers:true,createJob:true,notifyMembers:true};
const names=['Miller Family - Fall Portraits','Carter Wedding','Campion Graduation 2026','Carter Executive Headshots','Peterson Newborn','Wilson Family + Dog'];
const workspaces=['Portraits and IDs','Weddings and Events','Schools and Corporate','Graduations','Commercial and Products','Sports and Groups'].map((name,i)=>({id:'w'+i,name,permissions,emailAddress:'studio@example.invalid',openCount:4}));
const jobs=names.map((title,i)=>({id:'j'+i,workspaceId:'w'+i,conversationId:'c'+i,title,clientName:['Miller Family','Emily & Daniel Carter','Campion College','Carter Corp','Peterson Family','Wilson Family'][i],clientId:'cl'+i,status:['Scheduled','Production','Photography','Editing','Scheduled','Scheduled'][i],eventDate:new Date().toLocaleDateString('en-CA'),createdAt:iso(10,-6),assignedTo:'demo.sam',jobLead:'demo.sam',financialEvents:[{id:'charge'+i,kind:'Charge',amount:45000,createdAt:iso(10,-5)},{id:'pay'+i,kind:'Deposit',amount:22500,createdAt:iso(10,-3)}]}));
const tasks=jobs.flatMap((j,i)=>[0,1,2].map(n=>({id:'t'+i+'-'+n,jobId:j.id,title:['Cull images','Retouch portraits','Proof selection'][n],activityType:['Cull','Retouch','Proof'][n],status:['NotStarted','InProgress','Blocked'][n],priority:n===2?'High':'Normal',assignedTo:'demo.sam',collectionName:'Selects',scheduledAtUtc:iso(11),dueAtUtc:iso(17,n-1),createdAtUtc:iso(9,-3),blockedReason:n===2?'Awaiting client selection':null})));
const sessions=jobs.map((j,i)=>({id:'s'+i,jobId:j.id,title:j.title,startsAtUtc:iso(9+i),endsAtUtc:iso(11+i),location:i===4?'Client Home':'Studio A',status:'Scheduled',resourceIds:['r1']}));
const photographers=[{userName:'demo.sam',displayName:'Samantha Lee',isActive:true,scheduleResourceId:'r1',state:'Available',utilizationPercent:40,scheduledMinutes:120,remainingMinutes:180}];
const media=j=>({gallery:{id:j,defaultLens:'Auto',default_lens:'Auto',defaultCollection:'All'},subjects:[{id:'sub'+j,name:'Sarah Miller',reference:'P001',productionStatus:'Ready',collectionName:'Selects'}],collections:['Selects','Proofs','Finals'],assets:Array.from({length:20},(_,i)=>({id:'a'+j+i,assetKey:'photo'+i,versionId:'v'+j+i,contentType:'image/jpeg',collection:'Selects',fileName:'Portrait '+i+'.jpg',productionStatus:['Needs Editing','Ready','Needs Review'][i%3],assignedTo:'demo.sam',subjects:['sub'+j],status:'Needs Editing',version:1,versions:[{id:'v'+j+i,versionNumber:1,contentType:'image/jpeg'}]})),unmatched:[]});
function api(u){const p=u.pathname;
 if(p==='/api/workspace-access')return workspaces.map(workspace=>({workspace,permissions:workspace.permissions}));
 if(p==='/api/auth/me')return {user:{userName:'demo.sam',displayName:'Samantha Lee',isAdministrator:true}};
 if(p==='/api/workspaces'||p==='/api/frontdesk/workspaces')return workspaces;
 if(p==='/api/jobs'||p==='/api/today/jobs')return jobs;
 if(p==='/api/today/tasks')return tasks;
 if(p==='/api/today/media-summary')return jobs.map(j=>({jobId:j.id,assetCount:20,thumbnailVersionId:'v'+j.id+'0'}));
 if(/^\/api\/jobs\/j\d$/.test(p))return jobs.find(j=>j.id===p.split('/').pop());
 if(p.endsWith('/access'))return {permissions};
 if(p.endsWith('/appearance'))return {color:'#b99750',icon:'folder'};
 if(p.endsWith('/media'))return media(p.split('/')[3]);
 if(p.endsWith('/subjects'))return media(p.split('/')[3]).subjects;
 if(p.includes('/activities'))return p.includes('/jobs/')?tasks.filter(t=>t.jobId===p.split('/')[3]):tasks;
 if(p==='/api/schedule/entries'||p==='/api/today/schedule')return sessions;
 if(p.includes('/shoots'))return [];
 if(p==='/api/photographers')return photographers;
 if(p==='/api/studio-employees')return photographers.map(x=>({userName:x.userName,displayName:x.displayName,isActive:true,isAssignable:true,roles:['Photographer']}));
 if(p==='/api/schedule/resources')return [{id:'r1',name:'Samantha Lee',resourceType:'Photographer',isActive:true},{id:'kit',name:'Canon EOS R5',resourceType:'Equipment',isActive:true,notes:'Available'}];
 if(p==='/api/accounts/invoices')return jobs.map((j,i)=>({id:'inv'+i,jobId:j.id,invoiceNumber:'2026-00'+i,clientName:j.clientName,totalAmount:45000,status:i?'Issued':'Overdue'}));
 if(p==='/api/security/global-role-me')return {frontDesk:true,permissions};
 if(p==='/api/frontdesk/clients')return jobs.map((j,i)=>({id:j.clientId,displayName:j.clientName,email:'client'+i+'@example.invalid',phone:'876-555-000'+i}));
 if(p==='/api/notifications')return jobs.map((j,i)=>({id:'n'+i,subject:j.title,eventType:'WorkspaceMessage',body:'Please confirm our session and delivery timing.',referenceType:'Job',referenceId:j.id,isRead:false,createdAtUtc:iso(10)}));
 if(p.includes('/sending-identities'))return {identities:[{type:'Workspace',isDefault:true},{type:'Self'}]};
 if(p.includes('/conversations/c'))return {id:'c0',channel:'Email',subject:'Client session',contactName:'Sarah Miller',messages:[{id:'m1',body:'Please confirm our session and package.',authorDisplayName:'Sarah Miller',visibility:'External',createdAt:iso(10,-1)}]};
 if(p.includes('/messages/threads/Job/'))return {id:'thread'};if(p.includes('/messages/threads/'))return [];
 if(p.includes('settings'))return {};
 return [];
}


module.exports={api,jobs,workspaces,permissions};

