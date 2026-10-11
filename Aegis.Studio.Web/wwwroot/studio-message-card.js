// Canonical message card. Page renderers call it directly and retain their data/actions.
(() => {
 'use strict';
 const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 function html({id,title='Messages & Announcements',body='',footer='',actions='',open=false,attributes=''}){
  const panelId=id+'-content';
  return `<section id="${escape(id)}" class="smc-card${open?' is-open':''}" ${attributes}><header class="smc-header"><button type="button" class="smc-toggle" aria-expanded="${open}" aria-controls="${escape(panelId)}"><span aria-hidden="true">✉</span><strong>${escape(title)}</strong><span class="smc-chevron" aria-hidden="true">⌄</span></button>${actions?`<div class="smc-actions">${actions}</div>`:''}</header><div id="${escape(panelId)}" class="smc-drawer" aria-hidden="${!open}" ${open?'':'inert'}><div class="smc-content"><div class="smc-feed">${body}</div>${footer?`<footer class="smc-footer">${footer}</footer>`:''}</div></div></section>`;
 }
 function create(options){const template=document.createElement('template');template.innerHTML=html(options);return template.content.firstElementChild;}
 function isOpen(card){return card?.querySelector('.smc-toggle')?.getAttribute('aria-expanded')==='true';}
 function setOpen(card,open,restoreFocus=false){
  if(!card)return;
  const toggle=card.querySelector('.smc-toggle'),drawer=card.querySelector('.smc-drawer');
  if(!toggle||!drawer)return;
  if(!open&&drawer.contains(document.activeElement))restoreFocus=true;
  card.classList.toggle('is-open',open);
  toggle.setAttribute('aria-expanded',String(open));
  drawer.setAttribute('aria-hidden',String(!open));drawer.toggleAttribute('inert',!open);
  if(restoreFocus)toggle.focus({preventScroll:true});
 }
 function bind(card){
  if(!card)return;
  // Rebinding after a page-owned partial render replaces only this card's handlers.
  card.querySelector('.smc-toggle').onclick=()=>setOpen(card,!isOpen(card));
  card.onkeydown=event=>{if(event.key==='Escape'&&isOpen(card)){event.preventDefault();event.stopPropagation();setOpen(card,false,true);}};
 }
 window.StudioMessageCard={html,create,bind,setOpen,isOpen};
})();
