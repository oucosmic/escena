# Escena

**Reproductor de cues per a teatre i espectacles, per a Windows.** Àudio, vídeo, imatges, textos i micròfons en directe, amb GO, foses, grups, encadenaments i tot el que cal per portar el so i la imatge d'una funció. Inspirat en [Linux Show Player](https://github.com/FrancescoCeruti/linux-show-player) i en QLab.

> *English summary below.*

## Descarregar

A **[Releases](https://github.com/oucosmic/escena/releases/latest)** hi ha el zip de l'última versió. Descomprimeix-lo i obre `Escena.exe`: no cal instal·lar res.

- Windows 10 o 11 de 64 bits.
- Per moure'l a un altre ordinador, copia la carpeta sencera (la carpeta `libvlc` és el VLC que reprodueix el vídeo).
- Windows pot avisar que el programa és d'un editor desconegut (no està signat): «Més informació → Executa igualment».

## Què fa

- **Cues:** àudio, vídeo, imatge, text (títols i sobretítols), micròfon, fosa, atura/pausa/reprèn, surt del bucle, espera i grups.
- **En directe:** GO amb protecció de doble GO, cursor d'espera separat de la selecció, atura-ho tot, pànic de dos nivells, escoltar una cue sense tocar l'espectacle, vista de regidor, mode funció (F9) que bloqueja l'edició.
- **Àudio:** sortides WASAPI i ASIO, multicanal, matriu d'encaminament, enviaments, ducking, bucles dins del fitxer.
- **Vídeo:** diverses pantalles, foses a negre, correcció de cantonades del projector, so del vídeo per la sortida que vulguis.
- **Grups com al QLab:** línia de temps, llista, entra, primera i aleatòria.
- **Edició:** desfer i refer, inspector amb pestanyes, copiar i enganxar, editar diverses cues alhora, arrossegar, tecles directes, comprovació abans de la funció, començar des d'un temps, registre d'assaig, empaquetar l'espectacle.
- **Exportar al QLab** (Mac) amb un AppleScript.
- Interfície en català i en anglès. Còpia automàtica i recuperació si es tanca malament.

Manual complet: [LLEGEIX-ME.md](LLEGEIX-ME.md) (també surt com a `Instruccions.html` al costat del programa). Canvis de cada versió: [CHANGELOG.md](CHANGELOG.md).

## Estat

Versió **0.8** (en desenvolupament). Encara no s'ha provat amb targetes multicanal i ASIO, micròfons en directe ni projectors reals: prova-ho bé abans de fer-lo servir en una funció.

## Fet amb intel·ligència artificial

Escena s'ha programat amb l'ajuda d'intel·ligència artificial: el codi l'ha escrit **Claude** (d'Anthropic) a partir de les indicacions, el disseny, les decisions i les proves de Metromàtic. Tot el que fa el programa s'ha decidit i revisat a partir de l'ús real en escena, però pot tenir errors que una persona no hauria comès: si en trobes, obre una *issue*.

## Compilar

Cal el [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```
cd Escena
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ..\Escena-Windows
```

C# · WPF · .NET 8 · [NAudio](https://github.com/naudio/NAudio) · [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp). Vegeu [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

La carpeta `Demo` té un espectacle d'exemple amb tons, una imatge i un vídeo curt d'un hipopòtam (`hipopotam_en_moviment.mp4`, sense so), generat amb Claude a partir d'instruccions de Metromàtic.

## Llicència

Copyright © 2026 Metromàtic

Escena és programari lliure: el pots redistribuir i modificar segons els termes de la **GNU General Public License versió 3** publicada per la Free Software Foundation. Es distribueix amb l'esperança que sigui útil, però **sense cap garantia**. Vegeu [LICENSE](LICENSE).

---

## English summary

**Escena** is a free cue player for theatre and live shows on Windows: audio, video, images, text/surtitles and live microphones, with GO, fades, QLab-style groups, auto-continue/follow, multichannel routing (WASAPI/ASIO), ducking, multiple screens with projector corner correction, a stage-manager view, rehearsal log, start-from-time and an AppleScript export to QLab. The interface is available in Catalan and English.

Download the latest zip from **Releases**, unzip and run `Escena.exe` (Windows 10/11, 64-bit, no installation).

It was written with the help of AI (Claude, by Anthropic), directed, designed and tested by Metromàtic. Still in development (0.x): test it carefully before using it in a show.

License: **GPL-3.0**. Copyright © 2026 Metromàtic.
