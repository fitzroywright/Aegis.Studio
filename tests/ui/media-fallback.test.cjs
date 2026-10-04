const {test}=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs');const path=require('node:path');const {JSDOM}=require('jsdom');
test('a missing photo offers a real retry while its job remains available',()=>{
 const dom=new JSDOM('<div id="job"><img src="/api/jobs/job/versions/by-id/version/content"><button id="open">Open job</button></div>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/media-fallback.js'),'utf8'));
 const image=w.document.querySelector('img');image.dispatchEvent(new w.Event('error'));
 assert.ok(w.document.getElementById('open'));assert.match(w.document.querySelector('.studio-photo-unavailable').textContent,/Photo unavailable/);
 w.document.querySelector('.studio-photo-unavailable button').click();assert.equal(w.document.querySelector('img'),image);assert.match(image.src,/versions\/by-id\/version\/content/);
 image.dispatchEvent(new w.Event('error'));assert.equal(w.document.querySelectorAll('.studio-photo-unavailable').length,1);dom.window.close();
});
