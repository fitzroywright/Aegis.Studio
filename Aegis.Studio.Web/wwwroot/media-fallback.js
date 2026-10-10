(() => {
    document.addEventListener('error', event => {
        const image=event.target;
        if(!(image instanceof HTMLImageElement)||!image.isConnected||!image.src.includes('/versions/by-id/'))return;
        // A page with its own recovery handler owns the first error. Do not detach
        // its image before that handler can reveal a placeholder or load a fallback.
        if(image.dataset.previewOwner||typeof image.onerror==='function')return;
        const unavailable=document.createElement('span'),message=document.createElement('span'),retry=document.createElement('button');
        const height=getComputedStyle(image).height;
        if(height&&height!=='auto'&&height!=='0px')unavailable.style.height=height;
        unavailable.className='studio-photo-unavailable';message.textContent=image.src.includes('/preview')?'Preview unavailable':'Photo unavailable';
        retry.type='button';retry.textContent=image.src.includes('/preview')?'Retry preview':'Retry photo';
        retry.onclick=event=>{event.preventDefault();event.stopPropagation();unavailable.replaceWith(image);image.src=image.src;};
        unavailable.append(message,retry);image.replaceWith(unavailable);
    },true);
})();

