// Optional independent gallery reference host. It has no API/Studio database dependency.
import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { createReferenceGallery } from './reference-gallery.mjs';
import { json,readBody,issueCsrf } from './http-helpers.mjs';

export function createReferenceServer(env=process.env) {
  const settings={...env,REFERENCE_SITE_CSRF_COOKIE:'reference_gallery_csrf'};
  const gallery=createReferenceGallery(settings,readBody,json),root=new URL('./public/',import.meta.url);
  const server=http.createServer(async(req,res)=>{
    const url=new URL(req.url,'http://'+(req.headers.host||'localhost'));
    res.setHeader('Content-Security-Policy',"default-src 'self'; img-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'");
    res.setHeader('X-Frame-Options','DENY');res.setHeader('Referrer-Policy','no-referrer');
    try {
      if(req.method==='GET'&&url.pathname==='/health'){res.writeHead(200,{'Cache-Control':'no-store'}).end('ok');return;}
      if(req.method==='GET'&&url.pathname==='/api/intake-session'){issueCsrf(res,settings,settings.REFERENCE_SITE_CSRF_COOKIE);return;}
      const assets={'/':'gallery-requests.html','/gallery-requests':'gallery-requests.html','/gallery-requests.js':'gallery-requests.js','/exchange.css':'exchange.css'};
      if(req.method==='GET'&&assets[url.pathname]){
        const file=assets[url.pathname],type=file.endsWith('.html')?'text/html':file.endsWith('.css')?'text/css':'text/javascript';
        res.writeHead(200,{'Content-Type':type+'; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});res.end(await readFile(new URL(file,root)));return;
      }
      if(await gallery.handle(req,res,url))return;
      json(res,404,{message:'Not found.'});
    } catch(error) {
      if(!res.headersSent)json(res,error.status||503,{message:error.status?error.message:'Gallery website unavailable. Retry later.'});else res.end();
    }
  });
  server.on('listening',()=>gallery.start());server.on('close',()=>{void gallery.close();});return server;
}
if(process.argv[1]&&fileURLToPath(import.meta.url)===process.argv[1]){
  createReferenceServer().listen(Number(process.env.PORT||8081),process.env.HOST||'127.0.0.1');
}
