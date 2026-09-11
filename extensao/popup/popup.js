// YT Baixador: popup da barra de ferramentas.

const client = YTB.createClient();
const $ = (id) => document.getElementById(id);
const app = $('app');

const style = document.createElement('style');
style.textContent = YTB.css;
document.head.append(style);

const darkQuery = matchMedia('(prefers-color-scheme: dark)');
const applyTheme = () => app.classList.toggle('ytb-dark', darkQuery.matches);
applyTheme();
darkQuery.addEventListener('change', applyTheme);

$('logo').append(YTB.icon('download', 16));

// Prévia do recorte no popup: toca o áudio direto do YouTube (sem baixar o arquivo).
function streamPlayer(url) {
  const audio = new Audio();
  audio.preload = 'metadata';
  audio.src = url;
  return {
    ...YTB.mediaPlayer(() => audio),
    controls: true,
    paused: () => audio.paused,
    play: () => audio.play().catch(() => {}),
    pause: () => audio.pause(),
    destroy() {
      audio.pause();
      audio.removeAttribute('src');
      audio.load();
    },
  };
}

const panel = new YTB.Panel($('panel'), {
  client,
  embedded: true,
  makePlayer: (info) => (info.previewUrl ? streamPlayer(info.previewUrl) : null),
});
const isVideoUrl = (u) => /(youtube\.com\/(watch\?|shorts\/|live\/|embed\/)|youtu\.be\/)/.test(u || '');

function showHint() {
  $('panel').replaceChildren(
    YTB.h('p', { class: 'hint' }, 'Abra um vídeo do YouTube e clique no botão ', YTB.h('b', {}, 'Baixar'),
      ' embaixo do player, ou cole o link acima.')
  );
}

function openUrl(url) {
  url = (url || '').trim();
  if (!/^https?:\/\//i.test(url)) return showHint();
  panel.load(url);
}

$('url-form').addEventListener('submit', (e) => {
  e.preventDefault();
  openUrl($('url').value);
});
$('url').addEventListener('paste', () => setTimeout(() => openUrl($('url').value), 0));

// ------------------------------------------------------------ histórico

client.subscribe((jobs) => {
  const list = [...jobs.values()].sort((a, b) => b.createdAt - a.createdAt);
  $('history').hidden = !list.length;
  $('history-list').replaceChildren(...list.map((j) => YTB.renderJob(j, client)));
});
$('clear').addEventListener('click', () => client.request('clearHistory'));

// ------------------------------------------------------------ configurações e status

$('toggle-settings').addEventListener('click', () => ($('settings').hidden = !$('settings').hidden));

function banner(html, kind = 'warn') {
  $('banner').className = `banner is-${kind}`;
  $('banner').replaceChildren(...html);
  $('banner').hidden = false;
}

function engineText(host) {
  const parts = [host.ytdlp ? `yt-dlp ${host.ytdlp}` : 'yt-dlp não encontrado'];
  parts.push(host.ffmpeg ? 'FFmpeg ok' : 'sem FFmpeg');
  parts.push(host.js ? `${host.js === 'node' ? 'Node.js' : 'Deno'} ok` : 'sem Node/Deno');
  return parts.join(' · ');
}

async function loadStatus() {
  try {
    const { host, settings } = await client.request('status');
    $('folder-path').textContent = settings.folder || host.defaultFolder;
    $('folder-path').title = $('folder-path').textContent;
    $('prefer-h264').checked = settings.preferH264;
    $('notify').checked = settings.notify;
    $('engine').textContent = engineText(host);
    if (!host.ytdlp || !host.ffmpeg) {
      banner(['Falta instalar parte do programa auxiliar. Rode o ', YTB.h('b', {}, 'instalar.bat'), ' de novo.']);
    } else if (!host.js) {
      banner(['Instale o Node.js ou o Deno: o YouTube exige um deles para liberar os downloads.']);
    }
  } catch (e) {
    $('engine').textContent = 'indisponível';
    if (e.code === 'NO_HOST') {
      banner([
        YTB.h('b', {}, 'Falta um passo: '),
        'rode o ', YTB.h('b', {}, 'instalar.bat'), ' da pasta do projeto e depois reinicie o navegador.',
      ], 'bad');
    } else {
      banner([e.message], 'bad');
    }
  }
}

$('pick-folder').addEventListener('click', async (e) => {
  e.preventDefault();
  try {
    const { folder } = await client.request('pickFolder');
    if (folder) $('folder-path').textContent = $('folder-path').title = folder;
  } catch (err) {
    alert(err.message);
  }
});

$('prefer-h264').addEventListener('change', (e) =>
  client.request('setSettings', { patch: { preferH264: e.target.checked } }));
$('notify').addEventListener('change', (e) =>
  client.request('setSettings', { patch: { notify: e.target.checked } }));

$('update-engine').addEventListener('click', async () => {
  const btn = $('update-engine');
  btn.disabled = true;
  btn.textContent = 'Atualizando…';
  try {
    const { ytdlp } = await client.request('update');
    $('engine').textContent = `yt-dlp ${ytdlp} (atualizado)`;
  } catch (err) {
    alert(err.message);
  }
  btn.disabled = false;
  btn.textContent = 'Atualizar';
});

// ------------------------------------------------------------ atualização da extensão

const currentVersion = chrome.runtime.getManifest().version;
$('version').textContent = `v${currentVersion}`;

async function showUpdate(force = false) {
  try {
    const u = await client.request('checkUpdate', { force });
    $('ext-version').textContent = u.available ? `v${u.current} · nova versão ${u.latest} disponível` : `v${u.current} · em dia`;
    $('update-text').textContent = `Nova versão ${u.latest} disponível.`;
    $('update').hidden = !u.available;
  } catch {
    $('ext-version').textContent = `v${currentVersion} · não consegui verificar`;
  }
}

$('check-update').addEventListener('click', async () => {
  $('ext-version').textContent = 'verificando…';
  await showUpdate(true);
});

$('update-now').addEventListener('click', async () => {
  const btn = $('update-now');
  btn.disabled = true;
  btn.textContent = 'Atualizando…';
  try {
    const { version } = await client.request('selfUpdate');
    // A extensão recarrega sozinha logo em seguida e o popup fecha.
    $('update-text').textContent = `Atualizado para ${version}! Reabra o popup e recarregue as abas do YouTube.`;
    btn.hidden = true;
  } catch (err) {
    alert(err.message);
    btn.disabled = false;
    btn.textContent = 'Atualizar agora';
  }
});

// ------------------------------------------------------------ crédito

$('credit').addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText('Skymer#9220');
    const text = $('credit-text');
    const old = text.innerHTML;
    text.textContent = 'Discord copiado!';
    setTimeout(() => (text.innerHTML = old), 1500);
  } catch {}
});

// ------------------------------------------------------------ início

(async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (isVideoUrl(tab?.url)) {
    $('url').value = tab.url;
    panel.load(tab.url);
  } else {
    showHint();
  }
  loadStatus();
  showUpdate();
})();
