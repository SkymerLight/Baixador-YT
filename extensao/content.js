(() => {
  const client = YTB.createClient();
  let modal = null;

  const isDark = () => document.documentElement.hasAttribute('dark');
  const currentVideoUrl = () => {
    const { pathname, search } = location;
    if (pathname === '/watch' && new URLSearchParams(search).get('v')) return location.href;
    if (/^\/shorts\/[\w-]{11}/.test(pathname)) return location.href;
    return null;
  };

  function prefetch() {
    const url = currentVideoUrl();
    if (url) client.request('info', { url }).catch(() => {});
  }

  const BUTTON_CSS = `
:host { display: inline-flex; align-self: center; margin-left: 8px; flex: none; }
button { display: inline-flex; align-items: center; gap: 6px; height: 36px; padding: 0 16px 0 12px; border: 0;
  border-radius: 18px; cursor: pointer; font: 500 14px/36px Roboto, Arial, sans-serif; white-space: nowrap;
  background: rgba(0,0,0,.05); color: #0f0f0f; }
button:hover { background: rgba(0,0,0,.1); }
button svg { color: #e1002d; }
:host(.floating) button svg { color: #fff; }
:host(.dark) button { background: rgba(255,255,255,.1); color: #f1f1f1; }
:host(.dark) button:hover { background: rgba(255,255,255,.2); }
:host(.floating) { position: fixed; right: 24px; bottom: 24px; z-index: 2147483000; margin: 0; }
:host(.floating) button { height: 44px; border-radius: 22px; background: #e1002d !important; color: #fff !important;
  box-shadow: 0 4px 16px rgba(0,0,0,.3); }
`;

  function makeButton() {
    const host = document.createElement('ytb-download-button');
    const shadow = host.attachShadow({ mode: 'open' });
    const style = document.createElement('style');
    style.textContent = BUTTON_CSS;
    const btn = document.createElement('button');
    btn.title = 'Baixar vídeo ou áudio com o YT Baixador';
    btn.append(YTB.icon('download', 22), 'YT Baixador');
    btn.addEventListener('click', openPanel);
    btn.addEventListener('mouseenter', prefetch, { once: false });
    shadow.append(style, btn);
    return host;
  }

  function placeButton() {
    const url = currentVideoUrl();
    let host = document.querySelector('ytb-download-button');

    if (!url) {
      host?.remove();
      return;
    }
    const isShorts = location.pathname.startsWith('/shorts/');
    const target = isShorts
      ? document.body
      : document.querySelector('ytd-watch-metadata #top-level-buttons-computed') ||
        document.querySelector('ytd-watch-metadata #actions');
    if (!target) return;

    if (!host) host = makeButton();
    host.classList.toggle('dark', isDark());
    host.classList.toggle('floating', isShorts);
    if (host.parentElement !== target) target.append(host);
  }

  const getVideo = () =>
    document.querySelector('#movie_player video.html5-main-video') ||
    document.querySelector('ytd-reel-video-renderer[is-active] video') ||
    document.querySelector('video');

  const player = YTB.mediaPlayer(getVideo);

  function openPanel() {
    const url = currentVideoUrl();
    if (!url) return;
    closePanel();

    const host = document.createElement('ytb-download-panel');
    const shadow = host.attachShadow({ mode: 'open' });
    const style = document.createElement('style');
    style.textContent =
      YTB.css +
      `
:host { position: fixed; top: 68px; right: 16px; z-index: 2147483600; }
.ytb-modal { width: min(440px, calc(100vw - 32px)); max-height: calc(100vh - 84px); overflow: auto; padding: 14px 16px 16px;
  border-radius: 16px; background: var(--bg); box-shadow: 0 12px 40px rgba(0,0,0,.35), 0 0 0 1px var(--line);
  animation: ytb-pop .18s ease; }
@keyframes ytb-pop { from { opacity: 0; transform: translateY(-6px) scale(.98); } }
`;
    const root = document.createElement('div');
    root.className = `ytb-root ytb-modal${isDark() ? ' ytb-dark' : ''}`;
    root.setAttribute('role', 'dialog');
    root.setAttribute('aria-label', 'Baixar vídeo');
    for (const type of ['keydown', 'keypress', 'keyup']) {
      root.addEventListener(type, (e) => e.key !== 'Escape' && e.stopPropagation());
    }
    shadow.append(style, root);
    document.documentElement.append(host);

    const panel = new YTB.Panel(root, { client, onClose: closePanel, player });
    panel.load(url);
    modal = { host, panel };
    document.addEventListener('keydown', onKey, true);
  }

  function closePanel() {
    if (!modal) return;
    modal.panel.destroy();
    modal.host.remove();
    modal = null;
    document.removeEventListener('keydown', onKey, true);
  }

  function onKey(e) {
    if (e.key === 'Escape') {
      e.stopPropagation();
      closePanel();
    }
  }

  let scheduled = false;
  const schedule = () => {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(() => {
      scheduled = false;
      placeButton();
    });
  };

  document.addEventListener('yt-navigate-finish', () => {
    closePanel();
    schedule();
  });
  new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
  schedule();
})();
