# YT Baixador

Extensão para Edge/Chrome que coloca um botão **Baixar** nos vídeos do YouTube.
Você escolhe vídeo (144p até 4K) ou só áudio (M4A, Opus, MP3, WAV ou FLAC), pode
recortar só um trecho, e o arquivo vai direto para a sua pasta Downloads. Nada passa
por site de terceiros.

Criador: Skymer#9220 (Discord)

## Como funciona

```
YouTube (botão Baixar) ─┐
                        ├─> extensão ──(Native Messaging)──> host.py ──> yt-dlp + FFmpeg ──> Downloads
Popup (colar link) ─────┘
```

- **extensao/**: a extensão em si (é essa pasta que você carrega no navegador).
- **host/**: o programa auxiliar. O navegador abre ele sozinho quando precisa e fecha
  depois de 1 minuto parado. Só esta extensão tem permissão de falar com ele.
- O yt-dlp faz o trabalho pesado (decifrar o YouTube); o FFmpeg junta vídeo + áudio,
  recorta e converte. Uma extensão sozinha não consegue mais fazer isso: o YouTube bloqueia.

## Instalação (uma vez só)

1. No GitHub, clique em **Code > Download ZIP** e extraia numa pasta fixa (tipo `Documentos`).
   Não apague essa pasta depois: o navegador carrega a extensão direto dela.
2. Dê dois cliques em **instalar.bat** e espere terminar. Ele instala o que faltar
   (Python, yt-dlp, FFmpeg e Deno) e registra o programa no Edge, Chrome, Brave e Chromium.
3. Abra `edge://extensions` (ou `chrome://extensions`), ligue o **Modo de desenvolvedor**,
   clique em **Carregar sem compactação** e escolha a pasta `extensao`.

Precisa de Windows 10 ou 11.

## Uso

- **No YouTube:** abra um vídeo e clique em **YT Baixador**, ao lado de "Compartilhar"
  (não confunda com o "Baixar" do próprio YouTube, que é o download offline do Premium).
  Nos Shorts o botão fica flutuando no canto de baixo.
- **Pelo ícone da extensão:** cole qualquer link do YouTube. Também mostra o histórico
  de downloads e as configurações.

### Recortar um trecho

Ligue **Recortar só um trecho**, digite o início e o fim (tipo `1:30` e `2:45`) e baixe.
Só o trecho é baixado, então fica rápido. Antes de baixar dá para conferir:
- **Tocar trecho**: toca só o trecho e pausa sozinho no fim;
- **Agora**: usa o ponto que está tocando como início ou fim;
- clicar na barrinha: pula para aquele ponto.

No painel da página do vídeo a prévia usa o próprio player do YouTube (com imagem).
No popup a prévia é só o áudio, e tem também o botão **Ouvir** para tocar livremente e
achar o ponto certo.

### Qual áudio escolher?

O YouTube entrega o áudio em ~130 kbps. **M4A** (o "MP4 de áudio") e **Opus** são esse
áudio original, sem conversão: são a melhor qualidade possível. MP3 de 320 kbps não soa
melhor, só fica maior; use MP3 quando o aparelho só tocar MP3. WAV e FLAC são para editar.

### Configurações (engrenagem no popup)

- **Pasta de destino**: padrão é Downloads.
- **Priorizar H.264**: ligado, o vídeo toca em qualquer player e celular. Acima de 1080p o
  YouTube só oferece VP9/AV1, então nesses casos ele usa o que tiver.
- **Motor de download > Atualizar**: atualiza o yt-dlp. Faça isso quando algum download
  começar a falhar: normalmente é o YouTube que mudou algo e o yt-dlp já tem a correção.
- **Extensão > Procurar**: confere se tem versão nova no GitHub.

## Atualizações

A extensão confere o GitHub a cada 6 horas. Quando tem versão nova, aparece
**Atualizar agora** no popup: o programa auxiliar baixa a versão nova do GitHub, troca os
arquivos e a extensão recarrega sozinha. Depois é só recarregar as abas do YouTube.

### Para publicar uma versão nova (criador)

1. Faça as mudanças e aumente o `"version"` em `extensao/manifest.json` (ex.: 1.1.0 para 1.2.0).
2. Mande para o GitHub (`git commit` + `git push`).

Pronto: em até 6 horas todo mundo vê o aviso. A cópia com a pasta `.git` (a de
desenvolvimento) não se atualiza pelo botão, para não apagar mudanças que ainda não foram
enviadas; nela use `git pull`.

## Problemas comuns

| Mensagem | O que fazer |
| --- | --- |
| "O programa auxiliar não está instalado" | Rode o `instalar.bat` e reinicie o navegador. |
| "Recarregue a página (F5)" | A extensão foi atualizada; recarregue a aba do YouTube. |
| "O recorte não foi aplicado... versão antiga" | Em `edge://extensions`, clique em **Recarregar** no YT Baixador. Acontece quando os arquivos mudam no disco e o navegador ainda roda a versão anterior. |
| Erro 403 / "verificação anti-robô" | Clique em **Atualizar** no motor de download e tente de novo. |
| "Restrição de idade" / "Vídeo privado" | O YouTube exige login para esse vídeo; não é suportado. |

O log do programa auxiliar fica em `%LOCALAPPDATA%\YTBaixador\host.log`.
Para testar sem o navegador: `%LOCALAPPDATA%\YTBaixador\venv\Scripts\python.exe %LOCALAPPDATA%\YTBaixador\testar.py LINK`.

## Desinstalar

Rode **desinstalar.bat** e remova a extensão em `edge://extensions`. Seus downloads, o
FFmpeg, o Python e o Node.js ficam onde estão.

## Detalhes técnicos

- O ID da extensão é fixo (`enedkkbdeanincbhpmaokfjmhjlcpmop`) por causa do campo `key` no
  `manifest.json`. Se apagar esse campo, o ID muda e o programa auxiliar para de aceitar a extensão.
- A atualização automática só aceita um pacote do repositório `SkymerLight/Baixador-YT` que
  tenha a mesma `key`, então não dá para trocar a extensão por outra.
