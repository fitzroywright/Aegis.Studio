import { randomUUID } from 'node:crypto';

export function json(res,status,value,headers={}) {
  res.writeHead(status,{'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff',...headers});
  res.end(JSON.stringify(value));
}
export async function readBody(req,limit=65536) {
  const chunks=[];let size=0;
  for await(const chunk of req){size+=chunk.length;if(size>limit)throw Object.assign(new Error('Request is too large.'),{status:413});chunks.push(chunk);}
  return Buffer.concat(chunks);
}
export function issueCsrf(res,env,name='intake_csrf') {
  const csrf=randomUUID();
  json(res,200,{csrf},{'Set-Cookie':name+'='+csrf+'; Path=/; HttpOnly; SameSite=Strict'+(env.NODE_ENV==='production'?'; Secure':'')});
}
