# Programari de tercers

Escena (llicència GPL-3.0) fa servir aquests components. Els que van dins del zip del programa s'hi distribueixen sense modificar.

| Component | Per a què | Llicència | Web |
|---|---|---|---|
| **NAudio** 2.2.1 | Àudio: WASAPI, ASIO, lectura de fitxers, mesclador | MIT | https://github.com/naudio/NAudio |
| **LibVLCSharp** 3.10.2 | Connexió amb el VLC des de .NET | LGPL-2.1 | https://code.videolan.org/videolan/LibVLCSharp |
| **VLC / libVLC** 3.0.24 (`libvlc\`) | Descodificar vídeo | LGPL-2.1 o posterior (alguns connectors, GPL-2.0 o posterior) | https://www.videolan.org/vlc/libvlc.html |
| **.NET 8** (inclòs a l'exe) | Entorn d'execució i WPF | MIT | https://github.com/dotnet/runtime |

El codi font de VLC és a https://code.videolan.org/videolan/vlc i el de LibVLCSharp, a l'adreça de la taula. Les biblioteques del VLC són a la carpeta `libvlc\` del programa i es poden substituir per una altra versió compatible.

Escena s'inspira en el funcionament de [Linux Show Player](https://github.com/FrancescoCeruti/linux-show-player) (GPL-3.0) i de QLab (Figure 53), però no en conté codi. «QLab» és una marca de Figure 53; l'exportació al QLab només genera un AppleScript per a aquest programa i no hi té cap relació oficial.
