const state={workspaceId:null,workspaceName:null,folder:'Inbox',conversationId:null,mode:'reply'};
const elements={workspaces:document.getElementById('workspaces'),folders:document.getElementById('folders'),conversations:document.getElementById('conversations'),listTitle:document.getElementById('listTitle'),workspaceName:document.getElementById('workspaceName'),searchBox:document.getElementById('searchBox'),threadEmpty:document.getElementById('threadEmpty'),threadView:document.getElementById('threadView'),threadSubject:document.getElementById('threadSubject'),threadMeta:document.getElementById('threadMeta'),messages:document.getElementById('messages'),assignee:document.getElementById('assignee'),statusSelect:document.getElementById('statusSelect'),composerForm:document.getElementById('composerForm'),composerBody:document.getElementById('composerBody'),composerHint:document.getElementById('composerHint'),sendButton:document.getElementById('sendButton'),replyTab:document.getElementById('replyTab'),noteTab:document.getElementById('noteTab')};

async function loadWorkspaces(){const response=await fetch('/api/workspaces');const items=await response.json();elements.workspaces.innerHTML='';items.forEach((item,index)=>{const button=document.createElement('button');button.textContent=item.name;button.dataset.id=item.id;button.onclick=()=>selectWorkspace(item,button);elements.workspaces.appendChild(button);if(index===0)selectWorkspace(item,button);});}

async function selectWorkspace(workspace,button){state.workspaceId=workspace.id;state.workspaceName=workspace.name;state.conversationId=null;[...elements.workspaces.children].forEach(item=>item.classList.remove('active'));button.classList.add('active');elements.workspaceName.textContent=workspace.name;hideThread();await loadConversations();}

async function loadConversations(){if(!state.workspaceId)return;const query=new URLSearchParams({folder:state.folder});const search=elements.searchBox.value.trim();if(search)query.set('search',search);const response=await fetch(`/api/workspaces/${state.workspaceId}/conversations?${query}`);const items=await response.json();renderConversations(items);}

function renderConversations(items){elements.conversations.innerHTML='';if(items.length===0){elements.conversations.className='conversation-items empty-state';elements.conversations.textContent='No conversations in this folder.';return;}elements.conversations.className='conversation-items';items.forEach(item=>{const button=document.createElement('button');button.className=`conversation-card${item.isUnread?' unread':''}${item.id===state.conversationId?' selected':''}`;button.innerHTML=`<div class="conversation-line"><strong>${escapeHtml(item.clientEmail)}</strong><span>${formatDate(item.lastActivityUtc)}</span></div><div class="conversation-line"><span class="subject">${escapeHtml(item.subject)}</span>${item.assignedUserId?`<span class="badge">${escapeHtml(item.assignedUserId)}</span>`:''}</div><p>${escapeHtml(item.preview)}</p><div class="conversation-line"><span class="status">${statusText(item.status)}</span>${item.bookingId?'<span class="badge">Booking</span>':''}</div>`;button.onclick=()=>openConversation(item.id);elements.conversations.appendChild(button);});}

async function openConversation(id){state.conversationId=id;await fetch(`/api/conversations/${id}/read`,{method:'POST'});const [detailsResponse,messagesResponse]=await Promise.all([fetch(`/api/conversations/${id}`),fetch(`/api/conversations/${id}/messages`)]);const details=await detailsResponse.json();const messages=await messagesResponse.json();elements.threadEmpty.hidden=true;elements.threadView.hidden=false;elements.threadSubject.textContent=details.subject;elements.threadMeta.textContent=`${details.clientEmail}${details.bookingId?' • Linked booking':''}`;elements.assignee.value=details.assignedUserId||'';elements.statusSelect.value=String(details.status===0?1:details.status);renderMessages(messages);await loadConversations();}

function renderMessages(items){elements.messages.innerHTML='';items.forEach(item=>{const article=document.createElement('article');const direction=item.direction===0?'inbound':item.direction===1?'outbound':'internal';article.className=`message ${direction}`;const label=item.direction===0?'Customer':item.direction===1?'Workspace reply':'Internal note';article.innerHTML=`<div class="message-head"><strong>${label}</strong><span>${formatDateTime(item.createdUtc)}</span></div><div class="message-addresses">${escapeHtml(item.fromAddress)} → ${escapeHtml(item.toAddress)}</div><div class="message-body">${escapeHtml(item.body).replace(/\n/g,'<br>')}</div>`;elements.messages.appendChild(article);});elements.messages.scrollTop=elements.messages.scrollHeight;}

function hideThread(){elements.threadEmpty.hidden=false;elements.threadView.hidden=true;}

async function submitComposer(event){event.preventDefault();if(!state.conversationId)return;const body=elements.composerBody.value.trim();if(!body)return;const isReply=state.mode==='reply';const endpoint=isReply?'reply':'notes';const payload=isReply?{body}:{body,author:'Aegis User'};const response=await fetch(`/api/conversations/${state.conversationId}/${endpoint}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});if(!response.ok){alert(await response.text());return;}elements.composerBody.value='';await openConversation(state.conversationId);}

function setComposerMode(mode){state.mode=mode;const reply=mode==='reply';elements.replyTab.classList.toggle('active',reply);elements.replyTab.classList.toggle('secondary',!reply);elements.noteTab.classList.toggle('active',!reply);elements.noteTab.classList.toggle('secondary',reply);elements.composerBody.placeholder=reply?'Write a reply...':'Write an internal note...';elements.composerHint.textContent=reply?'Reply will be sent from the Workspace address.':'Internal notes are visible only to staff.';elements.sendButton.textContent=reply?'Send Reply':'Add Note';}

async function assignConversation(){if(!state.conversationId)return;await fetch(`/api/conversations/${state.conversationId}/assign`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({userId:elements.assignee.value.trim()||null})});await openConversation(state.conversationId);}

async function updateStatus(){if(!state.conversationId)return;await fetch(`/api/conversations/${state.conversationId}/status`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({status:Number(elements.statusSelect.value)})});await loadConversations();await openConversation(state.conversationId);}

function selectFolder(button){state.folder=button.dataset.folder;state.conversationId=null;[...elements.folders.querySelectorAll('button')].forEach(item=>item.classList.remove('active'));button.classList.add('active');elements.listTitle.textContent=button.textContent;hideThread();loadConversations();}

function statusText(status){return ['New','Open','Waiting','Resolved','Closed'][status]||'Unknown';}
function formatDate(value){return new Intl.DateTimeFormat(undefined,{month:'short',day:'numeric'}).format(new Date(value));}
function formatDateTime(value){return new Intl.DateTimeFormat(undefined,{dateStyle:'medium',timeStyle:'short'}).format(new Date(value));}
function escapeHtml(value){const div=document.createElement('div');div.textContent=value??'';return div.innerHTML;}

elements.folders.querySelectorAll('button').forEach(button=>button.onclick=()=>selectFolder(button));
elements.searchBox.addEventListener('input',()=>loadConversations());
elements.composerForm.addEventListener('submit',submitComposer);
elements.replyTab.onclick=()=>setComposerMode('reply');
elements.noteTab.onclick=()=>setComposerMode('note');
document.getElementById('assignButton').onclick=assignConversation;
document.getElementById('statusButton').onclick=updateStatus;
setComposerMode('reply');
loadWorkspaces();
