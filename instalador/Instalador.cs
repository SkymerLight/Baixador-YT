using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace YTBaixador
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }

            if (args.Contains("--silent"))
            {
                Installer installer = new Installer(msg => { }, pct => { });
                try
                {
                    if (args.Contains("--uninstall")) installer.Uninstall();
                    else installer.Install();
                    return 0;
                }
                catch (Exception e)
                {
                    installer.Log("ERRO: " + e.Message);
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
            return 0;
        }
    }

    class Installer
    {
        public const string Version = "2.0.0";
        public const string HostName = "com.ytbaixador.host";
        public const string ExtensionId = "enedkkbdeanincbhpmaokfjmhjlcpmop";
        public const string HostExeName = "YTBaixador-Host.exe";

        public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YTBaixador");
        public static readonly string ExtDir = Path.Combine(AppDir, "extensao");
        static readonly string BinDir = Path.Combine(AppDir, "bin");
        static readonly string HostExe = Path.Combine(AppDir, HostExeName);
        static readonly string LogFile = Path.Combine(AppDir, "instalador.log");

        static readonly string[][] Browsers =
        {
            new[] { "Microsoft Edge", @"Software\Microsoft\Edge\NativeMessagingHosts" },
            new[] { "Google Chrome", @"Software\Google\Chrome\NativeMessagingHosts" },
            new[] { "Brave", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts" },
            new[] { "Vivaldi", @"Software\Vivaldi\NativeMessagingHosts" },
            new[] { "Chromium", @"Software\Chromium\NativeMessagingHosts" },
        };

        readonly Action<string> status;
        readonly Action<int> progress;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public Installer(Action<string> status, Action<int> progress)
        {
            this.status = status;
            this.progress = progress;
        }

        public static bool IsInstalled
        {
            get { return File.Exists(HostExe); }
        }

        public void Install()
        {
            Directory.CreateDirectory(BinDir);
            Log("---- instalação " + Version + " em " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            Step(3, "Copiando a extensão...");
            ExtractExtension();
            Step(6, "Copiando o programa auxiliar...");
            WriteHost();

            Step(8, "Baixando o motor de download (yt-dlp)...");
            EnsureYtDlp(8, 40);
            Step(40, "Procurando o FFmpeg...");
            EnsureFfmpeg(40, 85);
            Step(85, "Procurando o Deno ou o Node.js...");
            EnsureJsRuntime(85, 94);

            Step(95, "Registrando nos navegadores...");
            Register();
            WriteConfig();
            CleanupOldVersion();

            Step(98, "Testando...");
            TestHost();
            Step(100, "Pronto!");
        }

        public void Uninstall()
        {
            Step(10, "Tirando o registro dos navegadores...");
            foreach (string[] browser in Browsers)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(browser[1] + "\\" + HostName, false); }
                catch (Exception) { }
            }
            Step(50, "Apagando os arquivos...");
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HostExeName)))
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch (Exception) { }
            }
            Thread.Sleep(500);
            try { Directory.Delete(AppDir, true); }
            catch (Exception)
            {
                throw new Exception("Alguns arquivos estão em uso. Feche o navegador e tente de novo.");
            }
            Step(100, "Desinstalado.");
        }

        void ExtractExtension()
        {
            using (Stream resource = Resource("extensao.zip"))
            using (ZipArchive zip = new ZipArchive(resource, ZipArchiveMode.Read))
            {
                Directory.CreateDirectory(ExtDir);
                string root = Path.GetFullPath(ExtDir).TrimEnd('\\') + "\\";
                HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/")) continue;
                    string rel = entry.FullName.Replace('/', '\\');
                    string target = Path.GetFullPath(Path.Combine(ExtDir, rel));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                    keep.Add(rel);
                }
                foreach (string file in Directory.GetFiles(ExtDir, "*", SearchOption.AllDirectories))
                {
                    if (!keep.Contains(file.Substring(root.Length))) File.Delete(file);
                }
                Log("extensão copiada: " + keep.Count + " arquivos em " + ExtDir);
            }
        }

        void WriteHost()
        {
            string fresh = HostExe + ".new";
            using (Stream resource = Resource("host.exe"))
            using (FileStream file = File.Create(fresh))
            {
                resource.CopyTo(file);
            }
            if (File.Exists(HostExe) && File.ReadAllBytes(HostExe).SequenceEqual(File.ReadAllBytes(fresh)))
            {
                File.Delete(fresh);
                return;
            }
            string old = HostExe + ".old";
            try { if (File.Exists(old)) File.Delete(old); }
            catch (Exception) { }
            if (File.Exists(HostExe)) File.Move(HostExe, old);
            File.Move(fresh, HostExe);
            Log("programa auxiliar gravado em " + HostExe);
        }

        void EnsureYtDlp(int from, int to)
        {
            string exe = Path.Combine(BinDir, "yt-dlp.exe");
            if (File.Exists(exe))
            {
                Step(from, "Atualizando o motor de download (yt-dlp)...");
                string output = RunCapture(exe, "-U", 300000);
                Log("yt-dlp -U: " + output.Trim());
            }
            else
            {
                Download("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", exe, from, to, "Baixando o yt-dlp");
                Verify(exe, "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS", "yt-dlp.exe");
            }
            string version = RunCapture(exe, "--version", 120000).Trim();
            if (version.Length == 0) throw new Exception("O yt-dlp não abriu. O antivírus pode ter bloqueado o arquivo " + exe);
            Log("yt-dlp " + version);
        }

        void EnsureFfmpeg(int from, int to)
        {
            string found = FindFfmpeg();
            if (found != null)
            {
                Log("FFmpeg já instalado: " + found);
                return;
            }
            string zip = Path.Combine(AppDir, "ffmpeg.zip");
            Download("https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", zip, from, to - 3, "Baixando o FFmpeg");
            Verify(zip, "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256", "ffmpeg-master-latest-win64-gpl.zip");
            Step(to - 3, "Extraindo o FFmpeg...");
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.Name.ToLowerInvariant();
                    if ((name == "ffmpeg.exe" || name == "ffprobe.exe") && entry.FullName.Contains("/bin/"))
                    {
                        entry.ExtractToFile(Path.Combine(BinDir, entry.Name), true);
                    }
                }
            }
            File.Delete(zip);
            if (!File.Exists(Path.Combine(BinDir, "ffmpeg.exe"))) throw new Exception("Não consegui extrair o FFmpeg.");
            Log("FFmpeg instalado em " + BinDir);
        }

        void EnsureJsRuntime(int from, int to)
        {
            string deno = Path.Combine(BinDir, "deno.exe");
            string found = File.Exists(deno) ? deno : Which("deno.exe") ?? Which("node.exe");
            if (found != null)
            {
                Log("JavaScript: " + found);
                return;
            }
            string zip = Path.Combine(AppDir, "deno.zip");
            Download("https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip", zip, from, to, "Baixando o Deno");
            Verify(zip, "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip.sha256sum", "deno-x86_64-pc-windows-msvc.zip");
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new Exception("Pacote do Deno inválido.");
                entry.ExtractToFile(deno, true);
            }
            File.Delete(zip);
            Log("Deno instalado em " + deno);
        }

        void Register()
        {
            string manifestPath = Path.Combine(AppDir, HostName + ".json");
            Dictionary<string, object> manifest = new Dictionary<string, object>
            {
                { "name", HostName },
                { "description", "YT Baixador" },
                { "path", HostExe },
                { "type", "stdio" },
                { "allowed_origins", new[] { "chrome-extension://" + ExtensionId + "/" } },
            };
            File.WriteAllText(manifestPath, Json.Serialize(manifest), new UTF8Encoding(false));
            foreach (string[] browser in Browsers)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(browser[1] + "\\" + HostName))
                {
                    key.SetValue("", manifestPath);
                }
                Log("registrado no " + browser[0]);
            }
        }

        void WriteConfig()
        {
            Dictionary<string, object> config = new Dictionary<string, object> { { "extensionDir", ExtDir } };
            File.WriteAllText(Path.Combine(AppDir, "config.json"), Json.Serialize(config), new UTF8Encoding(false));
        }

        void CleanupOldVersion()
        {
            foreach (string name in new[] { "host.py", "host.bat", "testar.py", "host.log.old" })
            {
                try { File.Delete(Path.Combine(AppDir, name)); }
                catch (Exception) { }
            }
            string venv = Path.Combine(AppDir, "venv");
            try { if (Directory.Exists(venv)) Directory.Delete(venv, true); }
            catch (Exception) { Log("não consegui apagar a pasta venv antiga"); }
        }

        void TestHost()
        {
            string output = RunCapture(HostExe, "--test", 120000);
            Log("teste: " + output.Trim());
            if (!output.Contains("\"ytdlp\":\"")) throw new Exception("O programa auxiliar não conseguiu usar o yt-dlp. Veja " + LogFile);
            if (!output.Contains("\"ffmpeg\":true")) throw new Exception("O programa auxiliar não achou o FFmpeg. Veja " + LogFile);
        }

        void Download(string url, string dest, int from, int to, string label)
        {
            string part = dest + ".part";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "YTBaixador-Instalador/" + Version;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (FileStream file = File.Create(part))
            {
                long total = response.ContentLength;
                long done = 0;
                byte[] buffer = new byte[81920];
                int read;
                int last = -1;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    file.Write(buffer, 0, read);
                    done += read;
                    if (total <= 0) continue;
                    int pct = (int)(done * 100 / total);
                    if (pct == last) continue;
                    last = pct;
                    progress(from + (to - from) * pct / 100);
                    status(label + "... " + pct + "% (" + Mb(done) + " de " + Mb(total) + ")");
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
            Log("baixado " + url + " -> " + dest);
        }

        void Verify(string file, string sumsUrl, string name)
        {
            string sums;
            using (WebClient web = new WebClient())
            {
                web.Headers["User-Agent"] = "YTBaixador-Instalador/" + Version;
                sums = web.DownloadString(sumsUrl);
            }
            System.Text.RegularExpressions.Regex hex = new System.Text.RegularExpressions.Regex("[0-9a-fA-F]{64}");
            string line = sums.Split('\n').FirstOrDefault(l => l.Contains(name) && hex.IsMatch(l));
            System.Text.RegularExpressions.MatchCollection all = hex.Matches(sums);
            string expected = line != null ? hex.Match(line).Value : all.Count == 1 ? all[0].Value : null;
            if (expected == null) throw new Exception("Não encontrei o código de verificação de " + name + ".");
            string actual;
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
            {
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new Exception("O arquivo " + name + " baixado não confere com o original (SHA-256 diferente). Por segurança ele foi apagado. Tente de novo.");
            }
            Log("SHA-256 ok: " + name);
        }

        static string FindFfmpeg()
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

        static string Which(string exe)
        {
            string path = string.Join(";",
                Environment.GetEnvironmentVariable("PATH") ?? "",
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "",
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
            foreach (string dir in path.Split(';'))
            {
                string d = Environment.ExpandEnvironmentVariables(dir.Trim());
                if (d.Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(d, exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        static string RunCapture(string exe, string args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                p.StandardInput.Close();
                var output = p.StandardOutput.ReadToEndAsync();
                var errors = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); }
                    catch (Exception) { }
                    return "";
                }
                return output.Result + errors.Result;
            }
        }

        static Stream Resource(string name)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) throw new Exception("Instalador corrompido: falta " + name + ". Baixe de novo.");
            return stream;
        }

        static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0") + " MB";
        }

        void Step(int pct, string text)
        {
            progress(pct);
            status(text);
            Log(text);
        }

        public void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss ") + text + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
        }
    }

    class InstallerForm : Form
    {
        static readonly Color Accent = Color.FromArgb(225, 0, 45);
        static readonly Color Muted = Color.FromArgb(96, 96, 96);

        readonly Panel page = new Panel();
        readonly Label statusLabel = new Label();
        readonly ProgressBar bar = new ProgressBar();
        Button installButton;
        Button uninstallButton;

        public InstallerForm()
        {
            Text = "YT Baixador - Instalador";
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 440);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            Controls.Add(page);
            page.Dock = DockStyle.Fill;
            Controls.Add(Header());
            ShowWelcome();
        }

        Control Header()
        {
            Panel header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(248, 248, 248) };
            PictureBox logo = new PictureBox { Location = new Point(24, 18), Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom };
            try { logo.Image = Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")); }
            catch (Exception) { }
            Label title = new Label { Text = "YT Baixador", Font = new Font("Segoe UI Semibold", 16f), Location = new Point(92, 18), AutoSize = true };
            Label sub = new Label { Text = "Baixe vídeos e áudios do YouTube, Instagram, TikTok e X  ·  v" + Installer.Version, ForeColor = Muted, Location = new Point(94, 52), AutoSize = true };
            header.Controls.AddRange(new Control[] { logo, title, sub });
            return header;
        }

        void ShowWelcome()
        {
            page.Controls.Clear();
            bool installed = Installer.IsInstalled;
            Label text = new Label
            {
                Location = new Point(24, 20),
                Size = new Size(512, 150),
                Text = (installed
                    ? "O YT Baixador já está instalado neste PC. Clique em Atualizar para reinstalar a extensão e renovar o motor de download.\n\n"
                    : "Este instalador prepara tudo que a extensão precisa para baixar os vídeos:\n\n") +
                    "•  o motor de download (yt-dlp, programa oficial e gratuito);\n" +
                    "•  o FFmpeg, que junta vídeo e áudio e converte para MP3 (se ainda não tiver);\n" +
                    "•  o registro da extensão no Edge, Chrome, Brave e Vivaldi.\n\n" +
                    "Nada é instalado fora da sua pasta de usuário e não precisa de administrador.",
            };
            statusLabel.Location = new Point(24, 200);
            statusLabel.Size = new Size(512, 24);
            statusLabel.ForeColor = Muted;
            statusLabel.Text = "";
            bar.Location = new Point(24, 228);
            bar.Size = new Size(512, 10);
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = 0;
            bar.Visible = false;

            installButton = PrimaryButton(installed ? "Atualizar" : "Instalar", new Point(376, 280));
            installButton.Click += delegate { Run(false); };
            uninstallButton = new Button { Text = "Desinstalar", Location = new Point(24, 284), Size = new Size(120, 36), FlatStyle = FlatStyle.System, Visible = installed };
            uninstallButton.Click += delegate
            {
                if (MessageBox.Show(this, "Tirar o programa auxiliar do YT Baixador deste PC?", "Desinstalar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Run(true);
            };
            page.Controls.AddRange(new Control[] { text, statusLabel, bar, installButton, uninstallButton });
        }

        void Run(bool uninstall)
        {
            installButton.Enabled = false;
            uninstallButton.Enabled = false;
            bar.Visible = true;
            Installer installer = new Installer(
                msg => BeginInvoke((Action)(() => statusLabel.Text = msg)),
                pct => BeginInvoke((Action)(() => bar.Value = Math.Max(0, Math.Min(100, pct)))));
            Thread thread = new Thread(() =>
            {
                try
                {
                    if (uninstall) installer.Uninstall();
                    else installer.Install();
                    BeginInvoke((Action)(() => { if (uninstall) ShowUninstalled(); else ShowDone(); }));
                }
                catch (Exception e)
                {
                    installer.Log("ERRO: " + e);
                    BeginInvoke((Action)(() =>
                    {
                        statusLabel.ForeColor = Accent;
                        statusLabel.Text = "Deu erro: " + e.Message;
                        MessageBox.Show(this, e.Message + "\n\nDetalhes em " + Path.Combine(Installer.AppDir, "instalador.log"), "Não deu certo", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        installButton.Enabled = true;
                        installButton.Text = "Tentar de novo";
                        uninstallButton.Enabled = true;
                    }));
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void ShowDone()
        {
            page.Controls.Clear();
            Label title = new Label { Text = "Tudo pronto! Falta só colocar a extensão no navegador:", Font = new Font("Segoe UI Semibold", 11f), Location = new Point(24, 18), AutoSize = true };
            Label steps = new Label
            {
                Location = new Point(24, 50),
                Size = new Size(512, 150),
                Text =
                    "1.  Clique em \"Abrir extensões\" do seu navegador (botões abaixo).\n" +
                    "2.  Ligue o \"Modo de desenvolvedor\" (fica na lateral ou no canto da página).\n" +
                    "3.  Clique em \"Carregar sem compactação\" e escolha a pasta abaixo.\n" +
                    "     Dica: o botão \"Copiar o caminho\" já copia; é só colar na janela e confirmar.\n" +
                    "4.  Abra um vídeo do YouTube e clique em \"YT Baixador\", ao lado de Compartilhar.",
            };
            TextBox path = new TextBox { Text = Installer.ExtDir, ReadOnly = true, Location = new Point(24, 206), Size = new Size(512, 26), BackColor = Color.FromArgb(245, 245, 245) };
            Button copy = SecondaryButton("Copiar o caminho", new Point(24, 244), 160);
            copy.Click += delegate
            {
                Clipboard.SetText(Installer.ExtDir);
                copy.Text = "Copiado!";
            };
            Button folder = SecondaryButton("Abrir a pasta", new Point(192, 244), 130);
            folder.Click += delegate { Process.Start("explorer.exe", "\"" + Installer.ExtDir + "\""); };
            Button edge = PrimaryButton("Abrir extensões do Edge", new Point(24, 300));
            edge.Size = new Size(250, 40);
            edge.Click += delegate { OpenExtensionsPage("msedge", "edge://extensions"); };
            Button chrome = SecondaryButton("Abrir extensões do Chrome", new Point(286, 300), 250);
            chrome.Height = 40;
            chrome.Click += delegate { OpenExtensionsPage("chrome", "chrome://extensions"); };
            Label foot = new Label { Text = "O botão \"Modo de desenvolvedor\" precisa ficar ligado para a extensão funcionar.", ForeColor = Muted, Location = new Point(24, 356), AutoSize = true };
            page.Controls.AddRange(new Control[] { title, steps, path, copy, folder, edge, chrome, foot });
        }

        void ShowUninstalled()
        {
            page.Controls.Clear();
            Label text = new Label
            {
                Location = new Point(24, 24),
                Size = new Size(512, 120),
                Text = "Pronto, o programa auxiliar foi removido.\n\nPara tirar a extensão, abra edge://extensions (ou chrome://extensions) e clique em Remover no YT Baixador. Seus downloads continuam na pasta onde estavam.",
            };
            Button close = PrimaryButton("Fechar", new Point(376, 280));
            close.Click += delegate { Close(); };
            page.Controls.AddRange(new Control[] { text, close });
        }

        void OpenExtensionsPage(string browser, string url)
        {
            try { Process.Start(new ProcessStartInfo(browser, url) { UseShellExecute = true }); }
            catch (Exception)
            {
                Clipboard.SetText(url);
                MessageBox.Show(this, "Não achei o navegador. Copiei o endereço " + url + ": cole na barra de endereço do navegador.", "YT Baixador");
            }
        }

        static Button PrimaryButton(string text, Point location)
        {
            Button b = new Button { Text = text, Location = location, Size = new Size(160, 40), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 10f), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 0, 40);
            return b;
        }

        static Button SecondaryButton(string text, Point location, int width)
        {
            Button b = new Button { Text = text, Location = location, Size = new Size(width, 34), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(240, 240, 240), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(220, 220, 220);
            return b;
        }
    }
}
