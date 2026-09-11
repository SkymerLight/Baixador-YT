(() => {
  if (globalThis.YTB) return;
  const YTB = (globalThis.YTB = {});

  const AUDIO_OPTIONS = [
    { value: 'm4a', name: 'M4A', sub: 'Melhor qualidade', size: 'm4a',
      note: 'Áudio original do YouTube, sem conversão nenhuma. M4A é o "MP4 de áudio" e toca em quase tudo.' },
    { value: 'opus', name: 'Opus', sub: 'original', size: 'opus',
      note: 'Também é o original, sem conversão. Soa igual ao M4A, mas alguns aparelhos antigos não tocam.' },
    { value: 'mp3-320', name: 'MP3', sub: '320 kbps', kbps: 320,
      note: 'O YouTube manda o áudio em ~130 kbps. O MP3 320 fica maior, mas não soa melhor que o original. Use se o aparelho só tocar MP3.' },
    { value: 'mp3-192', name: 'MP3', sub: '192 kbps', kbps: 192, note: 'Toca em qualquer aparelho, com boa qualidade.' },
    { value: 'mp3-128', name: 'MP3', sub: '128 kbps', kbps: 128, note: 'Mais leve, bom para celular.' },
    { value: 'mp3-64', name: 'MP3', sub: '64 kbps', kbps: 64, note: 'Bem leve. Serve para voz: podcast, aula, palestra.' },
    { value: 'wav', name: 'WAV', sub: 'para edição', kbps: 1411,
      note: 'Sem compressão, para abrir em editor de áudio. Arquivo grande e a mesma qualidade do original.' },
    { value: 'flac', name: 'FLAC', sub: 'para edição', kbps: 750,
      note: 'Igual ao WAV, mas comprimido sem perda (arquivo menor). Não melhora o som do original.' },
  ];
  const ACTIVE = new Set(['starting', 'downloading', 'processing']);

  YTB.createClient = () => {
    let port = null;
    let seq = 0;
    let dead = false;
    const pending = new Map();
    const jobs = new Map();
    const listeners = new Set();

    const emit = () => listeners.forEach((fn) => fn(jobs));

    function ensure() {
      if (port) return port;
      if (dead) throw new Error('A extensão foi atualizada. Recarregue a página (F5).');
      try {
        port = chrome.runtime.connect({ name: 'ytb-ui' });
      } catch {
        dead = true;
        throw new Error('A extensão foi atualizada. Recarregue a página (F5).');
      }
      port.onMessage.addListener((msg) => {
        if (msg.type === 'reply') {
          const req = pending.get(msg.reqId);
          if (!req) return;
          pending.delete(msg.reqId);
          if (msg.ok) req.resolve(msg.result);
          else req.reject(Object.assign(new Error(msg.error), { code: msg.code }));
        } else if (msg.type === 'jobs') {
          jobs.clear();
          msg.jobs.forEach((j) => jobs.set(j.id, j));
          emit();
        } else if (msg.type === 'job') {
          jobs.set(msg.job.id, msg.job);
          emit();
        }
      });
      port.onDisconnect.addListener(() => {
        port = null;
        for (const req of pending.values()) req.reject(new Error('Conexão com a extensão perdida. Tente de novo.'));
        pending.clear();
        if (listeners.size) setTimeout(() => listeners.size && tryEnsure(), 1000);
      });
      return port;
    }

    function tryEnsure() {
      try {
        ensure();
      } catch {}
    }

    return {
      jobs,
      request(action, data = {}) {
        return new Promise((resolve, reject) => {
          try {
            const reqId = ++seq;
            pending.set(reqId, { resolve, reject });
            ensure().postMessage({ reqId, action, ...data });
          } catch (e) {
            reject(e);
          }
        });
      },
      subscribe(fn) {
        listeners.add(fn);
        tryEnsure();
        fn(jobs);
        return () => listeners.delete(fn);
      },
    };
  };

  const fmt = (YTB.fmt = {
    bytes(n) {
      if (!n) return '';
      const units = ['B', 'KB', 'MB', 'GB'];
      let i = 0;
      while (n >= 1024 && i < units.length - 1) {
        n /= 1024;
        i++;
      }
      return `${n.toFixed(n < 10 && i > 1 ? 1 : 0)} ${units[i]}`;
    },
    time(s) {
      if (s == null || !isFinite(s)) return '';
      s = Math.round(s);
      const h = Math.floor(s / 3600);
      const m = Math.floor((s % 3600) / 60);
      const sec = String(s % 60).padStart(2, '0');
      return h ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`;
    },
  });

  function parseTime(text) {
    const str = String(text ?? '').trim().replace(',', '.');
    if (!str) return null;
    const parts = str.split(':');
    if (parts.length > 3 || parts.some((p) => !/^\d+(\.\d+)?$/.test(p))) return NaN;
    return parts.reduce((acc, p) => acc * 60 + parseFloat(p), 0);
  }

  function h(tag, attrs = {}, ...children) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
      if (v == null || v === false) continue;
      if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
      else if (k === 'class') el.className = v;
      else if (k === 'value') el.value = v;
      else el.setAttribute(k, v === true ? '' : v);
    }
    for (const c of children.flat()) if (c != null && c !== false) el.append(c);
    return el;
  }
  YTB.h = h;

  const ICONS = {
    download: 'M12 3v10.2l3.6-3.6L17 11l-5 5-5-5 1.4-1.4 3.6 3.6V3h2zM5 18h14v2H5z',
    close: 'M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z',
    folder: 'M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z',
    stop: 'M6 6h12v12H6z',
    play: 'M8 5v14l11-7z',
    pause: 'M6 5h4v14H6zm8 0h4v14h-4z',
    scissors: 'M9.64 7.64c.23-.5.36-1.05.36-1.64 0-2.21-1.79-4-4-4S2 3.79 2 6s1.79 4 4 4c.59 0 1.14-.13 1.64-.36L10 12l-2.36 2.36C7.14 14.13 6.59 14 6 14c-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4c0-.59-.13-1.14-.36-1.64L12 14l7 7h3v-1L9.64 7.64zM6 8c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm0 12c-1.1 0-2-.89-2-2s.9-2 2-2 2 .89 2 2-.9 2-2 2zm6-7.5c-.28 0-.5-.22-.5-.5s.22-.5.5-.5.5.22.5.5-.22.5-.5.5zM19 3l-6 6 2 2 7-7V3h-3z',
    clock: 'M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm0 18a8 8 0 1 1 0-16 8 8 0 0 1 0 16zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67V7z',
  };
  const icon = (name, size = 20) => {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('width', size);
    svg.setAttribute('height', size);
    svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', ICONS[name]);
    path.setAttribute('fill', 'currentColor');
    svg.append(path);
    return svg;
  };
  YTB.icon = icon;

  YTB.mediaPlayer = (getMedia) => ({
    now: () => getMedia()?.currentTime ?? 0,
    seek(t) {
      const m = getMedia();
      if (m) m.currentTime = t;
    },
    playRange(start, end, onStop) {
      const m = getMedia();
      if (!m) return () => {};
      let raf = 0;
      let done = false;
      const finish = (pause) => {
        if (done) return;
        done = true;
        cancelAnimationFrame(raf);
        m.removeEventListener('pause', onPause);
        if (pause) m.pause();
        onStop();
      };
      const onPause = () => finish(false);
      const tick = () => {
        if (m.currentTime >= end) return finish(true);
        if (m.currentTime < start - 1) return finish(false);
        raf = requestAnimationFrame(tick);
      };
      m.currentTime = start;
      m.play().catch(() => finish(false));
      m.addEventListener('pause', onPause);
      raf = requestAnimationFrame(tick);
      return () => finish(true);
    },
    onTime(cb) {
      const m = getMedia();
      if (!m) return () => {};
      const events = ['timeupdate', 'play', 'pause', 'seeked'];
      const handler = () => cb(m.currentTime);
      events.forEach((e) => m.addEventListener(e, handler));
      return () => events.forEach((e) => m.removeEventListener(e, handler));
    },
  });

  function statusText(job) {
    switch (job.status) {
      case 'starting':
        return 'Iniciando…';
      case 'downloading': {
        const what = job.mode === 'audio' || job.stream === 'audio' ? 'áudio' : 'vídeo';
        const parts = [`Baixando ${what}`];
        if (job.percent != null) parts.push(`${Math.floor(job.percent)}%`);
        else if (job.downloaded) parts.push(fmt.bytes(job.downloaded));
        if (job.speed) parts.push(`${fmt.bytes(job.speed)}/s`);
        if (job.eta) parts.push(`${fmt.time(job.eta)} restantes`);
        return parts.join(' · ');
      }
      case 'processing':
        return `${job.stageLabel || 'Processando'}…`;
      case 'done':
        return `Pronto${job.size ? ` · ${fmt.bytes(job.size)}` : ''}`;
      case 'cancelled':
        return 'Cancelado';
      default:
        return job.error || 'Falhou';
    }
  }

  YTB.renderJob = (job, client, { showTitle = true } = {}) => {
    const active = ACTIVE.has(job.status);
    const pct = job.status === 'done' ? 100 : job.percent ?? 0;
    const indeterminate = job.status === 'starting' || job.status === 'processing' || job.percent == null;
    const act = (action) => () => client.request(action, { jobId: job.id }).catch((e) => alert(e.message));

    return h(
      'div',
      { class: `ytb-job is-${job.status}`, title: job.detail || null },
      showTitle && job.thumbnail ? h('img', { class: 'ytb-job-thumb', src: job.thumbnail, alt: '' }) : null,
      h(
        'div',
        { class: 'ytb-job-body' },
        showTitle ? h('div', { class: 'ytb-job-title' }, job.title) : null,
        h(
          'div',
          { class: 'ytb-job-meta' },
          h('span', { class: 'ytb-tag' }, job.label || (job.mode === 'audio' ? 'Áudio' : 'Vídeo')),
          h('span', { class: 'ytb-job-status' }, statusText(job))
        ),
        active || job.status === 'done'
          ? h(
              'div',
              { class: `ytb-bar${indeterminate && active ? ' is-indeterminate' : ''}` },
              h('div', { class: 'ytb-bar-fill', style: `width:${indeterminate && active ? 100 : pct}%` })
            )
          : null
      ),
      active
        ? h('button', { class: 'ytb-icon-btn', title: 'Cancelar', onclick: act('cancel') }, icon('stop', 18))
        : job.status === 'done'
          ? h('button', { class: 'ytb-icon-btn', title: 'Mostrar na pasta', onclick: act('reveal') }, icon('folder', 18))
          : null
    );
  };

  YTB.Panel = class {
    constructor(root, { client, onClose, embedded = false, player = null, makePlayer = null }) {
      this.root = root;
      this.client = client;
      this.onClose = onClose;
      this.embedded = embedded;
      this.makePlayer = makePlayer;
      this.refs = {};
      this.state = { phase: 'idle' };
      this.unsub = client.subscribe(() => this.renderJobs());
      this.usePlayer(player);
    }

    usePlayer(player) {
      this.unTime?.();
      if (this.makePlayer && this.player !== player) this.player?.destroy?.();
      this.player = player;
      this.unTime = player?.onTime((t) => this.onPlayerTime(t));
    }

    destroy() {
      this.stopPreview?.();
      this.unsub();
      this.usePlayer(null);
      this.root.replaceChildren();
    }

    async load(url) {
      this.url = url;
      this.stopPreview?.();
      this.state = { phase: 'loading' };
      this.render();
      const token = (this.token = Symbol());
      try {
        const [info, settings] = await Promise.all([
          this.client.request('info', { url }),
          this.client.request('getSettings'),
        ]);
        if (token !== this.token) return;
        const mode = info.video.length ? settings.lastMode : 'audio';
        const quality =
          info.video.find((v) => v.q <= settings.lastQuality)?.q ?? info.video[info.video.length - 1]?.q;
        const audio = AUDIO_OPTIONS.some((o) => o.value === settings.lastAudio) ? settings.lastAudio : 'm4a';
        if (this.makePlayer) this.usePlayer(this.makePlayer(info));
        this.state = { phase: 'ready', info, settings, mode, quality, audio, cut: { on: false, start: '', end: '' } };
      } catch (e) {
        if (token !== this.token) return;
        this.state = { phase: 'error', error: e.message, code: e.code };
      }
      this.render();
    }

    set(patch) {
      Object.assign(this.state, patch);
      this.render();
    }

    cutRange() {
      const { cut, info } = this.state;
      if (!cut?.on) return null;
      const start = parseTime(cut.start) ?? 0;
      const end = parseTime(cut.end) ?? info.duration;
      if (Number.isNaN(start) || Number.isNaN(end)) return { error: 'Use o formato minutos:segundos, tipo 1:30.' };
      if (info.duration && end > info.duration + 1) return { error: `O vídeo só vai até ${fmt.time(info.duration)}.` };
      if (end - start < 1) return { error: 'O fim precisa ser pelo menos 1 segundo depois do início.' };
      return { start, end };
    }

    setCut(patch, rerender = false) {
      this.stopPreview?.();
      Object.assign(this.state.cut, patch);
      if (rerender) this.render();
      else this.refreshCut();
    }

    refreshCut() {
      const r = this.refs;
      const range = this.cutRange();
      const { duration } = this.state.info || {};
      const invalid = !!range?.error;
      if (r.download) r.download.disabled = this.state.starting || invalid;
      if (r.cutError) {
        r.cutError.textContent = invalid ? range.error : range ? `Trecho de ${fmt.time(range.end - range.start)}` : '';
        r.cutError.classList.toggle('is-bad', invalid);
      }
      if (r.preview) r.preview.disabled = invalid;
      if (r.seg && duration) {
        const ok = range && !invalid;
        r.seg.style.left = ok ? `${(range.start / duration) * 100}%` : '0';
        r.seg.style.width = ok ? `${((range.end - range.start) / duration) * 100}%` : '0';
      }
      if (r.hStart && duration) {
        const pos = (t) => `${Math.min(100, Math.max(0, (t / duration) * 100))}%`;
        r.hStart.style.left = pos(this.cutPoint('start'));
        r.hEnd.style.left = pos(this.cutPoint('end'));
      }
      this.refreshSizes();
    }

    cutPoint(key) {
      const t = parseTime(this.state.cut[key]);
      if (Number.isFinite(t)) return t;
      return key === 'start' ? 0 : this.state.info.duration;
    }

    setHandle(key, t, seek) {
      const { duration } = this.state.info;
      const other = this.cutPoint(key === 'start' ? 'end' : 'start');
      t = Math.round(Math.max(0, Math.min(duration, t)));
      t = key === 'start' ? Math.min(t, other - 1) : Math.max(t, other + 1);
      this.state.cut[key] = fmt.time(t);
      const input = this.refs[key === 'start' ? 'inStart' : 'inEnd'];
      if (input) input.value = this.state.cut[key];
      this.refreshCut();
      if (seek && this.player) this.player.seek(t);
    }

    dragHandle(key, e) {
      e.preventDefault();
      e.stopPropagation();
      this.stopPreview?.();
      const handle = e.currentTarget;
      const track = this.refs.track;
      handle.setPointerCapture(e.pointerId);
      handle.focus();
      let lastSeek = 0;
      const move = (ev) => {
        const rect = track.getBoundingClientRect();
        const t = ((ev.clientX - rect.left) / rect.width) * this.state.info.duration;
        const seek = Date.now() - lastSeek > 150;
        if (seek) lastSeek = Date.now();
        this.setHandle(key, t, seek);
      };
      const up = (ev) => {
        handle.removeEventListener('pointermove', move);
        handle.removeEventListener('pointerup', up);
        handle.removeEventListener('pointercancel', up);
        lastSeek = 0;
        move(ev);
      };
      handle.addEventListener('pointermove', move);
      handle.addEventListener('pointerup', up);
      handle.addEventListener('pointercancel', up);
    }

    nudgeHandle(key, e) {
      const steps = { ArrowLeft: -1, ArrowDown: -1, ArrowRight: 1, ArrowUp: 1 };
      if (!(e.key in steps)) return;
      e.preventDefault();
      this.stopPreview?.();
      this.setHandle(key, this.cutPoint(key) + steps[e.key] * (e.shiftKey ? 5 : 1), true);
    }

    refreshSizes() {
      for (const [el, size] of this.refs.sizes || []) el.textContent = size();
    }

    onPlayerTime(t) {
      const { duration } = this.state.info || {};
      if (this.refs.head && duration) this.refs.head.style.left = `${Math.min(100, (t / duration) * 100)}%`;
      if (this.refs.clock) this.refs.clock.textContent = `${fmt.time(t)} / ${fmt.time(duration)}`;
      if (this.refs.free) {
        const playing = !this.player.paused() && !this.stopPreview;
        this.refs.free.replaceChildren(icon(playing ? 'pause' : 'play', 18), playing ? 'Pausar' : 'Ouvir');
      }
    }

    toggleFree() {
      if (this.stopPreview) this.stopPreview();
      if (this.player.paused()) this.player.play();
      else this.player.pause();
    }

    togglePreview() {
      if (this.stopPreview) return this.stopPreview();
      const range = this.cutRange();
      if (!range || range.error) return;
      const stop = this.player.playRange(range.start, range.end, () => {
        this.stopPreview = null;
        this.paintPreviewButton();
        this.onPlayerTime(this.player.now());
      });
      this.stopPreview = () => stop();
      this.paintPreviewButton();
      this.onPlayerTime(this.player.now());
    }

    paintPreviewButton() {
      const btn = this.refs.preview;
      if (!btn) return;
      const playing = !!this.stopPreview;
      btn.replaceChildren(icon(playing ? 'pause' : 'play', 18), playing ? 'Pausar' : 'Tocar trecho');
    }

    async start() {
      const { info, mode, quality, audio } = this.state;
      const range = this.cutRange();
      if (range?.error) return;
      const audioOpt = AUDIO_OPTIONS.find((o) => o.value === audio);
      let label =
        mode === 'audio'
          ? audioOpt.name === 'MP3' ? `MP3 ${audioOpt.kbps}` : audioOpt.name
          : info.video.find((v) => v.q === quality)?.label;
      if (range) label += ` · ${fmt.time(range.start)} a ${fmt.time(range.end)}`;
      this.stopPreview?.();
      this.set({ starting: true });
      try {
        const job = await this.client.request('download', {
          url: info.url,
          mode,
          quality,
          audioFormat: audio,
          section: range || undefined,
          meta: { id: info.id, title: info.title, thumbnail: info.thumbnail, label },
        });
        if (range && !job?.section) {
          this.client.request('cancel', { jobId: job.id }).catch(() => {});
          alert('O recorte não foi aplicado porque o navegador ainda está rodando a versão antiga da extensão.\n\n' +
            'Abra edge://extensions, clique em "Recarregar" no YT Baixador e tente de novo.');
        }
      } catch (e) {
        alert(e.message);
      }
      this.set({ starting: false });
    }

    header() {
      return h(
        'div',
        { class: 'ytb-head' },
        h('div', { class: 'ytb-brand' }, h('span', { class: 'ytb-logo' }, icon('download', 16)), 'YT Baixador'),
        this.onClose
          ? h('button', { class: 'ytb-icon-btn', title: 'Fechar (Esc)', onclick: () => this.onClose() }, icon('close'))
          : null
      );
    }

    render() {
      const s = this.state;
      this.refs = {};
      const body = [];
      if (s.phase === 'loading') {
        body.push(h('div', { class: 'ytb-loading' }, h('div', { class: 'ytb-spinner' }), 'Lendo o vídeo…'));
      } else if (s.phase === 'error') {
        body.push(this.renderError());
      } else if (s.phase === 'ready') {
        body.push(this.renderReady());
      }
      this.jobsBox = h('div', { class: 'ytb-jobs' });
      this.root.replaceChildren(
        h('div', { class: 'ytb-panel' }, this.embedded ? null : this.header(), ...body, this.jobsBox)
      );
      if (s.phase === 'ready') {
        this.refreshCut();
        this.paintPreviewButton();
        if (this.player) this.onPlayerTime(this.player.now());
      }
      this.renderJobs();
    }

    renderError() {
      const s = this.state;
      const extra =
        s.code === 'NO_HOST'
          ? h(
              'ol',
              { class: 'ytb-steps' },
              h('li', {}, 'Baixe o ', h('b', {}, 'YTBaixador-Instalador.exe'), ' no GitHub do projeto.'),
              h('li', {}, 'Dê dois cliques nele e clique em Instalar.'),
              h('li', {}, 'Recarregue esta página.')
            )
          : null;
      return h(
        'div',
        { class: 'ytb-error' },
        h('p', {}, s.error),
        extra,
        h('button', { class: 'ytb-btn ytb-btn-ghost', onclick: () => this.load(this.url) }, 'Tentar de novo')
      );
    }

    renderReady() {
      const { info, mode, quality, audio, starting } = this.state;
      const sizes = (this.refs.sizes = []);
      const factor = () => {
        const range = this.cutRange();
        return range && !range.error && info.duration ? (range.end - range.start) / info.duration : 1;
      };
      const sizeText = (bytes, prefix = '') => () => (bytes ? `${prefix}~${fmt.bytes(bytes * factor())}` : '');

      const tab = (value, text, disabled) =>
        h(
          'button',
          { class: `ytb-tab${mode === value ? ' is-on' : ''}`, disabled, onclick: () => this.set({ mode: value }) },
          text
        );

      const option = (on, name, sub, getSize, onclick) => {
        const subEl = h('span', { class: 'ytb-opt-sub' });
        const sizeEl = h('span', { class: 'ytb-opt-size' });
        subEl.textContent = sub;
        sizes.push([sizeEl, getSize]);
        return h(
          'button',
          { class: `ytb-opt${on ? ' is-on' : ''}`, onclick },
          h('span', { class: 'ytb-opt-name' }, name),
          subEl,
          sizeEl
        );
      };

      const options =
        mode === 'video'
          ? info.video.map((v) =>
              option(quality === v.q, v.label, qualityName(v.q), sizeText(v.size), () => this.set({ quality: v.q }))
            )
          : AUDIO_OPTIONS.map((o) => {
              const bytes = o.size ? info.audioSizes?.[o.size] || info.audioSize : o.kbps * 125 * info.duration;
              return option(audio === o.value, o.name, o.sub, sizeText(bytes), () => this.set({ audio: o.value }));
            });

      const note = mode === 'audio' ? AUDIO_OPTIONS.find((o) => o.value === audio)?.note : null;
      const settings = this.state.settings || {};
      const extras = [];
      if (settings.sponsorblock && info.site === 'Youtube' && !this.state.cut.on) extras.push('tirar os patrocínios (SponsorBlock)');
      if (settings.normalize && mode === 'audio' && audio.startsWith('mp3')) extras.push('igualar o volume');

      this.refs.download = h(
        'button',
        { class: 'ytb-btn ytb-btn-primary', disabled: starting, onclick: () => this.start() },
        icon('download', 18),
        starting ? 'Iniciando…' : 'Baixar'
      );

      return h(
        'div',
        { class: 'ytb-ready' },
        h(
          'div',
          { class: 'ytb-video' },
          info.thumbnail
            ? h('img', { class: 'ytb-thumb', src: info.thumbnail, alt: '', referrerpolicy: 'no-referrer', onerror: (e) => e.target.remove() })
            : null,
          h(
            'div',
            { class: 'ytb-video-text' },
            h('div', { class: 'ytb-title', title: info.title }, info.title),
            h('div', { class: 'ytb-sub' }, [info.uploader, fmt.time(info.duration)].filter(Boolean).join(' · '))
          )
        ),
        h(
          'div',
          { class: 'ytb-tabs' },
          tab('video', 'Vídeo (MP4)', !info.video.length),
          tab('audio', 'Só áudio', !info.hasAudio)
        ),
        h('div', { class: 'ytb-opts' }, options),
        note ? h('p', { class: 'ytb-note' }, note) : null,
        this.renderCut(),
        this.refs.download,
        extras.length ? h('p', { class: 'ytb-note ytb-extras' }, `Também vai ${extras.join(' e ')}.`) : null
      );
    }

    renderCut() {
      const { cut, info, mode } = this.state;
      const toggle = h(
        'button',
        {
          class: `ytb-cut-toggle${cut.on ? ' is-on' : ''}`,
          'aria-pressed': String(cut.on),
          onclick: () => this.setCut({ on: !cut.on }, true),
        },
        icon('scissors', 18),
        cut.on ? 'Recortar trecho' : 'Recortar só um trecho',
        h('span', { class: 'ytb-switch' })
      );
      if (!cut.on) return h('div', { class: 'ytb-cut' }, toggle);

      const field = (key, label, placeholder) => {
        const input = h('input', {
          class: 'ytb-time',
          value: cut[key],
          placeholder,
          inputmode: 'numeric',
          spellcheck: 'false',
          'aria-label': label,
          oninput: (e) => this.setCut({ [key]: e.target.value }),
          onkeydown: (e) => e.key === 'Enter' && e.target.blur(),
          onchange: (e) => {
            const t = parseTime(e.target.value);
            if (t != null && !Number.isNaN(t)) e.target.value = this.state.cut[key] = fmt.time(t);
            this.refreshCut();
          },
        });
        const now = this.player
          ? h(
              'button',
              {
                class: 'ytb-mini-btn',
                title: 'Usar o ponto onde o vídeo está agora',
                onclick: () => this.setCut({ [key]: fmt.time(this.player.now()) }, true),
              },
              icon('clock', 16),
              'Agora'
            )
          : null;
        this.refs[key === 'start' ? 'inStart' : 'inEnd'] = input;
        return h('label', { class: 'ytb-field' }, h('span', { class: 'ytb-field-label' }, label), h('span', { class: 'ytb-field-row' }, input, now));
      };

      const handle = (key, label) =>
        h('div', {
          class: `ytb-handle ytb-handle-${key}`,
          role: 'slider',
          tabindex: '0',
          'aria-label': label,
          title: `${label}: arraste ou use as setas`,
          onpointerdown: (e) => this.dragHandle(key, e),
          onkeydown: (e) => this.nudgeHandle(key, e),
          onclick: (e) => e.stopPropagation(),
        });

      this.refs.seg = h('div', { class: 'ytb-seg' });
      this.refs.head = this.player ? h('div', { class: 'ytb-head-mark' }) : null;
      this.refs.hStart = handle('start', 'Início do trecho');
      this.refs.hEnd = handle('end', 'Fim do trecho');
      const timeline = (this.refs.track = h(
        'div',
        {
          class: `ytb-timeline${this.player ? ' is-clickable' : ''}`,
          title: this.player ? 'Clique para pular para esse ponto do vídeo' : null,
          onclick: (e) => {
            if (!this.player || !info.duration) return;
            const rect = e.currentTarget.getBoundingClientRect();
            this.player.seek(((e.clientX - rect.left) / rect.width) * info.duration);
          },
        },
        this.refs.seg,
        this.refs.head,
        this.refs.hStart,
        this.refs.hEnd
      ));

      const controls = !!this.player?.controls;
      this.refs.cutError = h('span', { class: 'ytb-cut-info' });
      this.refs.clock = controls ? h('span', { class: 'ytb-clock' }) : null;
      this.refs.free = controls
        ? h('button', { class: 'ytb-btn ytb-btn-ghost small', onclick: () => this.toggleFree() })
        : null;
      this.refs.preview = this.player
        ? h('button', { class: 'ytb-btn ytb-btn-ghost small', onclick: () => this.togglePreview() })
        : null;

      const notes = [];
      if (!this.player) notes.push('Prévia indisponível para este vídeo.');
      else if (controls && mode === 'video') {
        notes.push('A prévia aqui é só o áudio. Para ver a imagem, use o botão YT Baixador na página do vídeo.');
      }
      if (mode === 'video') notes.push('Recortar vídeo leva um pouco mais de tempo: o trecho é recodificado para cortar no segundo exato.');

      return h(
        'div',
        { class: 'ytb-cut is-open' },
        toggle,
        h('div', { class: 'ytb-cut-grid' }, field('start', 'Início', '0:00'), field('end', 'Fim', fmt.time(info.duration))),
        timeline,
        h(
          'div',
          { class: 'ytb-cut-foot' },
          h('span', { class: 'ytb-cut-text' }, this.refs.cutError, this.refs.clock),
          h('span', { class: 'ytb-cut-actions' }, this.refs.free, this.refs.preview)
        ),
        notes.length ? h('p', { class: 'ytb-note' }, notes.join(' ')) : null
      );
    }

    renderJobs() {
      if (!this.jobsBox) return;
      const id = this.state.info?.id;
      const list = id
        ? [...this.client.jobs.values()]
            .filter((j) => j.videoId === id && (ACTIVE.has(j.status) || Date.now() - j.createdAt < 30 * 60_000))
            .sort((a, b) => b.createdAt - a.createdAt)
        : [];
      this.jobsBox.replaceChildren(...list.map((j) => YTB.renderJob(j, this.client, { showTitle: false })));
    }
  };

  function qualityName(q) {
    if (q >= 2160) return '4K';
    if (q >= 1440) return '2K';
    if (q >= 1080) return 'Full HD';
    if (q >= 720) return 'HD';
    return 'SD';
  }

  YTB.css = `
.ytb-root {
  --bg: #fff; --fg: #0f0f0f; --muted: #606060; --line: rgba(0,0,0,.1); --chip: rgba(0,0,0,.05);
  --chip-hover: rgba(0,0,0,.1); --accent: #e1002d; --accent-fg: #fff; --on: #0f0f0f; --on-fg: #fff;
  --ok: #1a7f37; --bad: #c5221f;
  font: 14px/1.4 Roboto, "Segoe UI", Arial, sans-serif; color: var(--fg);
}
.ytb-root.ytb-dark {
  --bg: #212121; --fg: #f1f1f1; --muted: #aaa; --line: rgba(255,255,255,.12); --chip: rgba(255,255,255,.1);
  --chip-hover: rgba(255,255,255,.18); --on: #f1f1f1; --on-fg: #0f0f0f; --ok: #3fb950; --bad: #ff6b6b;
}
.ytb-root *, .ytb-root *::before, .ytb-root *::after { box-sizing: border-box; }
.ytb-root button { font: inherit; color: inherit; cursor: pointer; border: 0; background: none; }
.ytb-root button:disabled { cursor: default; opacity: .45; }
.ytb-root button:focus-visible, .ytb-root input:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.ytb-panel { display: flex; flex-direction: column; gap: 14px; }
.ytb-head { display: flex; align-items: center; justify-content: space-between; }
.ytb-brand { display: flex; align-items: center; gap: 8px; font-weight: 700; font-size: 15px; }
.ytb-logo { display: grid; place-items: center; width: 26px; height: 26px; border-radius: 7px; background: var(--accent); color: #fff; }
.ytb-icon-btn { display: grid; place-items: center; width: 36px; height: 36px; border-radius: 50%; flex: none; }
.ytb-icon-btn:hover { background: var(--chip-hover); }
.ytb-video { display: flex; gap: 12px; align-items: center; }
.ytb-thumb { width: 120px; aspect-ratio: 16/9; object-fit: cover; border-radius: 8px; flex: none; background: var(--chip); }
.ytb-video-text { min-width: 0; }
.ytb-title { font-weight: 600; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
.ytb-sub { color: var(--muted); font-size: 12.5px; margin-top: 2px; }
.ytb-ready { display: flex; flex-direction: column; gap: 12px; }
.ytb-tabs { display: grid; grid-template-columns: 1fr 1fr; background: var(--chip); border-radius: 10px; padding: 3px; }
.ytb-tab { padding: 8px; border-radius: 8px; font-weight: 500; }
.ytb-tab.is-on { background: var(--on); color: var(--on-fg); }
.ytb-opts { display: grid; grid-template-columns: repeat(auto-fill, minmax(92px, 1fr)); gap: 8px; }
.ytb-opt { display: flex; flex-direction: column; align-items: flex-start; gap: 0; padding: 7px 10px; border-radius: 10px;
  background: var(--chip); border: 1.5px solid transparent !important; text-align: left; }
.ytb-opt:hover { background: var(--chip-hover); }
.ytb-opt.is-on { border-color: var(--fg) !important; background: var(--bg); }
.ytb-opt-name { font-weight: 600; }
.ytb-opt-sub, .ytb-opt-size { font-size: 11.5px; color: var(--muted); line-height: 1.35; }
.ytb-opt-size:empty { display: none; }
.ytb-note { margin: -2px 2px 0; font-size: 12.5px; color: var(--muted); }
.ytb-btn { display: inline-flex; align-items: center; justify-content: center; gap: 8px; height: 40px; padding: 0 18px;
  border-radius: 20px; font-weight: 600; }
.ytb-btn.small { height: 32px; padding: 0 14px; font-size: 13px; flex: none; }
.ytb-btn-primary { background: var(--accent) !important; color: var(--accent-fg) !important; }
.ytb-btn-primary:hover:not(:disabled) { filter: brightness(1.08); }
.ytb-btn-ghost { background: var(--chip) !important; }
.ytb-btn-ghost:hover:not(:disabled) { background: var(--chip-hover) !important; }
.ytb-cut { display: flex; flex-direction: column; gap: 10px; }
.ytb-cut.is-open { padding: 10px 12px 12px; border-radius: 12px; background: var(--chip); }
.ytb-cut-toggle { display: flex; align-items: center; gap: 8px; font-weight: 500; text-align: left; padding: 2px 0; }
.ytb-switch { margin-left: auto; width: 34px; height: 20px; border-radius: 10px; background: var(--chip-hover); position: relative; flex: none; transition: background .15s; }
.ytb-switch::after { content: ""; position: absolute; top: 3px; left: 3px; width: 14px; height: 14px; border-radius: 50%; background: var(--bg); box-shadow: 0 1px 2px rgba(0,0,0,.3); transition: transform .15s; }
.ytb-cut-toggle.is-on .ytb-switch { background: var(--accent); }
.ytb-cut-toggle.is-on .ytb-switch::after { transform: translateX(14px); background: #fff; }
.ytb-cut-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.ytb-field { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.ytb-field-label { font-size: 12px; color: var(--muted); }
.ytb-field-row { display: flex; gap: 6px; }
.ytb-time { flex: 1; min-width: 0; height: 34px; padding: 0 10px; border-radius: 8px; border: 1.5px solid var(--line);
  background: var(--bg); color: var(--fg); font: 500 15px/1 Roboto, "Segoe UI", Arial, sans-serif; font-variant-numeric: tabular-nums; }
.ytb-time:focus { border-color: var(--accent); outline: none; }
.ytb-mini-btn { display: inline-flex; align-items: center; gap: 4px; padding: 0 9px; height: 34px; border-radius: 8px;
  background: var(--bg) !important; border: 1.5px solid var(--line) !important; font-size: 12.5px !important; flex: none; }
.ytb-mini-btn:hover { border-color: var(--fg) !important; }
.ytb-timeline { position: relative; height: 8px; margin: 6px 9px; border-radius: 4px; background: var(--chip-hover); touch-action: none; }
.ytb-handle { position: absolute; top: 50%; width: 18px; height: 18px; margin-left: -9px; transform: translateY(-50%);
  border-radius: 50%; background: #fff; border: 3px solid var(--accent); box-shadow: 0 1px 4px rgba(0,0,0,.35);
  cursor: ew-resize; touch-action: none; z-index: 2; }
.ytb-handle:hover, .ytb-handle:focus-visible { transform: translateY(-50%) scale(1.15); outline: none; box-shadow: 0 0 0 4px rgba(225,0,45,.25); }
.ytb-extras { margin-top: -4px; text-align: center; }
.ytb-timeline.is-clickable { cursor: pointer; }
.ytb-seg { position: absolute; top: 0; bottom: 0; background: var(--accent); border-radius: 4px; }
.ytb-head-mark { position: absolute; top: -4px; width: 3px; height: 16px; margin-left: -1.5px; border-radius: 2px; background: var(--fg); pointer-events: none; }
.ytb-cut-foot { display: flex; align-items: center; justify-content: space-between; gap: 10px; min-height: 32px; }
.ytb-cut-text { display: flex; flex-direction: column; min-width: 0; }
.ytb-cut-actions { display: flex; gap: 6px; flex: none; }
.ytb-clock { font-size: 12.5px; font-variant-numeric: tabular-nums; color: var(--fg); }
.ytb-cut-info { font-size: 12.5px; color: var(--muted); }
.ytb-cut-info.is-bad { color: var(--bad); font-weight: 500; }
.ytb-loading { display: flex; align-items: center; gap: 12px; color: var(--muted); padding: 28px 4px; }
.ytb-spinner { width: 22px; height: 22px; border-radius: 50%; border: 3px solid var(--chip-hover); border-top-color: var(--accent);
  animation: ytb-spin .8s linear infinite; }
@keyframes ytb-spin { to { transform: rotate(360deg); } }
.ytb-error { display: flex; flex-direction: column; gap: 10px; align-items: flex-start; }
.ytb-error p { margin: 0; color: var(--bad); font-weight: 500; }
.ytb-steps { margin: 0; padding-left: 20px; color: var(--muted); }
.ytb-jobs { display: flex; flex-direction: column; gap: 8px; }
.ytb-jobs:empty { display: none; }
.ytb-job { display: flex; align-items: center; gap: 10px; padding: 10px 6px 10px 12px; border-radius: 10px; background: var(--chip); }
.ytb-job-thumb { width: 64px; aspect-ratio: 16/9; object-fit: cover; border-radius: 6px; flex: none; }
.ytb-job-body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 5px; }
.ytb-job-title { font-weight: 500; font-size: 13px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.ytb-job-meta { display: flex; align-items: center; gap: 8px; font-size: 12.5px; min-width: 0; }
.ytb-job-status { color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.ytb-job.is-done .ytb-job-status { color: var(--ok); font-weight: 500; }
.ytb-job.is-error .ytb-job-status { color: var(--bad); white-space: normal; }
.ytb-tag { flex: none; font-size: 11px; font-weight: 700; padding: 1px 7px; border-radius: 6px; background: var(--chip-hover); }
.ytb-bar { height: 4px; border-radius: 2px; background: var(--chip-hover); overflow: hidden; }
.ytb-bar-fill { height: 100%; background: var(--accent); border-radius: 2px; transition: width .4s ease; }
.ytb-job.is-done .ytb-bar-fill { background: var(--ok); }
.ytb-bar.is-indeterminate .ytb-bar-fill { width: 35% !important; animation: ytb-slide 1.1s ease-in-out infinite; }
@keyframes ytb-slide { from { transform: translateX(-100%); } to { transform: translateX(290%); } }
`;
})();
