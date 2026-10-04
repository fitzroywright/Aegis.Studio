(() => {
    window.studioLoadDemoData = async onProgress => {
        const response = await fetch('/api/admin/demo/seed', {method:'POST',headers:{Accept:'application/x-ndjson'}});
        if (!response.ok) throw new Error('Demo loader returned HTTP '+response.status+'.');
        if (!response.headers.get('content-type')?.includes('application/x-ndjson') || !response.body)
            throw new Error('The server did not return demo progress. Refresh the application and retry.');
        const reader=response.body.getReader(),decoder=new TextDecoder();
        let buffer='',complete=null;
        function update(line){
            if(!line.trim())return;
            const progress=JSON.parse(line);
            if(progress.failed)throw new Error(progress.message||'Demo data could not be loaded.');
            onProgress(progress);
            if(progress.completed)complete=progress;
        }
        try{
            while(true){
                const {value,done}=await reader.read();
                buffer+=decoder.decode(value,{stream:!done});
                let newline;
                while((newline=buffer.indexOf('\n'))>=0){update(buffer.slice(0,newline));buffer=buffer.slice(newline+1);}
                if(done){update(buffer);break;}
            }
            if(!complete)throw new Error('Connection ended before demo loading completed. Retry to check or finish the dataset.');
            return complete;
        }finally{await reader.cancel().catch(()=>{});reader.releaseLock();}
    };
})();
