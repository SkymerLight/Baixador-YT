$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appDir = Join-Path $env:LOCALAPPDATA 'YTBaixador'
$hostName = 'com.ytbaixador.host'
$extensionId = 'enedkkbdeanincbhpmaokfjmhjlcpmop'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "    $text" -ForegroundColor Green }
function Warn($text) { Write-Host "    $text" -ForegroundColor Yellow }
function Fail($text) { Write-Host "`n$text" -ForegroundColor Red; exit 1 }

function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Install-WithWinget($id, $name) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Fail "winget não encontrado. Instale o $name manualmente e rode este instalador de novo."
    }
    Write-Host "    Instalando $name pelo winget (fonte oficial da Microsoft)..."
    winget install --id $id -e --silent --accept-source-agreements --accept-package-agreements | Out-Host
    Update-SessionPath
}

Write-Host 'YT Baixador: instalação do programa auxiliar' -ForegroundColor White

Step 'Procurando o Python'
function Find-Python {
    foreach ($candidate in @(@('py', '-3'), @('python'))) {
        $cmd = Get-Command $candidate[0] -ErrorAction SilentlyContinue
        if (-not $cmd -or $cmd.Source -like '*WindowsApps*') { continue }
        $pyArgs = @($candidate | Select-Object -Skip 1) + @('-c', 'import sys; print(sys.executable)')
        $exe = & $candidate[0] @pyArgs
        if ($LASTEXITCODE -eq 0 -and $exe) { return $exe.Trim() }
    }
    $installed = Get-ChildItem "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($installed) { return $installed.FullName }
    return $null
}

$python = Find-Python
if (-not $python) {
    Install-WithWinget 'Python.Python.3.13' 'Python'
    $python = Find-Python
}
if (-not $python) {
    Fail 'Python não encontrado. Instale em https://www.python.org/downloads/ (marque "Add python.exe to PATH") e rode de novo.'
}
Ok "Python: $python"

Step "Copiando o programa para $appDir"
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
try {
    Copy-Item -Force -ErrorAction Stop (Join-Path $root 'host\host.py'), (Join-Path $root 'host\host.bat'), (Join-Path $root 'host\testar.py') $appDir
} catch {
    Fail "Não consegui copiar os arquivos: $_"
}
$config = [ordered]@{ extensionDir = (Join-Path $root 'extensao') }
[IO.File]::WriteAllText((Join-Path $appDir 'config.json'), ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Ok 'Arquivos copiados.'

Step 'Instalando/atualizando o yt-dlp (num ambiente isolado, não mexe no seu Python)'
$venvPython = Join-Path $appDir 'venv\Scripts\python.exe'
if (-not (Test-Path $venvPython)) {
    & $python -m venv (Join-Path $appDir 'venv')
    if ($LASTEXITCODE -ne 0) { Fail 'Não consegui criar o ambiente isolado do Python.' }
}
& $venvPython -m pip install -U --disable-pip-version-check --quiet 'yt-dlp[default]'
if ($LASTEXITCODE -ne 0) { Fail 'Falha ao instalar o yt-dlp. Verifique sua internet e rode de novo.' }
Ok ("yt-dlp " + (& $venvPython -m yt_dlp --version))

Step 'FFmpeg (junta vídeo + áudio e converte para MP3)'
Update-SessionPath
if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
    Ok 'Já instalado.'
} else {
    Install-WithWinget 'Gyan.FFmpeg' 'FFmpeg'
    if (Get-Command ffmpeg -ErrorAction SilentlyContinue) { Ok 'Instalado.' } else { Warn 'Instalado, mas só aparece depois de reiniciar o navegador.' }
}

Step 'Node.js ou Deno (o YouTube exige um deles para liberar os downloads)'
if (Get-Command node -ErrorAction SilentlyContinue) {
    Ok ('Node.js ' + (node --version))
} elseif (Get-Command deno -ErrorAction SilentlyContinue) {
    Ok 'Deno já instalado.'
} else {
    Install-WithWinget 'DenoLand.Deno' 'Deno'
    Ok 'Deno instalado.'
}

Step 'Registrando nos navegadores'
$manifestPath = Join-Path $appDir "$hostName.json"
$manifest = [ordered]@{
    name            = $hostName
    description     = 'YT Baixador host'
    path            = (Join-Path $appDir 'host.bat')
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$extensionId/")
}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

$browsers = [ordered]@{
    'Google Chrome'  = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts'
    'Microsoft Edge' = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts'
    'Brave'          = 'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts'
    'Chromium'       = 'HKCU:\Software\Chromium\NativeMessagingHosts'
}
foreach ($name in $browsers.Keys) {
    New-Item -Path "$($browsers[$name])\$hostName" -Value $manifestPath -Force | Out-Null
    Ok $name
}

Step 'Testando'
& $venvPython (Join-Path $appDir 'testar.py')
if ($LASTEXITCODE -ne 0) { Fail "O teste falhou. Veja o arquivo host.log em $appDir" }

Write-Host "`nTudo pronto!" -ForegroundColor Green
Write-Host @"

Agora carregue a extensão (só na primeira vez):
  1. Abra edge://extensions  (ou chrome://extensions)
  2. Ligue o "Modo de desenvolvedor"
  3. Clique em "Carregar sem compactação" e escolha a pasta:
     $(Join-Path $root 'extensao')
  4. Feche e abra o navegador de novo.

Depois é só abrir um vídeo no YouTube e clicar em "Baixar".
"@
