import {sha256} from '/vendor/noble/sha2.js';
self.onmessage=async({data})=>{
  try{
    const hash=sha256.create(),step=2*1024*1024;
    for(let offset=0;offset<data.file.size;offset+=step){
      hash.update(new Uint8Array(await data.file.slice(offset,offset+step).arrayBuffer()));
      self.postMessage({hashed:Math.min(offset+step,data.file.size)});
    }
    self.postMessage({sha256:[...hash.digest()].map(x=>x.toString(16).padStart(2,'0')).join('')});
  }catch{self.postMessage({error:'Could not hash the selected photograph.'});}
};
