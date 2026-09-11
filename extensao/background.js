const HOST_NAME = 'com.ytbaixador.host';
const HOST_IDLE_MS = 60_000;
const INFO_TTL_MS = 10 * 60_000;
const MAX_HISTORY = 30;
const ACTIVE = new Set(['starting', 'downloading', 'processing']);

const DEFAULT_SETTINGS = {
  folder: '',
  preferH264: true,
  notify: true,
  lastMode: 'video',
  lastQuality: 1080,
  lastAudio: 'm4a',
};

let hostPort = null;
let hostSeq = 0;
let idleTimer = null;
const pending = new Map();
const uiPorts = new Set();
const infoCache = new Map();
let jobs = {};

const ready = chrome.storage.local.get('jobs').then(({ jobs: saved }) => {
  jobs = saved || {};
  for (const job of Object.values(jobs)) {
    if (ACTIVE.has(job.status)) Object.assign(job, { status: 'error', error: 'Interrompido (o navegador foi fechado).' });
  }
  updateBadge();
});

class HostError extends Error {
  constructor(message, code) {
    super(message);
    this.code = code;
  }
}

function translateDisconnect(message = '') {
  if (/not found/i.test(message)) {
    return new HostError('O programa auxiliar não está instalado. Rode o arquivo instalar.bat da pasta do projeto.', 'NO_HOST');
  }
  if (/forbidden/i.test(message)) {
    return new HostError('O programa auxiliar foi instalado para outra extensão. Rode o instalar.bat de novo.', 'NO_HOST');
  }
  if (/exited/i.test(message)) {
    return new HostError('O programa auxiliar fechou sozinho. Rode o instalar.bat de novo e veja o host.log.', 'HOST_CRASH');
  }
  return new HostError(message || 'Conexão com o programa auxiliar perdida.', 'HOST_CRASH');
}

function connectHost() {
  if (hostPort) return hostPort;
  hostPort = chrome.runtime.connectNative(HOST_NAME);
  hostPort.onMessage.addListener(onHostMessage);
  hostPort.onDisconnect.addListener(() => {
    const err = translateDisconnect(chrome.runtime.lastError?.message);
    hostPort = null;
    for (const { reject, timer } of pending.values()) {
      clearTimeout(timer);
      reject(err);
    }
    pending.clear();
    for (const job of Object.values(jobs)) {
      if (ACTIVE.has(job.status)) patchJob(job.id, { status: 'error', error: err.message });
    }
  });
  return hostPort;
}

function callHost(cmd, payload = {}, timeoutMs = 150_000) {
  return new Promise((resolve, reject) => {
    let port;
    try {
      port = connectHost();
    } catch (e) {
      return reject(translateDisconnect(e.message));
    }
    const id = ++hostSeq;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new HostError('O programa auxiliar demorou demais para responder.'));
    }, timeoutMs);
    pending.set(id, { resolve, reject, timer });
    port.postMessage({ id, cmd, ...payload });
    scheduleIdle();
  });
}

function onHostMessage(msg) {
  if (msg.id != null) {
    const req = pending.get(msg.id);
    if (!req) return;
    pending.delete(msg.id);
    clearTimeout(req.timer);
    msg.ok ? req.resolve(msg.result) : req.reject(new HostError(msg.error));
    scheduleIdle();
    return;
  }
  const job = jobs[msg.jobId];
  if (!job) return;
  switch (msg.event) {
    case 'progress':
      if (msg.stage === 'process') {
        patchJob(job.id, { status: 'processing', stageLabel: msg.label, percent: null, speed: null, eta: null });
      } else {
        patchJob(job.id, {
          status: 'downloading',
          stream: msg.stream,
          percent: msg.percent,
          speed: msg.speed,
          eta: msg.eta,
          downloaded: msg.downloaded,
          total: msg.total,
        });
      }
      break;
    case 'done':
      patchJob(job.id, { status: 'done', percent: 100, filepath: msg.filepath, filename: msg.filename, size: msg.size });
      notify(job.id, 'Download concluído', msg.filename);
      break;
    case 'error':
      patchJob(job.id, { status: 'error', error: msg.error, detail: msg.detail });
      notify(job.id, 'Falha no download', `${job.title}\n${msg.error}`);
      break;
    case 'cancelled':
      patchJob(job.id, { status: 'cancelled' });
      break;
  }
  scheduleIdle();
}

function scheduleIdle() {
  clearTimeout(idleTimer);
  idleTimer = setTimeout(() => {
    const busy = pending.size > 0 || Object.values(jobs).some((j) => ACTIVE.has(j.status));
    if (busy) return scheduleIdle();
    hostPort?.disconnect();
    hostPort = null;
  }, HOST_IDLE_MS);
}

let saveTimer = null;
function persistJobs() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    const list = Object.values(jobs).sort((a, b) => b.createdAt - a.createdAt).slice(0, MAX_HISTORY);
    jobs = Object.fromEntries(list.map((j) => [j.id, j]));
    chrome.storage.local.set({ jobs });
  }, 800);
}

function patchJob(id, patch) {
  const job = jobs[id];
  if (!job) return;
  Object.assign(job, patch);
  broadcast({ type: 'job', job });
  persistJobs();
  if ('status' in patch) updateBadge();
}

function updateBadge() {
  const active = Object.values(jobs).filter((j) => ACTIVE.has(j.status)).length;
  chrome.action.setBadgeText({ text: active ? String(active) : '' });
  chrome.action.setBadgeBackgroundColor({ color: '#e1002d' });
}

async function notify(jobId, title, message) {
  const { notify: enabled } = await getSettings();
  if (!enabled) return;
  chrome.notifications.create(jobId, { type: 'basic', iconUrl: 'icons/128.png', title, message });
}

