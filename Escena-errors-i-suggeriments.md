# Escena · errors i suggeriments

Fitxer compartit: aquí s'apunta tot el que es troba a Escena (errors i idees de millora).
Les sessions de feedback només hi escriuen; la sessió principal el llegeix i decideix què es fa.

Cada entrada té: data, tipus (**Error** o **Suggeriment**), on passa, què passa i com reproduir-ho.
Quan es resol, s'hi posa l'estat **Resolt** i la versió.

---

## Pendents

### [2026-11-13] Suggeriment · Verificar que els canvis recents funcionen — queda poc
- **On:** canvis publicats a la tanda del 10–13/11.
- **Comprovat a l'aplicació (13/11):** zoom i barra de desplaçament de la forma d'ona, desplaçament horitzontal (touchpad / roda inclinada), Atura/Pausa sobre la cue seleccionada, copiar/enganxar/retallar, edició en bloc, igualar el volum, la cue Nota i el canvi de nom de les cues associades.
- **Falta (cal fer-ho amb el teu equip):** Maj+roda sobre la forma d'ona amb un ratolí real; escoltar el so d'un vídeo per una sortida d'Escena (sincronia); deixar Escena obert més estona que l'apagat de pantalla i veure que no s'apaga ni s'adorm.
- **Estat:** Pendent (només aquestes tres comprovacions)

---

## Resolts

### [2026-11-13] Error · Grups dins de grups: el GO no entra al grup de dins
- **Què passava:** si la primera cue d'un grup «Entra» era un altre grup «Entra», el GO deixava en espera la segona cue del grup de fora en lloc d'entrar al de dins.
- **Com funciona ara (com al QLab):** el GO entra a tots els nivells. Exemple G1[G2[B, C], D]: GO → sona B i queda en espera C; GO → C i en espera D; GO → D i en espera la cue de després de G1. Si el grup de dins és la segona cue, quan li arriba el GO s'executa i hi entra.
- **Estat:** Resolt · publicat a Escena-Windows el 13/11 (després de la 0.8.0)

### [2026-11-13] Suggeriment · Cue d'anotació que no atura l'espectacle
- **Fet:** tipus de cue nou **Nota** (botó amb la icona de nota a la barra). No fa res; ve amb «Continua» marcat, de manera que quan li arriba el GO dispara la cue següent i l'espectacle no s'hi atura. A l'exportació al QLab es converteix en una cue Memo.
- **Estat:** Resolt · publicat a Escena-Windows el 13/11 (després de la 0.8.0)

### [2026-11-13] Suggeriment · Canviar el nom de les cues associades
- **Fet:** quan una cue canvia de nom, les foses i les Atura/Pausa que hi apunten i porten el nom entre «» (els noms que posa Escena, p. ex. «Fosa de «Pluja»») passen a portar el nou. Els noms escrits a mà sense el nom de la cue no es toquen. Es desfà amb Ctrl+Z com qualsevol canvi.
- **Estat:** Resolt · publicat a Escena-Windows el 13/11 (després de la 0.8.0)

### [2026-11-13] Suggeriment · Normalitzar el volum de diverses cues d'àudio i vídeo
- **Fet:** amb diverses cues d'àudio i/o vídeo seleccionades, el panell de propietats té **Iguala el volum**: tries el nivell (per defecte -20 dB) i Escena mesura cada fitxer (entre l'inici i el final de la cue, sense comptar els silencis) i posa a cada cue el volum que la deixa a aquell nivell. Si una cue saturaria (pic per sobre de -1 dBFS), es queda més baixa i ho diu. Els fitxers que no es poden mesurar (p. ex. un vídeo sense so) s'avisen i no es toquen. Es desfà amb Ctrl+Z.
- **Estat:** Resolt · publicat a Escena-Windows el 13/11 (després de la 0.8.0)

---

## Plantilla

```
### [data] Tipus · títol curt
- **On:** pantalla o funció (p. ex. cue d'àudio → propietats → forma d'ona)
- **Què passa:** descripció del problema o de la idea
- **Com reproduir-ho:** passos (només per als errors)
- **Estat:** Pendent
```
