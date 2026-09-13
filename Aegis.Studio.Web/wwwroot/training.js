(() => {
  let videos = [];
  let currentView = 'conversations';
  const button = document.getElementById('trainingButton');
  const dialog = document.getElementById('trainingDialog');
  if (!button || !dialog) return;

  const title = dialog.querySelector('[data-training-title]');
  const description = dialog.querySelector('[data-training-description]');
  const player = dialog.querySelector('[data-training-player]');
  const meta = dialog.querySelector('[data-training-meta]');

  function itemFor(view) { return videos.find(x => x.active !== false && x.view === view); }
  function refresh() { button.hidden = !itemFor(currentView); }
  function close() { const frame = player.querySelector('iframe'); if (frame) frame.src = ''; dialog.close(); }

  button.addEventListener('click', () => {
    const item = itemFor(currentView);
    if (!item) return;
    title.textContent = item.title;
    description.textContent = item.description || '';
    meta.textContent = item.duration ? `Expected length: ${item.duration}` : '';
    player.replaceChildren();
    if (item.videoUrl) {
      const frame = document.createElement('iframe');
      frame.src = item.videoUrl;
      frame.title = item.title;
      frame.allow = 'accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share';
      frame.allowFullscreen = true;
      player.appendChild(frame);
    } else {
      const empty = document.createElement('div');
      empty.className = 'training-coming-soon';
      empty.innerHTML = '<strong>Training video coming soon</strong><span>The contextual guide is wired and can be activated by assigning its video URL.</span>';
      player.appendChild(empty);
    }
    dialog.showModal();
  });
  dialog.querySelector('[data-training-close]').addEventListener('click', close);
  dialog.addEventListener('click', event => { if (event.target === dialog) close(); });

  document.querySelectorAll('[data-view]').forEach(nav => nav.addEventListener('click', () => {
    currentView = nav.dataset.view || 'conversations';
    refresh();
  }));

  fetch('/training-videos.json', { cache: 'no-store' })
    .then(response => response.ok ? response.json() : [])
    .then(data => { videos = data; refresh(); })
    .catch(() => { button.hidden = true; });
})();
