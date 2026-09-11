# YT Baixador

Extensão para Edge/Chrome que baixa vídeos e áudios do **YouTube, TikTok, X e Instagram**.
Você escolhe vídeo (144p até 4K) ou só áudio (M4A, Opus, MP3, WAV ou FLAC), pode recortar
só um trecho, e o arquivo vai direto para a sua pasta Downloads. Nada passa por site de terceiros.

Criador: Skymer#9220 (Discord)

## Instalação

1. Baixe o **[YTBaixador-Instalador.exe](https://github.com/SkymerLight/Baixador-YT/raw/main/dist/YTBaixador-Instalador.exe)**.
2. Dê dois cliques nele. Se o Windows mostrar "O Windows protegeu o computador", clique em
   **Mais informações > Executar assim mesmo** (o aviso aparece porque o instalador não tem
   assinatura digital paga).
3. Clique em **Instalar** e espere. Ele baixa o motor de download e o que mais faltar.
4. Na tela final, siga os passos: abrir a página de extensões, ligar o **Modo de desenvolvedor**,
   clicar em **Carregar sem compactação** e escolher a pasta que o instalador mostra
   (tem um botão que copia o caminho).

Não precisa de Python, nem de administrador. Tudo fica em `%LOCALAPPDATA%\YTBaixador`.
Precisa de Windows 10 ou 11.

## Uso

- **No YouTube:** clique em **YT Baixador**, ao lado de "Compartilhar"
  (não confunda com o "Baixar" do próprio YouTube, que é o download offline do Premium).
  Nos Shorts o botão fica flutuando no canto de baixo.
- **Botão direito:** em qualquer vídeo ou link do YouTube, TikTok, X ou Instagram, escolha
  **Baixar com YT Baixador**. Abre uma janelinha já com o vídeo carregado.
- **Ícone da extensão:** abre com o vídeo da aba atual, ou cole um link. Também mostra o
  histórico de downloads e as configurações.

### Recortar um trecho

Ligue **Recortar só um trecho** e escolha o início e o fim:
- **arraste as bolinhas** da barra (o vídeo acompanha para você ver onde está);
- ou digite (tipo `1:30` e `2:45`);
- ou use **Agora** para pegar o ponto que está tocando.

**Tocar trecho** toca só aquele pedaço e pausa sozinho no fim. No popup a prévia é só o áudio
e tem também o botão **Ouvir**; no painel da página do vídeo a prévia usa o próprio player.
Só o trecho é baixado, então fica rápido.

### Qual áudio escolher?

O YouTube entrega o áudio em ~130 kbps. **M4A** (o "MP4 de áudio") e **Opus** são esse áudio
original, sem conversão: são a melhor qualidade possível. MP3 de 320 kbps não soa melhor, só
fica maior; use MP3 quando o aparelho só tocar MP3. WAV e FLAC são para editar.

### Configurações (engrenagem no popup)

| Opção | O que faz |
| --- | --- |
| Pasta de destino | Onde os arquivos são salvos (padrão: Downloads). |
| Priorizar H.264 | O vídeo toca em qualquer player e celular. Acima de 1080p o YouTube só tem VP9/AV1. |
| Tirar patrocínios | Remove o "esse vídeo é patrocinado por..." dos vídeos do YouTube, usando o SponsorBlock. Não vale quando o recorte está ligado. |
| Igualar o volume das músicas | Deixa os MP3 no mesmo volume (-14 LUFS, o padrão do Spotify e do YouTube). |
| Usar meu login do navegador | Para posts que só abrem logado (quase todo o Instagram, alguns do X e TikTok). O navegador pede permissão na primeira vez. Os cookies desses sites vão só para o programa no seu PC e são apagados logo depois. |
| Motor de download > Atualizar | Atualiza o yt-dlp. Faça isso quando algum download começar a falhar. |
| Extensão > Procurar | Confere se tem versão nova no GitHub. |

## Sites

| Site | Sem login | Com "Usar meu login do navegador" |
| --- | --- | --- |
| YouTube | Funciona | Libera vídeos com restrição de idade* |
| TikTok | Funciona (alguns posts são bloqueados por país) | Funciona |
| X (Twitter) | Funciona para posts públicos | Libera posts que pedem login |
| Instagram | Quase sempre pede login | Funciona |

\* O login do YouTube não é enviado; a opção vale só para Instagram, X e TikTok.

## Atualizações

A extensão confere o GitHub a cada 6 horas. Quando tem versão nova, aparece
**Atualizar agora** no popup: o programa auxiliar baixa a versão nova, troca os arquivos
(inclusive ele mesmo) e a extensão recarrega sozinha. Depois é só recarregar as abas abertas.

## Problemas comuns

| Mensagem | O que fazer |
| --- | --- |
| "O programa auxiliar não está instalado" | Rode o `YTBaixador-Instalador.exe`. |
| "Recarregue a página (F5)" | A extensão foi atualizada; recarregue a aba. |
| "O recorte não foi aplicado... versão antiga" | Em `edge://extensions`, clique em **Recarregar** no YT Baixador. |
| Erro 403 / "verificação anti-robô" | Engrenagem > Motor de download > **Atualizar** e tente de novo. |
| "Só abre com login" | Ligue **Usar meu login do navegador** e entre na sua conta do site no navegador. |
| A extensão sumiu ou ficou desativada | Deixe o **Modo de desenvolvedor** ligado na página de extensões. |

Os registros ficam em `%LOCALAPPDATA%\YTBaixador\host.log` e `instalador.log`.

## Desinstalar

Rode o `YTBaixador-Instalador.exe` de novo e clique em **Desinstalar**. Depois remova a
extensão em `edge://extensions`. Seus downloads continuam onde estavam.

## Segurança

- O instalador só baixa programas das páginas oficiais no GitHub (yt-dlp, FFmpeg do projeto
  yt-dlp e Deno) e confere o código SHA-256 de cada um antes de usar. Se não bater, apaga e para.
- Só esta extensão consegue conversar com o programa auxiliar (ID fixo no registro do navegador).
- A atualização automática só aceita pacotes deste repositório com a mesma chave da extensão.
- Quem publica neste repositório consegue mandar código para todo mundo que usa: deixe a
  verificação em duas etapas ligada na conta do GitHub.

## Para desenvolver

```
extensao/     a extensão (Manifest V3)
host/         programa auxiliar em C# (Host.cs), fala com a extensão por Native Messaging
instalador/   instalador em C# (Instalador.cs), ícone e manifesto do Windows
dist/         os .exe prontos (o botão "Atualizar agora" baixa o host daqui)
build.ps1     compila tudo com o compilador que já vem no Windows (.NET Framework 4.8)
```

- Compilar: `powershell -ExecutionPolicy Bypass -File build.ps1`
- Testar o programa auxiliar pelo terminal:
  `%LOCALAPPDATA%\YTBaixador\YTBaixador-Host.exe --test LINK [720 | mp3-320 | m4a] [section=90-100] [sponsorblock=true]`
- Publicar uma versão: aumente o `"version"` em `extensao/manifest.json`, rode o `build.ps1`,
  faça commit (incluindo a pasta `dist/`) e `git push`. Em até 6 horas todo mundo vê o aviso.
- A cópia de desenvolvimento (com `.git`) não se atualiza pelo botão, para não apagar mudanças
  que ainda não foram enviadas.
- O ID da extensão é fixo (`enedkkbdeanincbhpmaokfjmhjlcpmop`) por causa do campo `key` no
  `manifest.json`. Não apague esse campo.
