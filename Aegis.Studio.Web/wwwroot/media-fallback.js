(() => {
    document.addEventListener('error', event => {
        const image=event.target;
        if(!(image instanceof HTMLImageElement)||!image.isConnected||!image.src.includes('/versions/by-id/'))return;
        const unavailable=document.createElement('span'),message=document.createElement('span'),retry=document.createElement('button');
        const height=getComputedStyle(image).height;
        if(height&&height!=='auto'&&height!=='0px')unavailable.style.height=height;
        unavailable.className='studio-photo-unavailable';message.textContent='Photo unavailable';
        retry.type='button';retry.textContent='Retry photo';
        retry.onclick=event=>{event.preventDefault();event.stopPropagation();unavailable.replaceWith(image);image.src=image.src;};
        unavailable.append(message,retry);image.replaceWith(unavailable);
    },true);
})();
