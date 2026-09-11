$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$dist = Join-Path $root 'dist'
$work = Join-Path $env:TEMP 'ytbaixador-build'
$refs = @('/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
$common = @('/nologo', '/optimize+', "/win32icon:$root\instalador\app.ico", "/win32manifest:$root\instalador\app.manifest")

New-Item -ItemType Directory -Force $dist, $work | Out-Null

Write-Host 'Compilando o programa auxiliar...'
& $csc @common /target:exe "/out:$dist\YTBaixador-Host.exe" @refs "$root\host\Host.cs"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o Host.cs' }

Write-Host 'Empacotando a extensão...'
$zip = Join-Path $work 'extensao.zip'
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $root 'extensao'), $zip)

Write-Host 'Compilando o instalador...'
& $csc @common /target:winexe "/out:$dist\YTBaixador-Instalador.exe" @refs `
    "/resource:$zip,extensao.zip" "/resource:$dist\YTBaixador-Host.exe,host.exe" "/resource:$root\instalador\logo.png,logo.png" `
    "$root\instalador\Instalador.cs"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o Instalador.cs' }

Get-ChildItem $dist | ForEach-Object { '{0,-28} {1,8:N0} KB' -f $_.Name, ($_.Length / 1KB) }
