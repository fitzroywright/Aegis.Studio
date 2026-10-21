import { parentPort, workerData } from 'node:worker_threads';
import { parseRosterFile } from './roster-file.mjs';
try { parentPort.postMessage({csv:await parseRosterFile(Buffer.from(workerData.bytes),workerData.fileName)}); }
catch(e) { parentPort.postMessage({error:e.status?e.message:'Invalid workbook.',code:e.code,status:e.status||400}); }
