# Canvis

Numeració: MAJOR.MENOR.CORRECCIÓ. Mentre el primer número sigui 0, Escena encara és en desenvolupament.
La versió del format dels fitxers `.show` va a part (ara és la 5) i només canvia quan canvia com es desen els espectacles.

## 0.8.0 — 2026-11-13

Novetats:

- **Mode funció** (F9 o el cadenat de la barra): bloqueja l'edició i mostra en gran la cue en espera amb les notes, la de després i les cues en marxa amb el compte enrere.
- **Inspector a sota de la llista, amb pestanyes:** «General» i les de cada tipus de cue («Fitxer i temps», «Nivells i foses», «Sortides»…). La forma d'ona ocupa tot l'ample. Ctrl+Tab canvia de pestanya; l'alçada es pot arrossegar i es recorda. En mode funció l'inspector s'amaga i la llista guanya alçada.
- **Icona i color de cada tipus de cue** a la llista, a la botonera i a l'inspector.
- **Desfer i refer** (Ctrl+Z, Ctrl+Y o Ctrl+Maj+Z): afegir, esborrar, moure, agrupar, enganxar i canviar propietats, fins a 100 passos. No atura el que sona.
- **Barra d'eines amb icones:** en passar-hi el punter surt el nom, què fa i la drecera. Els botons amb un + verd afegeixen una cue.
- **Moure diverses cues alhora** amb Ctrl+↑↓ o arrossegant-les.
- Pausar, reprendre i tallar fan una rampa de 12 ms per no fer «clic».
- Avís en tancar amb cues en marxa i en obrir un espectacle fet amb un Escena més nou.
- Amb tot en pausa, una cue disparada amb tecla directa o botonera sona i el botó torna a dir «Pausa».

Correccions:

- Tornar a disparar un grup aleatori ja no talla la fill que sona.
- «Encadena amb l'anterior» del registre d'assaig té en compte els grups.
- Cada finestra d'Escena té la seva recuperació: tancar-ne una no esborra la de l'altra.
- Una tecla mantinguda (Esc, Maj+Esc, Espai, Supr, tecles directes) compta un sol cop.
- Un fitxer que falla mentre sona acaba només aquella cue; si una sortida d'àudio cau, es torna a obrir sola.
- Si el projector es desendolla, la finestra de vídeo no es queda tapant Escena, i hi torna quan es torna a endollar.
- Canviar les sortides demana confirmació si hi ha cues en marxa, i no toca res si no ha canviat res.
- Empaquetar no deixa l'espectacle a mitges si falla una còpia.
- Maj+↑↓ ja no selecciona cues amagades dins d'un grup plegat.
- La forma d'ona es torna a calcular si el fitxer se substitueix pel mateix nom.

## 0.7.0 — 2026-11-11

- **Zoom a la forma d'ona:** roda del ratolí al voltant del punter, Maj+roda o botó del mig per desplaçar-se, i els botons 🔍+ 🔍− «Tot», «▏Inici» i «Final▕» per afinar l'inici i el final fins al mil·lisegon (els camps d'inici i final ara tenen tres decimals).
- **Ordinador despert:** mentre Escena és obert, Windows no adorm l'ordinador ni apaga la pantalla, perquè el projector no perdi el senyal mentre s'espera el primer GO. Es pot desactivar a ⚙ Ajustos.
- **Atura/Pausa nova:** si en crear-la hi ha una cue seleccionada, actua sobre aquella (si és una fosa, sobre la cue de la fosa); sense cap cue seleccionada, sobre totes.
- Espectacle d'exemple actualitzat al format actual (amb un grup).

## 0.6.0 — 2026-11-10

- **Exporta al QLab (Mac):** crea una carpeta amb els fitxers, un AppleScript que fa les cues al QLab 5 i un informe del que cal acabar a mà.
- **So del vídeo per:** cada cue de vídeo tria per quina sortida d'àudio surt el so (predeterminat de Windows o una sortida d'Escena, amb volum general, foses, pausa i ducking, sincronitzat amb la imatge).
- **Copiar, retallar, enganxar i duplicar** diverses cues (Ctrl+C/X/V/D i menú del botó dret), també entre espectacles. Supr esborra totes les seleccionades.
- **Editar diverses cues alhora:** amb diverses cues seleccionades, el panell mostra les propietats comunes i els canvis s'apliquen a totes. Botó «Numera».
- Al final de la llista, clicar una cue amb el ratolí la posa en espera.
- La versió surt al títol de la finestra. Instruccions en HTML al costat del programa.

## 0.5.0

- Matriu de sortides i enviaments, sortides multicanal, ducking.
- Grups com al QLab (contenen les seves cues) amb cinc modes: línia de temps, llista, entra, primera i aleatòria.
- Començar des d'un temps, tecles directes, cues de micròfon i de text (sobretítols).
- Comprovació abans de la funció, empaquetar l'espectacle, registre d'assaig, vista de regidor.
- Traduccions (català i anglès), diverses pantalles i correcció del projector.

## 0.4.0

- Cursor d'espera separat de la selecció, protecció de doble GO, números lliures i notes, pànic de dos nivells.
- Fosa de volum i panorama, bucles dins del fitxer amb «Surt del bucle», escoltar sense tocar l'espectacle.
- Còpia automàtica i recuperació després d'un tancament inesperat.

## 0.1.0

- Primera versió: cues d'àudio, vídeo, imatge, fosa, atura i espera, sortides d'àudio, llista i botonera.
