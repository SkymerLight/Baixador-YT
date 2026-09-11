"""YT Baixador: host nativo.

O navegador inicia este processo quando a extensão precisa dele (Native Messaging).
Ele recebe comandos em JSON pela entrada padrão e usa o yt-dlp para ler as
informações do vídeo e fazer os downloads. Só a extensão registrada no manifesto
do host consegue conversar com ele.
"""

import glob
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import uuid
import zipfile

VERSION = "1.2.0"
REPO = "SkymerLight/Baixador-YT"
BRANCH = "main"
APP_DIR = os.path.dirname(os.path.abspath(__file__))
TMP_DIR = os.path.join(APP_DIR, "tmp")
CACHE_DIR = os.path.join(APP_DIR, "cache")
LOG_FILE = os.path.join(APP_DIR, "host.log")
CONFIG_FILE = os.path.join(APP_DIR, "config.json")
PY = sys.executable
NO_WINDOW = 0x08000000 if sys.platform == "win32" else 0


def _mp3(kbps):
    return ["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", f"{kbps}K"]


# formato -> (argumentos do yt-dlp, dá para embutir a capa?)
AUDIO_FORMATS = {
    "m4a": (["-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a"], True),
    "opus": (["-f", "ba[acodec=opus]/ba/b", "-x", "--audio-format", "opus"], True),
    "mp3-320": (_mp3(320), True),
    "mp3-192": (_mp3(192), True),
    "mp3-128": (_mp3(128), True),
    "mp3-64": (_mp3(64), True),
    "wav": (["-f", "ba/b", "-x", "--audio-format", "wav"], False),
    "flac": (["-f", "ba/b", "-x", "--audio-format", "flac"], True),
}

# Arquivos da raiz do projeto que a atualização automática também renova.
ROOT_FILES = ["instalar.bat", "instalar.ps1", "desinstalar.bat", "desinstalar.ps1", "LEIAME.md"]
HOST_FILES = ["host.py", "host.bat", "testar.py"]

PP_LABELS = [
    ("merger", "Juntando vídeo e áudio"),
    ("extractaudio", "Convertendo áudio"),
    ("thumbnailsconvertor", "Preparando capa"),
    ("embedthumbnail", "Adicionando capa"),
    ("metadata", "Gravando informações"),
    ("movefiles", "Finalizando"),
]

ERROR_HINTS = [
    ("not a bot", "O YouTube pediu verificação anti-robô. Espere um pouco e tente de novo, ou atualize o yt-dlp."),
    ("confirm your age", "Vídeo com restrição de idade: o YouTube exige login para ele."),
    ("private video", "Esse vídeo é privado."),
    ("members-only", "Vídeo exclusivo para membros do canal."),
    ("video unavailable", "Vídeo indisponível."),
    ("is not a valid url", "Link inválido."),
    ("unsupported url", "Esse link não é suportado."),
    ("requested format is not available", "Essa qualidade não está disponível para este vídeo."),
    ("ffmpeg", "FFmpeg não encontrado. Rode o instalar.bat de novo."),
    ("ffprobe", "FFmpeg não encontrado. Rode o instalar.bat de novo."),
    ("javascript runtime", "O yt-dlp precisa do Node.js ou Deno instalado."),
    ("n challenge", "Falha ao decifrar o vídeo. Atualize o yt-dlp."),
    ("http error 403", "O YouTube bloqueou o download (erro 403). Atualize o yt-dlp e tente de novo."),
    ("no space left", "Sem espaço no disco."),
    ("no module named yt_dlp", "yt-dlp não instalado. Rode o instalar.bat."),
]


class HostError(Exception):
    pass


# ---------------------------------------------------------------- utilidades

def log(*parts):
    try:
        if os.path.exists(LOG_FILE) and os.path.getsize(LOG_FILE) > 1_000_000:
            os.replace(LOG_FILE, LOG_FILE + ".old")
        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + " ".join(str(p) for p in parts) + "\n")
    except OSError:
        pass


