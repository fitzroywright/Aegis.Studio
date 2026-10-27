const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const source=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/theme.js'),'utf8');
const settle=()=>new Promise(r=>setImmediate(r));
async function setup(t,stored='standard',systemDark=true){
 const dom=new JSDOM('<body><select id="themePicker"></select><select id="prefTheme"></select></body>',{url:'https://studio.test',runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window,calls=[];
 if(stored)w.localStorage.setItem('aegis.studio.theme',stored);
 w.matchMedia=()=>({matches:systemDark,addEventListener(){assert.fail('Standard must not switch with system preference');}});
 w.fetch=async(url,options)=>{calls.push({url,options});return {ok:true,json:async()=>({themeName:options?JSON.parse(options.body).themeName:stored})};};
 w.eval(source);w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();return {w,calls};
}
test('default and saved Standard use the original light palette with either browser preference',async t=>{
 for(const preference of [false,true])for(const stored of [null,'standard']){
  const {w}=await setup(t,stored,preference);assert.equal(w.document.body.dataset.theme,'studio-default');assert.equal(w.document.getElementById('themePicker').value,'standard');
 }
});
test('explicit Dark, Light and Comfort choices retain their own palettes',async t=>{
 for(const [choice,effective] of [['dark','fluent-dark'],['light','office-light'],['comfort','studio-comfort']]){
  const {w}=await setup(t,choice);assert.equal(w.document.body.dataset.theme,effective);
 }
});
test('changing from Dark to Standard applies and saves the original palette',async t=>{
 const {w,calls}=await setup(t,'dark'),picker=w.document.getElementById('themePicker');
 picker.value='standard';picker.dispatchEvent(new w.Event('change'));await settle();
 assert.equal(w.document.body.dataset.theme,'studio-default');assert.equal(w.localStorage.getItem('aegis.studio.theme'),'standard');
 assert.ok(calls.some(c=>c.options?.method==='PUT'&&JSON.parse(c.options.body).themeName==='standard'));
});
