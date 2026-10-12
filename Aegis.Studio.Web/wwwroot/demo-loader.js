(() => {
    const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

    async function readRun(url) {
        const response = await fetch(url, {headers:{Accept:'application/json'}, cache:'no-store'});
        if (!response.ok) throw new Error('Demo loader status returned HTTP '+response.status+'.');
        return await response.json();
    }

    window.studioLoadDemoData = async (onProgress, options = {}) => {
        const response = await fetch(options.reset?'/api/admin/connected-workflow/demo-data/reset-and-generate':'/api/admin/demo/seed', {
            method:'POST',
            headers:{Accept:'application/json',...(options.reset?{'Content-Type':'application/json'}:{})},
            ...(options.reset?{body:JSON.stringify({confirmation:'REMOVE STUDIO DEMO DATA'})}:{})
        });
        if (!response.ok) throw new Error('Demo loader returned HTTP '+response.status+'.');

        let run = await response.json();
        if (!run?.runId) throw new Error('The server did not return a demo run identifier.');

        const statusUrl = '/api/admin/demo/seed/' + encodeURIComponent(run.runId);
        onProgress(run);

        while (!run.completed && !run.failed) {
            await wait(1500);
            run = await readRun(statusUrl);
            onProgress(run);
        }

        if (run.failed)
            throw new Error(run.message || 'Demo data could not be loaded.');

        return run;
    };

    window.studioGetCurrentDemoDataRun = async () => {
        const response = await fetch('/api/admin/demo/seed/current', {
            headers:{Accept:'application/json'},
            cache:'no-store'
        });
        if (response.status === 204 || response.status === 404) return null;
        if (!response.ok) throw new Error('Demo loader status returned HTTP '+response.status+'.');
        return await response.json();
    };
})();