def refresh_path():
    """O navegador pode ter sido aberto antes do FFmpeg ser instalado; relê o PATH do registro."""
    if sys.platform != "win32":
        return
    import winreg

    extra = []
    keys = (
        (winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
        (winreg.HKEY_CURRENT_USER, "Environment"),
    )
    for root, sub in keys:
        try:
            with winreg.OpenKey(root, sub) as k:
                extra += os.path.expandvars(winreg.QueryValueEx(k, "Path")[0]).split(";")
        except OSError:
            pass
    seen, merged = set(), []
    for p in os.environ.get("PATH", "").split(";") + extra:
        p = p.strip()
        key = p.lower().rstrip("\\")
        if p and key not in seen:
            seen.add(key)
            merged.append(p)
    os.environ["PATH"] = ";".join(merged)


def find_ffmpeg():
    found = shutil.which("ffmpeg")
    if found:
        return found
    base = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Microsoft", "WinGet", "Packages")
    hits = glob.glob(os.path.join(base, "*FFmpeg*", "*", "bin", "ffmpeg.exe"))
    return hits[0] if hits else None


def find_js_runtime():
    for name in ("deno", "node"):
        found = shutil.which(name)
        if found:
            return name, found
    return None, None


def default_folder():
    if sys.platform == "win32":
        import winreg

        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                                r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders") as k:
                val = winreg.QueryValueEx(k, "{374DE290-123F-4565-9164-39C4925E467B}")[0]
                return os.path.expandvars(val)
        except OSError:
            pass
    return os.path.join(os.path.expanduser("~"), "Downloads")


def child_env():
    env = dict(os.environ)
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    return env


def run(args, timeout, text=False):
    # stdin=DEVNULL é obrigatório: no Windows, um filho que herda o pipe de entrada
    # trava enquanto a thread principal está lendo mensagens dele.
    return subprocess.run(args, capture_output=True, text=text, stdin=subprocess.DEVNULL,
                          creationflags=NO_WINDOW, env=child_env(), timeout=timeout)


def validate_url(url):
    url = (url or "").strip()
    if not re.match(r"^https?://[^\s/]+\.[^\s/]+", url, re.I):
        raise HostError("Link inválido.")
    return url


def friendly_error(stderr):
    lines = [l.strip() for l in (stderr or "").splitlines() if l.strip()]
    errors = [l for l in lines if l.startswith("ERROR:")] or lines[-1:]
    raw = errors[-1] if errors else "Erro desconhecido."
    low = raw.lower()
    for needle, msg in ERROR_HINTS:
        if needle in low:
            return msg, raw
    return raw.replace("ERROR: ", "", 1), raw


def to_num(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def base_args():
    args = [PY, "-m", "yt_dlp", "--ignore-config", "--no-playlist", "--cache-dir", CACHE_DIR]
    ffmpeg = find_ffmpeg()
    if ffmpeg:
        args += ["--ffmpeg-location", ffmpeg]
    name, path = find_js_runtime()
    if name:
        args += ["--js-runtimes", f"{name}:{path}"]
    return args


_ytdlp_version = None


def ytdlp_version(refresh=False):
    global _ytdlp_version
    if _ytdlp_version is None or refresh:
        p = run([PY, "-m", "yt_dlp", "--version"], timeout=60, text=True)
        _ytdlp_version = p.stdout.strip() if p.returncode == 0 else None
    return _ytdlp_version


# ---------------------------------------------------------------- mensagens

if sys.platform == "win32":
    import msvcrt

    msvcrt.setmode(sys.stdin.fileno(), os.O_BINARY)
    msvcrt.setmode(sys.stdout.fileno(), os.O_BINARY)

_out_lock = threading.Lock()


def send(msg):
    data = json.dumps(msg, ensure_ascii=False).encode("utf-8")
    with _out_lock:
        sys.stdout.buffer.write(struct.pack("<I", len(data)))
        sys.stdout.buffer.write(data)
        sys.stdout.buffer.flush()


def read_message():
    raw = sys.stdin.buffer.read(4)
    if len(raw) < 4:
        return None
    size = struct.unpack("<I", raw)[0]
    return json.loads(sys.stdin.buffer.read(size).decode("utf-8"))


# ---------------------------------------------------------------- comandos

def cmd_ping(_msg):
    js_name, _ = find_js_runtime()
    can_update, reason = self_update_status()
    return {
        "host": VERSION,
        "ytdlp": ytdlp_version(),
        "ffmpeg": bool(find_ffmpeg()),
        "js": js_name,
        "defaultFolder": default_folder(),
        "selfUpdate": {"ok": can_update, "reason": reason},
    }


def parse_section(section):
    """{"start": 90, "end": 165} -> (90.0, 165.0), ou None se não for recorte."""
    if not section:
        return None
    try:
        start, end = float(section["start"]), float(section["end"])
    except (KeyError, TypeError, ValueError):
        raise HostError("Trecho inválido.")
    if start < 0 or end <= start:
        raise HostError("O fim do trecho precisa ser depois do início.")
    return start, end


def time_label(seconds):
    s = int(round(seconds))
    h, m, sec = s // 3600, s % 3600 // 60, s % 60
    return f"{h}h{m:02d}m{sec:02d}s" if h else f"{m}m{sec:02d}s"


def format_size(fmt, duration):
    size = fmt.get("filesize") or fmt.get("filesize_approx")
    if not size and duration and fmt.get("tbr"):
        size = fmt["tbr"] * 1000 / 8 * duration
    return size or 0


def cmd_info(msg):
    url = validate_url(msg.get("url"))
    p = run(base_args() + ["-J", "--", url], timeout=120)
    if p.returncode != 0:
        text, raw = friendly_error(p.stderr.decode("utf-8", "replace"))
        log("info falhou:", url, raw)
        raise HostError(text)
    data = json.loads(p.stdout.decode("utf-8"))
    if data.get("_type") == "playlist":
        raise HostError("Isso é uma playlist. Abra um vídeo específico.")
    if data.get("is_live"):
        raise HostError("Transmissões ao vivo não são suportadas.")

    duration = data.get("duration") or 0
    formats = [f for f in data.get("formats") or [] if not f.get("has_drm")]
    audios = [f for f in formats if f.get("vcodec") == "none" and f.get("acodec") not in (None, "none")]
    bitrate = lambda f: f.get("abr") or f.get("tbr") or 0  # noqa: E731
    best_audio = max(audios, key=bitrate, default=None)
    audio_size = format_size(best_audio, duration) if best_audio else 0
    best_m4a = max((f for f in audios if (f.get("acodec") or "").startswith("mp4a")), key=bitrate, default=None)
    best_opus = max((f for f in audios if f.get("acodec") == "opus"), key=bitrate, default=None)
    audio_sizes = {
        "m4a": format_size(best_m4a, duration) if best_m4a else audio_size,
        "opus": format_size(best_opus, duration) if best_opus else audio_size,
    }
    # Link direto do áudio para a prévia do recorte no popup (o navegador toca sem baixar o arquivo).
    playable = [f for f in sorted(audios, key=bitrate, reverse=True) if f.get("protocol") == "https" and f.get("url")]
    preview = next((f for f in playable if (f.get("acodec") or "").startswith("mp4a")), None) or next(iter(playable), None)

    qualities = {}
    for f in formats:
        if f.get("vcodec") in (None, "none") or not f.get("height"):
            continue
        # A "qualidade" é o menor lado: um Short 1080x1920 é 1080p.
        q = min(f["height"], f.get("width") or f["height"])
        entry = qualities.setdefault(q, {"q": q, "fps": 0, "size": 0})
        entry["fps"] = max(entry["fps"], round(f.get("fps") or 0))
        entry["size"] = max(entry["size"], format_size(f, duration))

    video = []
    for q in sorted(qualities, reverse=True):
        e = qualities[q]
        label = f"{q}p" + ("60" if e["fps"] > 30 else "")
        size = e["size"] + audio_size if e["size"] else 0
        video.append({"q": q, "label": label, "fps": e["fps"], "size": size})

    return {
        "id": data.get("id"),
        "title": data.get("title") or "Sem título",
        "uploader": data.get("uploader") or data.get("channel") or "",
        "duration": duration,
        "thumbnail": data.get("thumbnail"),
        "url": data.get("webpage_url") or url,
        "video": video,
        "audioSize": audio_size,
        "audioSizes": audio_sizes,
        "previewUrl": preview["url"] if preview else None,
        "hasAudio": bool(best_audio) or any(f.get("acodec") not in (None, "none") for f in formats),
    }


class Job:
    def __init__(self, job_id, args, tmp):
        self.id = job_id
        self.args = args
        self.tmp = tmp
        self.proc = None
        self.cancelled = False
        self.filepath = None


JOBS = {}
_jobs_lock = threading.Lock()


def cmd_download(msg):
    url = validate_url(msg.get("url"))
    if not find_ffmpeg():
        raise HostError("FFmpeg não encontrado. Rode o instalar.bat de novo.")
    folder = msg.get("folder") or default_folder()
    try:
        os.makedirs(folder, exist_ok=True)
    except OSError:
        raise HostError(f"Não consegui usar a pasta: {folder}")

    job_id = uuid.uuid4().hex[:12]
    tmp = os.path.join(TMP_DIR, job_id)
    args = base_args() + [
        "-P", folder, "-P", f"temp:{tmp}",
        "--no-mtime", "--embed-metadata", "--newline", "--progress", "--progress-delta", "0.5",
        "--progress-template",
        "download:[YTB]%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|"
        "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.vcodec)s",
        "--progress-template", "postprocess:[YTBPP]%(progress.status)s|%(progress.postprocessor)s",
        "--print", "after_move:[YTBFILE]%(filepath)s",
    ]

    # Recorte: o yt-dlp baixa só o trecho pedido, sem precisar do vídeo inteiro.
    section = parse_section(msg.get("section"))
    suffix = f" ({time_label(section[0])} a {time_label(section[1])})" if section else ""
    if section:
        args += ["--download-sections", f"*{section[0]:.3f}-{section[1]:.3f}"]

    if msg.get("mode") == "audio":
        fmt = msg.get("audioFormat") if msg.get("audioFormat") in AUDIO_FORMATS else "m4a"
        fmt_args, cover = AUDIO_FORMATS[fmt]
        args += fmt_args + ["-o", f"%(title).150B{suffix}.%(ext)s"]
        if cover:
            args += ["--embed-thumbnail", "--convert-thumbnails", "jpg"]
        if section and fmt != "m4a":
            # Recortar o áudio Opus (WebM) sem isso gera o dobro da duração; o M4A já corta certo.
            args += ["--force-keyframes-at-cuts"]
    else:
        quality = int(msg.get("quality") or 0)
        sort = [f"res:{quality}"] if quality else []
        if msg.get("preferH264", True):
            sort += ["vcodec:h264", "acodec:aac"]
        label = f"{quality}p" if quality else "%(height)sp"
        args += ["-f", "bv*+ba/b", "--merge-output-format", "mp4",
                 "-o", f"%(title).150B [{label}]{suffix}.%(ext)s"]
        if sort:
            args += ["-S", ",".join(sort)]
        if section:
            # Corta no segundo exato (recodifica só o trecho); sem isso o corte cai no keyframe mais próximo.
            args += ["--force-keyframes-at-cuts"]

    args += ["--", url]
    job = Job(job_id, args, tmp)
    with _jobs_lock:
        JOBS[job_id] = job
    threading.Thread(target=run_job, args=(job,), daemon=True).start()
    return {"jobId": job_id}


def run_job(job):
    log("download:", " ".join(job.args[3:]))
    stderr_lines = []
    try:
        job.proc = subprocess.Popen(job.args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    stdin=subprocess.DEVNULL, creationflags=NO_WINDOW, env=child_env())

        def drain():
            for raw in job.proc.stderr:
                stderr_lines.append(raw.decode("utf-8", "replace"))

        threading.Thread(target=drain, daemon=True).start()

        for raw in job.proc.stdout:
            line = raw.decode("utf-8", "replace").strip()
            if line.startswith("[YTB]"):
                parts = (line[5:].split("|") + [""] * 7)[:7]
                status, done, total, estimate, speed, eta, vcodec = parts
                total_n = to_num(total) or to_num(estimate)
                done_n = to_num(done)
                percent = (done_n / total_n * 100) if done_n is not None and total_n else None
                if status == "finished":
                    percent = 100.0
                send({"event": "progress", "jobId": job.id, "stage": "download",
                      "stream": "audio" if vcodec == "none" else "video",
                      "percent": percent, "speed": to_num(speed), "eta": to_num(eta),
                      "downloaded": done_n, "total": total_n})
            elif line.startswith("[YTBPP]"):
                status, _, name = line[7:].partition("|")
                if status == "started":
                    label = next((l for key, l in PP_LABELS if key in name.lower()), "Processando")
                    send({"event": "progress", "jobId": job.id, "stage": "process", "label": label})
            elif line.startswith("[YTBFILE]"):
                job.filepath = line[9:].strip()

        code = job.proc.wait()
        time.sleep(0.2)
        if job.cancelled:
            send({"event": "cancelled", "jobId": job.id})
        elif code == 0 and job.filepath and os.path.isfile(job.filepath):
            send({"event": "done", "jobId": job.id, "filepath": job.filepath,
                  "filename": os.path.basename(job.filepath), "size": os.path.getsize(job.filepath)})
        else:
            text, raw = friendly_error("".join(stderr_lines))
            log("download falhou:", raw)
            send({"event": "error", "jobId": job.id, "error": text, "detail": raw})
    except Exception as exc:  # noqa: BLE001
        log("erro no job:", repr(exc))
        send({"event": "error", "jobId": job.id, "error": str(exc)})
    finally:
        with _jobs_lock:
            JOBS.pop(job.id, None)
        remove_dir(job.tmp)


def remove_dir(path):
    for _ in range(10):
        shutil.rmtree(path, ignore_errors=True)
        if not os.path.exists(path):
            return
        time.sleep(0.5)


def kill_tree(proc):
    if proc and proc.poll() is None:
        run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], timeout=30)


