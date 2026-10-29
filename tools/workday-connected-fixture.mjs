// Disposable local server for the connected .NET acceptance test. Never reads production configuration.
import {createServer} from '../Aegis.Studio.PublicSite/server.mjs';
import {join} from 'node:path';
const root=process.argv[2];
if(!root)throw Error('A disposable storage root is required.');
const server=createServer({NODE_ENV:'test',SESSION_SECRET:'connected-test-only-session-secret-32-characters',
  PUBLIC_API_PROVISIONING_TOKEN:process.env.CONNECTED_TEST_PROVISIONING_TOKEN,
  INTAKE_DB_PATH:join(root,'exchange.sqlite'),WORKDAY_UPLOAD_ROOT:join(root,'private-originals'),WORKDAY_ACK_GRACE_SECONDS:'2'});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
process.stdout.write(JSON.stringify({baseUrl:'http://127.0.0.1:'+server.address().port+'/api/v1/'})+'\n');
let closing=false;
function close(){if(closing)return;closing=true;server.closeAllConnections();server.close(()=>process.exit(0));}
process.stdin.resume();process.stdin.on('end',close);process.on('SIGTERM',close);
