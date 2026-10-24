import { RemoteMediaStore } from './remote-media-store.mjs';
import { activityFor } from './exchange-activity.mjs';
import { fail } from './intake-store.mjs';
import { createReadStream } from 'node:fs';
import { pipeline } from 'node:stream/promises';

export function createRemoteMediaApi({store,workdayStore,env,authenticate,requireWorkday,input,readBody,json,scanPath}) {
  const media=new RemoteMediaStore(store,env);
  let cleaned=0;
  function destination(user,p) {
    const snapshot=workdayStore.snapshot(user.user_name),d=snapshot?.mediaDestinations?.[p.assignmentId];
    if(!d||d.jobId!==p.jobId||d.sessionId!==(p.sessionId||null)||!d.collections.includes(p.collection)||
       p.subjectId&&!d.subjects.some(s=>s.id===p.subjectId&&s.collection===p.collection))
      fail(403,'destination_unavailable','Refresh your authorized job and collection destinations.');
    return d;
  }
  return {media,async handle(req,res,url) {
    const route=url.pathname,method=req.method;
    if(!/remote-media|workday\/destinations/.test(route))return false;
    if(Date.now()-cleaned>60_000){media.cleanup();cleaned=Date.now();}
    if(route==='/api/v1/workday/destinations'&&method==='GET'){
      const u=requireWorkday(req),s=workdayStore.snapshot(u.user_name);if(!s)fail(503,'snapshot_pending','Current Studio permissions are unavailable.');
      json(res,200,{updatedAt:s.updatedAt,items:Object.entries(s.mediaDestinations||{}).map(([assignmentId,value])=>({assignmentId,...value}))});return true;
    }
    if(route==='/api/v1/workday/remote-media'&&method==='GET'){
      const u=requireWorkday(req);json(res,200,{items:media.list(u.user_key)});return true;
    }
    if(route==='/api/v1/workday/remote-media/batches'&&method==='POST'){
      const u=requireWorkday(req),p=await input(req,1_000_000);destination(u,p);
      json(res,201,media.create(u.user_key,p));return true;
    }
    const file=route.match(/^\/api\/v1\/workday\/remote-media\/([0-9a-f-]{36})(?:\/(chunks|complete|activity))?$/i);
    if(file){
      const u=requireWorkday(req),r=media.get(file[1],u.user_key);
      if(file[2]==='activity'&&method==='GET'){json(res,200,{items:activityFor(store.db,u.user_key,r.id,Number(url.searchParams.get('after'))||0)});return true;}
      if(!file[2]&&method==='GET'){json(res,200,media.view(r));return true;}
      if(!file[2]&&method==='DELETE'){json(res,200,media.cancel(u.user_key,r.id));return true;}
      destination(u,JSON.parse(r.destination));
      if(file[2]==='chunks'&&method==='PUT'){
        json(res,200,media.chunk(u.user_key,r.id,Number(req.headers['x-upload-offset']),await readBody(req,4_000_000),String(req.headers['x-content-sha256']||'')));return true;
      }
      if(file[2]==='complete'&&method==='POST'){json(res,202,await media.complete(u.user_key,r.id,scanPath));return true;}
    }
    if(route==='/api/v1/admin/workday/remote-media'&&method==='GET'){
      authenticate(req,'workday:sync');json(res,200,{items:media.ready()});return true;
    }
    const admin=route.match(/^\/api\/v1\/admin\/workday\/remote-media\/([0-9a-f-]{36})\/(content|result|retry)$/i);
    if(admin){
      authenticate(req,'workday:sync');const r=media.get(admin[1]);
      if(admin[2]==='retry'&&method==='POST'){json(res,200,media.retry(r.id,await input(req,8192)));return true;}
      if(admin[2]==='result'&&method==='POST'){json(res,200,media.acknowledge(r.id,await input(req,16384)));return true;}
      if(admin[2]==='content'&&method==='GET'){
        if(r.state!=='ReceivedByApi')fail(409,'upload_unavailable','Upload is not ready for Studio import.');
        res.writeHead(200,{'Content-Type':r.content_type,'Content-Length':r.size_bytes,'X-Content-Sha256':r.sha256,'Cache-Control':'no-store'});
        await pipeline(createReadStream(media.path(r.id)),res);return true;
      }
    }
    fail(404,'not_found','Remote media operation not found.');
  }};
}