def cmd_cancel(msg):
    job = JOBS.get(msg.get("jobId"))
    if not job:
        return {"cancelled": False}
    job.cancelled = True
    kill_tree(job.proc)
    return {"cancelled": True}


def cmd_reveal(msg):
    path = os.path.normpath(msg.get("path") or "")
    if os.path.isfile(path):
        subprocess.Popen(f'explorer /select,"{path}"', stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return {"opened": True}
    folder = msg.get("folder") or os.path.dirname(path)
    if folder and os.path.isdir(folder):
        os.startfile(folder)
        return {"opened": True, "missing": True}
    raise HostError("Arquivo não encontrado. Ele foi movido ou apagado?")


def cmd_pick_folder(msg):
    import tkinter as tk
    from tkinter import filedialog

    root = tk.Tk()
    root.withdraw()
    root.attributes("-topmost", True)
    root.update()
    try:
        path = filedialog.askdirectory(parent=root, initialdir=msg.get("current") or default_folder(),
                                       title="Onde salvar os downloads?")
    finally:
        root.destroy()
    return {"folder": os.path.normpath(path) if path else None}


def cmd_update(_msg):
    if JOBS:
        raise HostError("Espere os downloads terminarem antes de atualizar.")
    p = run([PY, "-m", "pip", "install", "-U", "--disable-pip-version-check", "yt-dlp[default]"],
            timeout=600, text=True)
    if p.returncode != 0:
        log("update falhou:", p.stderr)
        raise HostError("Não consegui atualizar o yt-dlp. Veja o host.log.")
    return {"ytdlp": ytdlp_version(refresh=True)}


# ---------------------------------------------------------------- atualização automática

def load_config():
    try:
        with open(CONFIG_FILE, encoding="utf-8-sig") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def self_update_status():
    ext_dir = load_config().get("extensionDir")
    if not ext_dir or not os.path.isfile(os.path.join(ext_dir, "manifest.json")):
        return False, "Rode o instalar.bat de novo para ativar a atualização automática."
    if os.path.exists(os.path.join(os.path.dirname(ext_dir), ".git")):
        return False, "Esta é a cópia de desenvolvimento (repositório git): atualize com git pull."
    return True, ""


def extract_folder(zf, prefix, folder, dest):
    """Extrai a pasta `folder` do zip para `dest`, sem deixar nenhum arquivo escapar de `dest`."""
    base = prefix + folder + "/"
    dest_real = os.path.realpath(dest)
    extracted = set()
    for info in zf.infolist():
        if info.is_dir() or not info.filename.startswith(base):
            continue
        rel = os.path.normpath(info.filename[len(base):])
        target = os.path.realpath(os.path.join(dest, rel))
        if not target.startswith(dest_real + os.sep):
            raise HostError("Pacote de atualização inválido.")
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with zf.open(info) as src, open(target, "wb") as out:
            shutil.copyfileobj(src, out)
        extracted.add(os.path.normcase(rel))
    return extracted


def mirror_folder(src, dest, keep):
    """Copia `src` por cima de `dest` e apaga de `dest` o que não existe mais na versão nova."""
    for dirpath, _, files in os.walk(src):
        for name in files:
            rel = os.path.relpath(os.path.join(dirpath, name), src)
            os.makedirs(os.path.dirname(os.path.join(dest, rel)), exist_ok=True)
            shutil.copy2(os.path.join(src, rel), os.path.join(dest, rel))
    for dirpath, _, files in os.walk(dest, topdown=False):
        for name in files:
            rel = os.path.relpath(os.path.join(dirpath, name), dest)
            if os.path.normcase(rel) not in keep:
                os.remove(os.path.join(dest, rel))
        if dirpath != dest and not os.listdir(dirpath):
            os.rmdir(dirpath)


def cmd_self_update(_msg):
    ok, reason = self_update_status()
    if not ok:
        raise HostError(reason)
    if JOBS:
        raise HostError("Espere os downloads terminarem antes de atualizar.")
    ext_dir = load_config()["extensionDir"]
    root = os.path.dirname(ext_dir)

    url = f"https://codeload.github.com/{REPO}/zip/refs/heads/{BRANCH}"
    try:
        with urllib.request.urlopen(url, timeout=90) as res:
            zf = zipfile.ZipFile(io.BytesIO(res.read()))
    except (OSError, zipfile.BadZipFile) as exc:
        raise HostError(f"Não consegui baixar a atualização do GitHub: {exc}")
    prefix = zf.namelist()[0].split("/")[0] + "/"
    # utf-8-sig: aceita arquivos salvos com BOM (o Bloco de Notas antigo e o PowerShell 5 fazem isso).
    try:
        new_manifest = json.loads(zf.read(prefix + "extensao/manifest.json").decode("utf-8-sig"))
        with open(os.path.join(ext_dir, "manifest.json"), encoding="utf-8-sig") as f:
            old_manifest = json.load(f)
    except (KeyError, ValueError, OSError) as exc:
        log("manifest inválido na atualização:", repr(exc))
        raise HostError("Pacote de atualização inválido.")
    if new_manifest.get("key") != old_manifest.get("key"):
        raise HostError("O pacote do GitHub não é desta extensão.")

    staging = tempfile.mkdtemp(prefix="update-", dir=APP_DIR)
    try:
        keep = extract_folder(zf, prefix, "extensao", os.path.join(staging, "extensao"))
        extract_folder(zf, prefix, "host", os.path.join(staging, "host"))
        mirror_folder(os.path.join(staging, "extensao"), ext_dir, keep)
        os.makedirs(os.path.join(root, "host"), exist_ok=True)
        for name in HOST_FILES:
            src = os.path.join(staging, "host", name)
            if os.path.isfile(src):
                shutil.copy2(src, os.path.join(root, "host", name))
                shutil.copy2(src, os.path.join(APP_DIR, name))
        for name in ROOT_FILES:
            if prefix + name in zf.namelist():
                with open(os.path.join(root, name), "wb") as out:
                    out.write(zf.read(prefix + name))
    finally:
        shutil.rmtree(staging, ignore_errors=True)
    log("atualizado para", new_manifest.get("version"))
    return {"version": new_manifest.get("version")}


COMMANDS = {
    "ping": cmd_ping,
    "info": cmd_info,
    "download": cmd_download,
    "cancel": cmd_cancel,
    "reveal": cmd_reveal,
    "pickFolder": cmd_pick_folder,
    "update": cmd_update,
    "selfUpdate": cmd_self_update,
}


def handle(msg):
    req_id = msg.get("id")
    try:
        fn = COMMANDS.get(msg.get("cmd"))
        if not fn:
            raise HostError(f"Comando desconhecido: {msg.get('cmd')}")
        send({"id": req_id, "ok": True, "result": fn(msg)})
    except HostError as exc:
        send({"id": req_id, "ok": False, "error": str(exc)})
    except subprocess.TimeoutExpired:
        send({"id": req_id, "ok": False, "error": "Demorou demais para responder. Tente de novo."})
    except Exception as exc:  # noqa: BLE001
        log("erro:", msg.get("cmd"), repr(exc))
        send({"id": req_id, "ok": False, "error": f"Erro inesperado: {exc}"})


def main():
    refresh_path()
    os.makedirs(TMP_DIR, exist_ok=True)
    log("host iniciado", VERSION, PY)
    while True:
        try:
            msg = read_message()
        except (OSError, ValueError) as exc:
            log("leitura falhou:", repr(exc))
            break
        if msg is None:
            break
        threading.Thread(target=handle, args=(msg,), daemon=True).start()
    for job in list(JOBS.values()):
        job.cancelled = True
        kill_tree(job.proc)
    log("host encerrado")


if __name__ == "__main__":
    main()
