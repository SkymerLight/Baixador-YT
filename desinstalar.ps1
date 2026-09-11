$hostName = 'com.ytbaixador.host'
$appDir = Join-Path $env:LOCALAPPDATA 'YTBaixador'

foreach ($key in @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts',
    'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts',
    'HKCU:\Software\Chromium\NativeMessagingHosts')) {
    Remove-Item -Path "$key\$hostName" -Force -ErrorAction SilentlyContinue
}
Write-Host 'Registro nos navegadores removido.' -ForegroundColor Green

if (Test-Path $appDir) {
    Remove-Item -Recurse -Force $appDir -ErrorAction SilentlyContinue
    if (Test-Path $appDir) {
        Write-Host "Feche o navegador e rode de novo para apagar $appDir" -ForegroundColor Yellow
    } else {
        Write-Host "Pasta $appDir apagada." -ForegroundColor Green
    }
}
Write-Host 'Para tirar a extensão, remova-a em edge://extensions (ou chrome://extensions).'
