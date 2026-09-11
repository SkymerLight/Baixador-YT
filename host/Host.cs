using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace YTBaixador
{
    class HostError : Exception
    {
        public HostError(string message) : base(message) { }
    }

    class Job
    {
        public string Id;
        public List<string> Args;
        public string Tmp;
        public Process Proc;
        public volatile bool Cancelled;
        public string FilePath;
        public string CookieFile;
    }

    class ProcResult
    {
        public int Code;
        public string Out;
        public string Err;
    }

    static class Host
    {
        const string Version = "2.0.0";
        const string Repo = "SkymerLight/Baixador-YT";
        const string Branch = "main";
        const string ExeName = "YTBaixador-Host.exe";
        const string Loudnorm = "ExtractAudio+ffmpeg_o:-af loudnorm=I=-14:TP=-1.5:LRA=11 -ar 44100";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        static readonly string BinDir = Path.Combine(AppDir, "bin");
        static readonly string TmpDir = Path.Combine(AppDir, "tmp");
        static readonly string CacheDir = Path.Combine(AppDir, "cache");
        static readonly string LogFile = Path.Combine(AppDir, "host.log");
        static readonly string ConfigFile = Path.Combine(AppDir, "config.json");

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
        static readonly object OutLock = new object();
        static readonly object LogLock = new object();
        static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
        static Stream Stdout;
        static bool TestMode;
        static string cachedYtDlpVersion;

        static readonly Dictionary<string, string[]> AudioArgs = new Dictionary<string, string[]>
        {
            { "m4a", new[] { "-f", "ba[ext=m4a]/ba/b", "-x", "--audio-format", "m4a" } },
            { "opus", new[] { "-f", "ba[acodec=opus]/ba/b", "-x", "--audio-format", "opus" } },
            { "mp3-320", Mp3(320) },
            { "mp3-192", Mp3(192) },
            { "mp3-128", Mp3(128) },
            { "mp3-64", Mp3(64) },
            { "wav", new[] { "-f", "ba/b", "-x", "--audio-format", "wav" } },
            { "flac", new[] { "-f", "ba/b", "-x", "--audio-format", "flac" } },
        };

        static readonly string[][] PpLabels =
        {
            new[] { "merger", "Juntando vídeo e áudio" },
            new[] { "extractaudio", "Convertendo áudio" },
            new[] { "sponsorblock", "Procurando patrocínios" },
            new[] { "modifychapters", "Tirando patrocínios" },
            new[] { "thumbnailsconvertor", "Preparando capa" },
            new[] { "embedthumbnail", "Adicionando capa" },
            new[] { "metadata", "Gravando informações" },
            new[] { "movefiles", "Finalizando" },
        };

        static readonly string[][] ErrorHints =
        {
            new[] { "not a bot", "O YouTube pediu verificação anti-robô. Espere um pouco e tente de novo, ou atualize o motor de download." },
            new[] { "confirm your age", "Vídeo com restrição de idade: o site exige login para ele." },
            new[] { "private video", "Esse vídeo é privado." },
            new[] { "members-only", "Vídeo exclusivo para membros do canal." },
            new[] { "video unavailable", "Vídeo indisponível." },
            new[] { "empty media response", "O Instagram só libera esse post com login. Ligue \"Usar meu login do navegador\" nas configurações da extensão." },
            new[] { "ip address is blocked", "Esse post está bloqueado para o seu país ou rede." },
            new[] { "login required", "Esse conteúdo só abre com login. Ligue \"Usar meu login do navegador\" nas configurações da extensão." },
            new[] { "requires login", "Esse conteúdo só abre com login. Ligue \"Usar meu login do navegador\" nas configurações da extensão." },
            new[] { "use --cookies", "Esse site pediu login. Ligue \"Usar meu login do navegador\" nas configurações da extensão." },
            new[] { "rate-limit", "O site limitou os acessos. Espere um pouco e tente de novo." },
            new[] { "no video could be found", "Esse post não tem vídeo." },
            new[] { "no video in this", "Esse post não tem vídeo." },
            new[] { "is not a valid url", "Link inválido." },
            new[] { "unsupported url", "Esse link não é suportado." },
            new[] { "requested format is not available", "Essa qualidade não está disponível para este vídeo." },
            new[] { "ffmpeg", "FFmpeg não encontrado. Rode o instalador de novo." },
            new[] { "ffprobe", "FFmpeg não encontrado. Rode o instalador de novo." },
            new[] { "javascript runtime", "O motor de download precisa do Deno ou do Node.js. Rode o instalador de novo." },
            new[] { "n challenge", "Falha ao decifrar o vídeo. Atualize o motor de download." },
            new[] { "http error 403", "O site bloqueou o download (erro 403). Atualize o motor de download e tente de novo." },
            new[] { "unable to extract", "Não consegui ler essa página. Atualize o motor de download e tente de novo." },
            new[] { "no space left", "Sem espaço no disco." },
        };

        static string[] Mp3(int kbps)
        {
            return new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", kbps + "K" };
        }

        static int Main(string[] args)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            RefreshPath();
            TryDelete(Path.Combine(AppDir, ExeName + ".old"));

            if (args.Length > 0 && args[0] == "--version")
            {
                Console.WriteLine(Version);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--test") return RunTest(args);

            Directory.CreateDirectory(TmpDir);
            Stream stdin = Console.OpenStandardInput();
            Stdout = Console.OpenStandardOutput();
            Log("host iniciado " + Version);
            while (true)
            {
                Dictionary<string, object> msg;
                try { msg = ReadMessage(stdin); }
                catch (Exception e)
                {
                    Log("leitura falhou: " + e.Message);
                    break;
                }
                if (msg == null) break;
                Dictionary<string, object> current = msg;
                ThreadPool.QueueUserWorkItem(delegate { Handle(current); });
            }
            lock (Jobs)
            {
                foreach (Job job in Jobs.Values)
                {
                    job.Cancelled = true;
                    KillTree(job.Proc);
                }
            }
            Log("host encerrado");
            return 0;
        }

        static Dictionary<string, object> ReadMessage(Stream stream)
        {
            byte[] head = ReadExactly(stream, 4);
            if (head == null) return null;
            byte[] body = ReadExactly(stream, BitConverter.ToInt32(head, 0));
            if (body == null) return null;
            return Json.DeserializeObject(Encoding.UTF8.GetString(body)) as Dictionary<string, object>;
        }

        static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        static void Send(Dictionary<string, object> msg)
        {
            string text = Json.Serialize(msg);
            lock (OutLock)
            {
                if (TestMode)
                {
                    Console.WriteLine(text);
                    return;
                }
                byte[] data = Encoding.UTF8.GetBytes(text);
                Stdout.Write(BitConverter.GetBytes(data.Length), 0, 4);
                Stdout.Write(data, 0, data.Length);
                Stdout.Flush();
            }
        }

        static void Handle(Dictionary<string, object> msg)
        {
            object id = Get(msg, "id");
            string cmd = Str(msg, "cmd");
            try
            {
                object result;
                switch (cmd)
                {
                    case "ping": result = Ping(); break;
                    case "info": result = Info(msg); break;
                    case "download": result = Download(msg); break;
                    case "cancel": result = Cancel(msg); break;
                    case "reveal": result = Reveal(msg); break;
                    case "pickFolder": result = PickFolder(msg); break;
                    case "update": result = UpdateYtDlp(); break;
                    case "selfUpdate": result = SelfUpdate(); break;
                    default: throw new HostError("Comando desconhecido: " + cmd);
                }
                Send(D("id", id, "ok", true, "result", result));
            }
            catch (HostError e)
            {
                Send(D("id", id, "ok", false, "error", e.Message));
            }
            catch (Exception e)
            {
                Log("erro em " + cmd + ": " + e);
                Send(D("id", id, "ok", false, "error", "Erro inesperado: " + e.Message));
            }
        }

        static Dictionary<string, object> Ping()
        {
            string reason;
            bool canUpdate = SelfUpdateStatus(out reason);
            string[] js = JsRuntime();
            return D(
                "host", Version,
                "ytdlp", YtDlpVersion(false),
                "ffmpeg", FfmpegPath() != null,
                "js", js == null ? null : js[0],
                "defaultFolder", DefaultFolder(),
                "selfUpdate", D("ok", canUpdate, "reason", reason));
        }

        static Dictionary<string, object> Info(Dictionary<string, object> msg)
        {
            string url = ValidateUrl(Str(msg, "url"));
            List<string> args = BaseArgs();
            args.AddRange(new[]
            {
                "-O", "%(.{id,title,uploader,channel,duration,thumbnail,webpage_url,is_live,extractor_key,formats})j",
                "--", url,
            });
            string cookieFile = WriteCookies(msg);
            if (cookieFile != null) args.InsertRange(0, new[] { "--cookies", cookieFile });
            ProcResult r;
            try { r = RunProcess(RequireYtDlp(), args, 120000); }
            finally { TryDelete(cookieFile); }
            if (r.Code != 0)
            {
                string raw;
                string text = FriendlyError(r.Err, out raw);
                Log("info falhou: " + url + " " + raw);
                throw new HostError(text);
            }
            string first = r.Out.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("{"));
            if (first == null) throw new HostError("Não encontrei vídeo nesse link.");
            Dictionary<string, object> data = (Dictionary<string, object>)Json.DeserializeObject(first);
            if (Bool(data, "is_live", false)) throw new HostError("Transmissões ao vivo não são suportadas.");

            double duration = Num(data, "duration");
            object[] rawFormats = Get(data, "formats") as object[] ?? new object[0];
            List<Dictionary<string, object>> formats = rawFormats.OfType<Dictionary<string, object>>()
                .Where(f => !Bool(f, "has_drm", false)).ToList();

            Func<Dictionary<string, object>, bool> hasVideo = f => { string v = Str(f, "vcodec"); return v != null && v != "none"; };
            Func<Dictionary<string, object>, bool> hasAudio = f => { string a = Str(f, "acodec"); return a != null && a != "none"; };
            Func<Dictionary<string, object>, double> bitrate = f => Num(f, "abr") > 0 ? Num(f, "abr") : Num(f, "tbr");

            List<Dictionary<string, object>> audios = formats.Where(f => Str(f, "vcodec") == "none" && hasAudio(f)).ToList();
            Dictionary<string, object> bestAudio = audios.OrderByDescending(bitrate).FirstOrDefault();
            double audioSize = bestAudio == null ? 0 : FormatSize(bestAudio, duration);
            Dictionary<string, object> bestM4a = audios.Where(f => (Str(f, "acodec") ?? "").StartsWith("mp4a")).OrderByDescending(bitrate).FirstOrDefault();
            Dictionary<string, object> bestOpus = audios.Where(f => Str(f, "acodec") == "opus").OrderByDescending(bitrate).FirstOrDefault();

            Func<Dictionary<string, object>, bool> playable = f => Str(f, "protocol") == "https" && !string.IsNullOrEmpty(Str(f, "url"));
            Dictionary<string, object> preview =
                audios.Where(playable).OrderByDescending(f => (Str(f, "acodec") ?? "").StartsWith("mp4a")).ThenByDescending(bitrate).FirstOrDefault()
                ?? formats.Where(f => playable(f) && Str(f, "acodec") != "none" && Str(f, "ext") == "mp4").OrderBy(f => Num(f, "tbr")).FirstOrDefault();

            Dictionary<int, double[]> qualities = new Dictionary<int, double[]>();
            foreach (Dictionary<string, object> f in formats)
            {
                int height = (int)Num(f, "height");
                if (!hasVideo(f) || height <= 0) continue;
                int width = (int)Num(f, "width");
                int q = width > 0 ? Math.Min(height, width) : height;
                double[] entry;
                if (!qualities.TryGetValue(q, out entry)) qualities[q] = entry = new double[2];
                entry[0] = Math.Max(entry[0], Math.Round(Num(f, "fps")));
                entry[1] = Math.Max(entry[1], FormatSize(f, duration));
            }
            List<object> video = new List<object>();
            foreach (int q in qualities.Keys.OrderByDescending(k => k))
            {
                double fps = qualities[q][0];
                double size = qualities[q][1];
                video.Add(D(
                    "q", q,
                    "label", q + "p" + (fps > 30 ? ((int)fps).ToString(Inv) : ""),
                    "fps", fps,
                    "size", size > 0 ? size + audioSize : 0));
            }

            return D(
                "id", Str(data, "id"),
                "title", Str(data, "title") ?? "Sem título",
                "uploader", Str(data, "uploader") ?? Str(data, "channel") ?? "",
                "duration", duration,
                "thumbnail", Str(data, "thumbnail"),
                "url", Str(data, "webpage_url") ?? url,
                "site", Str(data, "extractor_key") ?? "",
                "video", video,
                "audioSize", audioSize,
                "audioSizes", D(
                    "m4a", bestM4a != null ? FormatSize(bestM4a, duration) : audioSize,
                    "opus", bestOpus != null ? FormatSize(bestOpus, duration) : audioSize),
                "previewUrl", preview == null ? null : Str(preview, "url"),
                "hasAudio", formats.Any(f => Str(f, "acodec") != "none"));
        }

        static double FormatSize(Dictionary<string, object> f, double duration)
        {
            double size = Num(f, "filesize");
            if (size <= 0) size = Num(f, "filesize_approx");
            if (size <= 0 && duration > 0 && Num(f, "tbr") > 0) size = Num(f, "tbr") * 1000 / 8 * duration;
            return size;
        }

        static Dictionary<string, object> Download(Dictionary<string, object> msg)
        {
            string url = ValidateUrl(Str(msg, "url"));
            string ytdlp = RequireYtDlp();
            if (FfmpegPath() == null) throw new HostError("FFmpeg não encontrado. Rode o instalador de novo.");
            string folder = Str(msg, "folder");
            if (string.IsNullOrEmpty(folder)) folder = DefaultFolder();
            try { Directory.CreateDirectory(folder); }
            catch (Exception) { throw new HostError("Não consegui usar a pasta: " + folder); }

            string jobId = Guid.NewGuid().ToString("N").Substring(0, 12);
            string tmp = Path.Combine(TmpDir, jobId);
            List<string> args = BaseArgs();
            string cookieFile = WriteCookies(msg);
            if (cookieFile != null) args.AddRange(new[] { "--cookies", cookieFile });
            args.AddRange(new[]
            {
                "-P", folder, "-P", "temp:" + tmp,
                "--no-mtime", "--embed-metadata", "--newline", "--progress", "--progress-delta", "0.5",
                "--progress-template",
                "download:[YTB]%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
                "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.vcodec)s",
                "--progress-template", "postprocess:[YTBPP]%(progress.status)s|%(progress.postprocessor)s",
                "--print", "after_move:[YTBFILE]%(filepath)s",
            });

            double[] section = ParseSection(Get(msg, "section") as Dictionary<string, object>);
            string suffix = section == null ? "" : " (" + TimeLabel(section[0]) + " a " + TimeLabel(section[1]) + ")";
            if (section != null)
            {
                args.Add("--download-sections");
                args.Add("*" + section[0].ToString("0.###", Inv) + "-" + section[1].ToString("0.###", Inv));
            }

            if (Str(msg, "mode") == "audio")
            {
                string fmt = Str(msg, "audioFormat");
                if (fmt == null || !AudioArgs.ContainsKey(fmt)) fmt = "m4a";
                args.AddRange(AudioArgs[fmt]);
                args.Add("-o");
                args.Add("%(title).150B" + suffix + ".%(ext)s");
                if (fmt != "wav") args.AddRange(new[] { "--embed-thumbnail", "--convert-thumbnails", "jpg" });
                if (section != null && fmt != "m4a") args.Add("--force-keyframes-at-cuts");
                if (fmt.StartsWith("mp3") && Bool(msg, "normalize", true))
                {
                    args.Add("--postprocessor-args");
                    args.Add(Loudnorm);
                }
            }
            else
            {
                int quality = (int)Num(msg, "quality");
                List<string> sort = new List<string>();
                if (quality > 0) sort.Add("res:" + quality);
                if (Bool(msg, "preferH264", true))
                {
                    sort.Add("vcodec:h264");
                    sort.Add("acodec:aac");
                }
                string label = quality > 0 ? quality + "p" : "%(height)sp";
                args.AddRange(new[] { "-f", "bv*+ba/b", "--merge-output-format", "mp4", "-o", "%(title).150B [" + label + "]" + suffix + ".%(ext)s" });
                if (sort.Count > 0)
                {
                    args.Add("-S");
                    args.Add(string.Join(",", sort));
                }
                if (section != null) args.Add("--force-keyframes-at-cuts");
            }

            if (section == null && Bool(msg, "sponsorblock", false) && IsYouTube(url))
            {
                args.Add("--sponsorblock-remove");
                args.Add("sponsor");
            }
            args.Add("--");
            args.Add(url);

            Job job = new Job { Id = jobId, Args = args, Tmp = tmp, CookieFile = cookieFile };
            lock (Jobs) Jobs[jobId] = job;
            Thread thread = new Thread(() => RunJob(job, ytdlp));
            thread.IsBackground = true;
            thread.Start();
            return D("jobId", jobId);
        }

        static void RunJob(Job job, string ytdlp)
        {
            Log("download: " + JoinArgs(job.Args));
            StringBuilder errors = new StringBuilder();
            try
            {
                job.Proc = StartProcess(ytdlp, job.Args);
                job.Proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                job.Proc.BeginErrorReadLine();
                string line;
                while ((line = job.Proc.StandardOutput.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("[YTB]")) SendProgress(job, line.Substring(5).Split('|'));
                    else if (line.StartsWith("[YTBPP]"))
                    {
                        string[] parts = line.Substring(7).Split('|');
                        if (parts[0] != "started") continue;
                        string name = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                        string[] match = PpLabels.FirstOrDefault(p => name.Contains(p[0]));
                        Send(D("event", "progress", "jobId", job.Id, "stage", "process", "label", match == null ? "Processando" : match[1]));
                    }
                    else if (line.StartsWith("[YTBFILE]")) job.FilePath = line.Substring(9).Trim();
                }
                job.Proc.WaitForExit();
                Thread.Sleep(200);
                if (job.Cancelled)
                {
                    Send(D("event", "cancelled", "jobId", job.Id));
                }
                else if (job.Proc.ExitCode == 0 && job.FilePath != null && File.Exists(job.FilePath))
                {
                    Send(D("event", "done", "jobId", job.Id, "filepath", job.FilePath,
                        "filename", Path.GetFileName(job.FilePath), "size", new FileInfo(job.FilePath).Length));
                }
                else
                {
                    string raw;
                    string text;
                    lock (errors) text = FriendlyError(errors.ToString(), out raw);
                    Log("download falhou: " + raw);
                    Send(D("event", "error", "jobId", job.Id, "error", text, "detail", raw));
                }
            }
            catch (Exception e)
            {
                Log("erro no job: " + e);
                Send(D("event", "error", "jobId", job.Id, "error", e.Message));
            }
            finally
            {
                lock (Jobs) Jobs.Remove(job.Id);
                RemoveDir(job.Tmp);
                TryDelete(job.CookieFile);
            }
        }

        static void SendProgress(Job job, string[] parts)
        {
            Func<int, string> part = i => i < parts.Length ? parts[i] : "";
            double? done = ToNum(part(1));
            double? total = ToNum(part(2)) ?? ToNum(part(3));
            object percent = null;
            if (done.HasValue && total.HasValue && total.Value > 0) percent = done.Value / total.Value * 100;
            if (part(0) == "finished") percent = 100.0;
            Send(D(
                "event", "progress", "jobId", job.Id, "stage", "download",
                "stream", part(6) == "none" ? "audio" : "video",
                "percent", percent, "speed", ToNum(part(4)), "eta", ToNum(part(5)),
                "downloaded", done, "total", total));
        }

        static Dictionary<string, object> Cancel(Dictionary<string, object> msg)
        {
            Job job;
            lock (Jobs) Jobs.TryGetValue(Str(msg, "jobId") ?? "", out job);
            if (job == null) return D("cancelled", false);
            job.Cancelled = true;
            KillTree(job.Proc);
            return D("cancelled", true);
        }

        static Dictionary<string, object> Reveal(Dictionary<string, object> msg)
        {
            string path = Str(msg, "path") ?? "";
            try { path = path.Length > 0 ? Path.GetFullPath(path) : ""; }
            catch (Exception) { path = ""; }
            if (path.Length > 0 && File.Exists(path))
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
                return D("opened", true);
            }
            string folder = Str(msg, "folder");
            if (string.IsNullOrEmpty(folder) && path.Length > 0) folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start("explorer.exe", "\"" + folder + "\"");
                return D("opened", true, "missing", true);
            }
            throw new HostError("Arquivo não encontrado. Ele foi movido ou apagado?");
        }

        static Dictionary<string, object> PickFolder(Dictionary<string, object> msg)
        {
            string current = Str(msg, "current");
            string chosen = null;
            Thread thread = new Thread(() =>
            {
                using (Form owner = new Form())
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    owner.TopMost = true;
                    owner.ShowInTaskbar = false;
                    owner.FormBorderStyle = FormBorderStyle.None;
                    owner.StartPosition = FormStartPosition.CenterScreen;
                    owner.Size = new System.Drawing.Size(1, 1);
                    owner.Opacity = 0;
                    dialog.Description = "Onde salvar os downloads?";
                    dialog.ShowNewFolderButton = true;
                    dialog.SelectedPath = string.IsNullOrEmpty(current) ? DefaultFolder() : current;
                    owner.Show();
                    owner.Activate();
                    if (dialog.ShowDialog(owner) == DialogResult.OK) chosen = dialog.SelectedPath;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return D("folder", chosen);
        }

        static Dictionary<string, object> UpdateYtDlp()
        {
            lock (Jobs)
            {
                if (Jobs.Count > 0) throw new HostError("Espere os downloads terminarem antes de atualizar.");
            }
            string ytdlp = RequireYtDlp();
            if (!ytdlp.StartsWith(BinDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new HostError("Esse yt-dlp não foi instalado pelo YT Baixador. Rode o instalador de novo.");
            }
            ProcResult r = RunProcess(ytdlp, new[] { "-U" }, 600000);
            if (r.Code != 0)
            {
                Log("update falhou: " + r.Out + r.Err);
                throw new HostError("Não consegui atualizar o yt-dlp. Veja o host.log.");
            }
            return D("ytdlp", YtDlpVersion(true));
        }

        static bool SelfUpdateStatus(out string reason)
        {
            string extDir = Str(LoadConfig(), "extensionDir");
            if (string.IsNullOrEmpty(extDir) || !File.Exists(Path.Combine(extDir, "manifest.json")))
            {
                reason = "Rode o instalador de novo para ativar a atualização automática.";
                return false;
            }
            string parent = Path.GetDirectoryName(extDir.TrimEnd('\\'));
            if (parent != null && Directory.Exists(Path.Combine(parent, ".git")))
            {
                reason = "Esta é a cópia de desenvolvimento (repositório git): atualize com git pull.";
                return false;
            }
            reason = "";
            return true;
        }

        static Dictionary<string, object> SelfUpdate()
        {
            string reason;
            if (!SelfUpdateStatus(out reason)) throw new HostError(reason);
            lock (Jobs)
            {
                if (Jobs.Count > 0) throw new HostError("Espere os downloads terminarem antes de atualizar.");
            }
            string extDir = Path.GetFullPath(Str(LoadConfig(), "extensionDir")).TrimEnd('\\');
            byte[] zipBytes;
            try
            {
                using (WebClient web = new WebClient())
                {
                    web.Headers["User-Agent"] = "YTBaixador/" + Version;
                    zipBytes = web.DownloadData("https://codeload.github.com/" + Repo + "/zip/refs/heads/" + Branch);
                }
            }
            catch (WebException e)
            {
                throw new HostError("Não consegui baixar a atualização do GitHub: " + e.Message);
            }

            using (ZipArchive zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                string prefix = zip.Entries[0].FullName.Split('/')[0] + "/";
                ZipArchiveEntry manifestEntry = zip.GetEntry(prefix + "extensao/manifest.json");
                if (manifestEntry == null) throw new HostError("Pacote de atualização inválido.");
                Dictionary<string, object> newManifest = (Dictionary<string, object>)Json.DeserializeObject(ReadEntry(manifestEntry));
                Dictionary<string, object> oldManifest = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Path.Combine(extDir, "manifest.json")));
                if (Str(newManifest, "key") != Str(oldManifest, "key")) throw new HostError("O pacote do GitHub não é desta extensão.");

                string extPrefix = prefix + "extensao/";
                HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(extPrefix) || entry.FullName.EndsWith("/")) continue;
                    string rel = entry.FullName.Substring(extPrefix.Length).Replace('/', '\\');
                    string target = Path.GetFullPath(Path.Combine(extDir, rel));
                    if (!target.StartsWith(extDir + "\\", StringComparison.OrdinalIgnoreCase)) throw new HostError("Pacote de atualização inválido.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                    keep.Add(rel);
                }
                foreach (string file in Directory.GetFiles(extDir, "*", SearchOption.AllDirectories))
                {
                    if (!keep.Contains(file.Substring(extDir.Length + 1))) File.Delete(file);
                }
                foreach (string dir in Directory.GetDirectories(extDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }

                ZipArchiveEntry hostEntry = zip.GetEntry(prefix + "dist/" + ExeName);
                if (hostEntry != null) ReplaceSelf(hostEntry);
                Log("atualizado para " + Str(newManifest, "version"));
                return D("version", Str(newManifest, "version"));
            }
        }

        static void ReplaceSelf(ZipArchiveEntry entry)
        {
            string exe = Path.Combine(AppDir, ExeName);
            string fresh = exe + ".new";
            string old = exe + ".old";
            entry.ExtractToFile(fresh, true);
            if (File.Exists(exe) && FilesEqual(fresh, exe))
            {
                File.Delete(fresh);
                return;
            }
            TryDelete(old);
            if (File.Exists(exe)) File.Move(exe, old);
            File.Move(fresh, exe);
        }

        static int RunTest(string[] args)
        {
            TestMode = true;
            Console.OutputEncoding = Encoding.UTF8;
            Directory.CreateDirectory(TmpDir);
            try
            {
                Console.WriteLine("ping: " + Json.Serialize(Ping()));
                if (args.Length < 2) return 0;
                Dictionary<string, object> info = Info(D("url", args[1]));
                Console.WriteLine(Str(info, "title") + " (" + Str(info, "site") + ", " + Str(info, "uploader") + ", " + Str(info, "duration") + "s)");
                foreach (Dictionary<string, object> v in (IEnumerable)info["video"])
                {
                    Console.WriteLine("  " + Str(v, "label").PadLeft(8) + "  ~" + (Num(v, "size") / 1e6).ToString("0.0", Inv) + " MB");
                }
                Console.WriteLine("  prévia: " + (info["previewUrl"] == null ? "não" : "sim") + " | áudio: " + ((bool)info["hasAudio"] ? "sim" : "não"));
                if (args.Length < 3) return 0;

                Dictionary<string, object> request = D("url", args[1]);
                int quality;
                if (int.TryParse(args[2], out quality))
                {
                    request["mode"] = "video";
                    request["quality"] = quality;
                }
                else
                {
                    request["mode"] = "audio";
                    request["audioFormat"] = args[2];
                }
                for (int i = 3; i < args.Length; i++)
                {
                    string[] kv = args[i].Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    if (kv[0] == "section")
                    {
                        string[] range = kv[1].Split('-');
                        request["section"] = D("start", double.Parse(range[0], Inv), "end", double.Parse(range[1], Inv));
                    }
                    else request[kv[0]] = kv[1] == "true" ? (object)true : kv[1] == "false" ? (object)false : kv[1];
                }
                string jobId = Str(Download(request), "jobId");
                while (true)
                {
                    lock (Jobs)
                    {
                        if (!Jobs.ContainsKey(jobId)) break;
                    }
                    Thread.Sleep(300);
                }
                return 0;
            }
            catch (HostError e)
            {
                Console.WriteLine("ERRO: " + e.Message);
                return 1;
            }
        }

        static List<string> BaseArgs()
        {
            List<string> args = new List<string> { "--ignore-config", "--no-playlist", "--encoding", "utf-8", "--cache-dir", CacheDir };
            string ffmpeg = FfmpegPath();
            if (ffmpeg != null)
            {
                args.Add("--ffmpeg-location");
                args.Add(ffmpeg);
            }
            string[] js = JsRuntime();
            if (js != null)
            {
                args.Add("--js-runtimes");
                args.Add(js[0] + ":" + js[1]);
            }
            return args;
        }

        static string WriteCookies(Dictionary<string, object> msg)
        {
            string cookies = Str(msg, "cookies");
            if (string.IsNullOrEmpty(cookies)) return null;
            Directory.CreateDirectory(TmpDir);
            string path = Path.Combine(TmpDir, "cookies-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, cookies, new UTF8Encoding(false));
            return path;
        }

        static string RequireYtDlp()
        {
            string path = YtDlpPath();
            if (path == null) throw new HostError("yt-dlp não encontrado. Rode o instalador de novo.");
            return path;
        }

        static string YtDlpPath()
        {
            string local = Path.Combine(BinDir, "yt-dlp.exe");
            return File.Exists(local) ? local : Which("yt-dlp.exe");
        }

        static string YtDlpVersion(bool refresh)
        {
            if (cachedYtDlpVersion != null && !refresh) return cachedYtDlpVersion;
            string path = YtDlpPath();
            if (path == null) return null;
            ProcResult r = RunProcess(path, new[] { "--version" }, 60000);
            cachedYtDlpVersion = r.Code == 0 ? r.Out.Trim() : null;
            return cachedYtDlpVersion;
        }

        static string FfmpegPath()
        {
            string local = Path.Combine(BinDir, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            string found = Which("ffmpeg.exe");
            if (found != null) return found;
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(packages)) return null;
            foreach (string pkg in Directory.GetDirectories(packages, "*FFmpeg*"))
            {
                foreach (string sub in Directory.GetDirectories(pkg))
                {
                    string candidate = Path.Combine(sub, "bin", "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        static string[] JsRuntime()
        {
            string deno = Path.Combine(BinDir, "deno.exe");
            if (!File.Exists(deno)) deno = Which("deno.exe");
            if (deno != null) return new[] { "deno", deno };
            string node = Which("node.exe");
            return node == null ? null : new[] { "node", node };
        }

        static void RefreshPath()
        {
            List<string> parts = new List<string>();
            parts.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            parts.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "").Split(';'));
            parts.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "").Split(';'));
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> merged = new List<string>();
            foreach (string raw in parts)
            {
                string p = Environment.ExpandEnvironmentVariables(raw.Trim());
                if (p.Length > 0 && seen.Add(p.TrimEnd('\\'))) merged.Add(p);
            }
            Environment.SetEnvironmentVariable("PATH", string.Join(";", merged));
        }

        static string Which(string exe)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        static Process StartProcess(string exe, IEnumerable<string> args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, JoinArgs(args));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Process p = Process.Start(psi);
            p.StandardInput.Close();
            return p;
        }

        static ProcResult RunProcess(string exe, IEnumerable<string> args, int timeoutMs)
        {
            Process p = StartProcess(exe, args);
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                KillTree(p);
                throw new HostError("Demorou demais para responder. Tente de novo.");
            }
            p.WaitForExit();
            return new ProcResult { Code = p.ExitCode, Out = outTask.Result, Err = errTask.Result };
        }

        static string JoinArgs(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(Quote));
        }

        static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            StringBuilder sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', slashes * 2 + 1);
                    sb.Append('"');
                    slashes = 0;
                    continue;
                }
                if (slashes > 0)
                {
                    sb.Append('\\', slashes);
                    slashes = 0;
                }
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        static void KillTree(Process p)
        {
            try
            {
                if (p == null || p.HasExited) return;
                Process killer = StartProcess("taskkill.exe", new[] { "/PID", p.Id.ToString(Inv), "/T", "/F" });
                killer.WaitForExit(30000);
            }
            catch (Exception) { }
        }

        static string FriendlyError(string stderr, out string raw)
        {
            List<string> lines = (stderr ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            List<string> errors = lines.Where(l => l.StartsWith("ERROR:")).ToList();
            raw = errors.Count > 0 ? errors[errors.Count - 1] : lines.Count > 0 ? lines[lines.Count - 1] : "Erro desconhecido.";
            string low = raw.ToLowerInvariant();
            foreach (string[] hint in ErrorHints)
            {
                if (low.Contains(hint[0])) return hint[1];
            }
            return raw.StartsWith("ERROR: ") ? raw.Substring(7) : raw;
        }

        static string ValidateUrl(string url)
        {
            url = (url ?? "").Trim();
            if (!Regex.IsMatch(url, @"^https?://[^\s/]+\.[^\s/]+", RegexOptions.IgnoreCase)) throw new HostError("Link inválido.");
            return url;
        }

        static bool IsYouTube(string url)
        {
            return Regex.IsMatch(url, @"^https?://([\w-]+\.)?(youtube\.com|youtu\.be)/", RegexOptions.IgnoreCase);
        }

        static double[] ParseSection(Dictionary<string, object> section)
        {
            if (section == null) return null;
            double start = Num(section, "start");
            double end = Num(section, "end");
            if (start < 0 || end <= start) throw new HostError("O fim do trecho precisa ser depois do início.");
            return new[] { start, end };
        }

        static string TimeLabel(double seconds)
        {
            int s = (int)Math.Round(seconds);
            int h = s / 3600, m = s % 3600 / 60, sec = s % 60;
            return h > 0 ? h + "h" + m.ToString("00") + "m" + sec.ToString("00") + "s" : m + "m" + sec.ToString("00") + "s";
        }

        static string DefaultFolder()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    string value = key == null ? null : key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
                    if (!string.IsNullOrEmpty(value)) return Environment.ExpandEnvironmentVariables(value);
                }
            }
            catch (Exception) { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        static Dictionary<string, object> LoadConfig()
        {
            try { return Json.DeserializeObject(File.ReadAllText(ConfigFile)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }

        static string ReadEntry(ZipArchiveEntry entry)
        {
            using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8)) return reader.ReadToEnd();
        }

        static bool FilesEqual(string a, string b)
        {
            FileInfo fa = new FileInfo(a), fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }

        static void RemoveDir(string path)
        {
            for (int i = 0; i < 10 && Directory.Exists(path); i++)
            {
                try { Directory.Delete(path, true); }
                catch (Exception) { Thread.Sleep(500); }
            }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        static void Log(string text)
        {
            lock (LogLock)
            {
                try
                {
                    if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 1000000)
                    {
                        TryDelete(LogFile + ".old");
                        File.Move(LogFile, LogFile + ".old");
                    }
                    File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", Inv) + text + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception) { }
            }
        }

        static Dictionary<string, object> D(params object[] kv)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        static object Get(IDictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) ? value : null;
        }

        static string Str(IDictionary<string, object> d, string key)
        {
            object value = Get(d, key);
            return value == null ? null : Convert.ToString(value, Inv);
        }

        static double Num(IDictionary<string, object> d, string key)
        {
            object value = Get(d, key);
            if (value == null || value is bool) return 0;
            if (value is string)
            {
                double parsed;
                return double.TryParse((string)value, NumberStyles.Float, Inv, out parsed) ? parsed : 0;
            }
            try { return Convert.ToDouble(value, Inv); }
            catch (Exception) { return 0; }
        }

        static bool Bool(IDictionary<string, object> d, string key, bool fallback)
        {
            object value = Get(d, key);
            if (value is bool) return (bool)value;
            if (value is string) return (string)value == "true";
            return fallback;
        }

        static double? ToNum(string text)
        {
            double value;
            return double.TryParse(text, NumberStyles.Float, Inv, out value) ? value : (double?)null;
        }
    }
}
