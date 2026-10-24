import { GalleryExchangeStore } from './gallery-exchange-store.mjs';
import { fail } from './intake-store.mjs';

export function createGalleryExchangeApi({store,env,authenticate,input,readBody,json}){
  const exchange=new GalleryExchangeStore(store,env);
  let cleaned=0;
  function cursor(value){if(value===null||value==='')return 0;const n=Number(value);if(!Number.isSafeInteger(n)||n<0)fail(400,'invalid_cursor','Use a nonnegative sequence cursor.');return n;}
  return {exchange,async handle(req,res,url){
    const path=url.pathname,method=req.method;
    if(!/^\/api\/v1\/(gallery-(requests|transfers|events|revocations)|admin\/gallery-exchange)(\/|$)/.test(path))return false;
    if(Date.now()-cleaned>60000){exchange.cleanup();cleaned=Date.now();}
    if(path==='/api/v1/gallery-requests'&&method==='POST'){const owner=authenticate(req,'gallery:request');json(res,201,exchange.createRequest(owner,await input(req,8192)));return true;}
    if(path==='/api/v1/gallery-requests/changes'&&method==='GET'){
      authenticate(req,'gallery:publish');const after=cursor(url.searchParams.get('after'));
      json(res,200,{items:store.db.prepare('SELECT * FROM gallery_requests WHERE sequence>? ORDER BY sequence LIMIT 100').all(after).map(r=>({sequence:r.sequence,...exchange.requestView(r,true)}))});return true;
    }
    const request=path.match(/^\/api\/v1\/gallery-requests\/([0-9a-f-]{36})(?:\/(decision|activity))?$/i);
    if(request){
      if(request[2]==='decision'&&method==='POST'){authenticate(req,'gallery:publish');json(res,200,exchange.decide(request[1],await input(req,8192)));return true;}
      const owner=authenticate(req,'gallery:request');
      if(request[2]==='activity'&&method==='GET'){json(res,200,{items:exchange.activity(owner,request[1],cursor(url.searchParams.get('after')))});return true;}
      if(!request[2]&&method==='GET'){json(res,200,exchange.requestView(exchange.request(request[1],owner)));return true;}
    }
    if(path==='/api/v1/gallery-events'&&method==='GET'){
      const owner=authenticate(req,'gallery:transfer');json(res,200,{items:exchange.events(owner,cursor(url.searchParams.get('after')))});return true;
    }
    if(path==='/api/v1/gallery-transfers'&&method==='POST'){authenticate(req,'gallery:publish');json(res,201,exchange.stage(await input(req,5_000_000)));return true;}
    const transfer=path.match(/^\/api\/v1\/gallery-transfers\/([0-9a-f-]{36})(?:\/(activate|ack|files)(?:\/([0-9a-f-]{36}))?)?$/i);
    if(transfer){
      const id=transfer[1],action=transfer[2],fileId=transfer[3];
      if(action==='activate'&&method==='POST'){authenticate(req,'gallery:publish');json(res,200,exchange.activate(id));return true;}
      if(action==='files'&&method==='PUT'&&fileId){authenticate(req,'gallery:publish');exchange.putFile(id,fileId,await readBody(req,100_000_000));json(res,200,{stored:true});return true;}
      const owner=authenticate(req,'gallery:transfer');
      if(!action&&method==='GET'){const r=exchange.transfer(id,owner);if(!['Available','Completed'].includes(r.state))fail(410,'transfer_unavailable','Transfer withdrawn or superseded.');json(res,200,exchange.transferView(r));return true;}
      if(action==='ack'&&method==='POST'){json(res,200,exchange.acknowledge(id,owner,await input(req,8192)));return true;}
      if(action==='files'&&method==='GET'&&fileId){
        const {item,bytes}=exchange.file(id,fileId,owner),range=req.headers.range;
        let start=0,end=bytes.length-1;
        if(range){const m=/^bytes=(\d+)-(\d*)$/.exec(range);if(!m)fail(416,'invalid_range','Use a single byte range.');
          start=Number(m[1]);end=m[2]?Number(m[2]):end;if(start>end||end>=bytes.length)fail(416,'invalid_range','Requested range is unavailable.');}
        res.writeHead(range?206:200,{'Content-Type':item.contentType,'Content-Length':end-start+1,'Accept-Ranges':'bytes','Cache-Control':'no-store','X-Content-Sha256':item.sha256,...(range?{'Content-Range':`bytes ${start}-${end}/${bytes.length}`}:{})});
        res.end(bytes.subarray(start,end+1));return true;
      }
    }
    if(path==='/api/v1/gallery-revocations'&&method==='POST'){authenticate(req,'gallery:publish');json(res,201,exchange.revoke(await input(req,8192)));return true;}
    const revoke=path.match(/^\/api\/v1\/gallery-revocations\/([0-9a-f-]{36})\/ack$/i);
    if(revoke&&method==='POST'){const owner=authenticate(req,'gallery:transfer');json(res,200,exchange.acknowledgeRevocation(revoke[1],owner,await input(req,8192)));return true;}
    if(path==='/api/v1/admin/gallery-exchange/state'&&method==='GET'){
      authenticate(req,'gallery:publish');const id=url.searchParams.get('requestId');exchange.request(id);
      json(res,200,{request:exchange.requestView(exchange.request(id),true),
        transfers:store.db.prepare('SELECT * FROM gallery_transfers WHERE request_id=? ORDER BY version').all(id).map(r=>exchange.transferView(r)),
        revocations:store.db.prepare('SELECT id,gallery_id AS galleryId,state,acknowledgment,created_at AS createdAt,completed_at AS completedAt FROM gallery_revocations WHERE request_id=? ORDER BY created_at').all(id),
        activity:exchange.activity(exchange.request(id).owner,id,cursor(url.searchParams.get('after')))});return true;
    }
    fail(404,'not_found','Gallery exchange operation not found.');
  }};
}