chrome.notifications.onClicked.addListener((jobId) => {
  const job = jobs[jobId];
  if (job?.filepath) callHost('reveal', { path: job.filepath, folder: job.folder }).catch(() => {});
  chrome.notifications.clear(jobId);
});

async function getSettings() {
  const { settings } = await chrome.storage.local.get('settings');
  return { ...DEFAULT_SETTINGS, ...settings };
}

function youtubeId(url) {
  const m = String(url).match(/(?:youtube\.com\/(?:watch\?(?:.*&)?v=|shorts\/|embed\/|live\/)|youtu\.be\/)([\w-]{11})/);
  return m ? m[1] : null;
}

function canonicalUrl(url) {
  const id = youtubeId(url);
  return id ? `https://www.youtube.com/watch?v=${id}` : String(url).trim();
}

async function getInfo(url) {
  const key = canonicalUrl(url);
  const hit = infoCache.get(key);
  if (hit && Date.now() - hit.at < INFO_TTL_MS) return hit.promise;
  const promise = callHost('info', { url: key });
  infoCache.set(key, { at: Date.now(), promise });
  promise.catch(() => infoCache.delete(key));
  return promise;
}

const REPO = 'SkymerLight/Baixador-YT';
const UPDATE_TTL_MS = 6 * 60 * 60_000;

function isNewer(a, b) {
  const pa = String(a).split('.').map(Number);
  const pb = String(b).split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const diff = (pa[i] || 0) - (pb[i] || 0);
    if (diff) return diff > 0;
  }
  return false;
}

async function checkUpdate(force = false) {
  const current = chrome.runtime.getManifest().version;
  let { updateCheck } = await chrome.storage.local.get('updateCheck');
  if (force || !updateCheck || Date.now() - updateCheck.at > UPDATE_TTL_MS) {
    const res = await fetch(`https://raw.githubusercontent.com/${REPO}/main/extensao/manifest.json`, { cache: 'no-store' });
    if (!res.ok) throw new HostError('Não consegui consultar o GitHub.');
    updateCheck = { at: Date.now(), latest: (await res.json()).version };
    await chrome.storage.local.set({ updateCheck });
  }
  return { current, latest: updateCheck.latest, available: isNewer(updateCheck.latest, current) };
}

const actions = {
  async status() {
    const [host, settings] = await Promise.all([callHost('ping', {}, 60_000), getSettings()]);
    return { host, settings };
  },

  info: ({ url }) => getInfo(url),

  async download({ url, mode, quality, audioFormat, section, meta = {} }) {
    const settings = await getSettings();
    const target = canonicalUrl(url);
    const { jobId } = await callHost('download', {
      url: target,
      mode,
      quality,
      audioFormat,
      section,
      folder: settings.folder || undefined,
      preferH264: settings.preferH264,
    });
    const job = {
      id: jobId,
      url: target,
      videoId: youtubeId(target),
      title: meta.title || target,
      thumbnail: meta.thumbnail || null,
      mode,
      label: meta.label || '',
      section: section || null,
      folder: settings.folder || '',
      status: 'starting',
      percent: 0,
      createdAt: Date.now(),
    };
    jobs[jobId] = job;
    broadcast({ type: 'job', job });
    persistJobs();
    updateBadge();
    await chrome.storage.local.set({
      settings: { ...settings, lastMode: mode, ...(mode === 'audio' ? { lastAudio: audioFormat } : { lastQuality: quality }) },
    });
    return job;
  },

  cancel: ({ jobId }) => callHost('cancel', { jobId }),

  async reveal({ jobId }) {
    const job = jobs[jobId];
    if (!job?.filepath) throw new HostError('Arquivo não encontrado.');
    return callHost('reveal', { path: job.filepath, folder: job.folder });
  },

  async pickFolder() {
    const settings = await getSettings();
    const { folder } = await callHost('pickFolder', { current: settings.folder }, 10 * 60_000);
    if (folder) await chrome.storage.local.set({ settings: { ...settings, folder } });
    return { folder };
  },

  update: () => callHost('update', {}, 10 * 60_000),

  checkUpdate: ({ force }) => checkUpdate(force),

  async selfUpdate() {
    const result = await callHost('selfUpdate', {}, 5 * 60_000);
    await chrome.storage.local.remove('updateCheck');
    setTimeout(() => chrome.runtime.reload(), 800);
    return result;
  },

  getSettings,

  async setSettings({ patch }) {
    const settings = { ...(await getSettings()), ...patch };
    await chrome.storage.local.set({ settings });
    return settings;
  },

  clearHistory() {
    for (const job of Object.values(jobs)) if (!ACTIVE.has(job.status)) delete jobs[job.id];
    broadcast({ type: 'jobs', jobs: Object.values(jobs) });
    persistJobs();
  },
};

function broadcast(msg) {
  for (const port of uiPorts) {
    try {
      port.postMessage(msg);
    } catch {
      uiPorts.delete(port);
    }
  }
}

chrome.runtime.onConnect.addListener(async (port) => {
  if (port.name !== 'ytb-ui') return;
  uiPorts.add(port);
  port.onDisconnect.addListener(() => uiPorts.delete(port));
  port.onMessage.addListener(async (msg) => {
    await ready;
    const fn = actions[msg.action];
    try {
      if (!fn) throw new HostError(`Ação desconhecida: ${msg.action}`);
      const result = await fn(msg);
      port.postMessage({ type: 'reply', reqId: msg.reqId, ok: true, result });
    } catch (e) {
      port.postMessage({ type: 'reply', reqId: msg.reqId, ok: false, error: e.message, code: e.code });
    }
  });
  await ready;
  port.postMessage({ type: 'jobs', jobs: Object.values(jobs) });
});
