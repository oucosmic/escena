# Escena 0.7 — reproductor de cues per a Windows

Programa nou per a Windows inspirat en Linux Show Player. Reprodueix àudio, vídeo i imatges. No té llums ni MIDI. Programari lliure (GPL-3.0), fet amb l'ajuda d'IA (vegeu [README.md](README.md)).

## Carpetes

| Carpeta | Què hi ha |
|---|---|
| `Escena-Windows\` | El programa compilat (no és al repositori: es baixa de les versions publicades). `Escena.exe`, la carpeta `libvlc` (el VLC per al vídeo), les instruccions en HTML i la llicència. No cal instal·lar res, però **per moure'l a un altre ordinador cal copiar la carpeta sencera**. |
| `Escena\` | El codi font (C# / WPF / .NET 8 + NAudio + LibVLCSharp). |
| `Demo\Demo.show` | Espectacle d'exemple amb tons, una imatge i un vídeo (`hipopotam_en_moviment.mp4`, 6 s, sense so, generat amb Claude a partir d'instruccions de Metromàtic). |

## Tipus de cue

| Cue | Què fa |
|---|---|
| **Àudio** | Reprodueix WAV, MP3, FLAC, AIFF, M4A o WMA. Té volum (dB), panorama, inici i final dins del fitxer (es poden arrossegar sobre la forma d'ona, que té **zoom**: roda del ratolí al voltant del punter; per desplaçar-se, la barra que surt a sota amb el zoom, el lliscament horitzontal del touchpad o la roda inclinada, Maj+roda o el botó del mig; i els botons 🔍+ 🔍− «Tot», «▏Inici» i «Final▕» per ampliar al voltant de cada punt i afinar-lo fins al mil·lisegon), bucle, fosa d'entrada i de sortida amb corba, i **bucles dins del fitxer** (vegeu més avall). |
| **Vídeo** | Reprodueix MP4, MOV, MKV, AVI, WMV, WebM, MPG… amb el VLC. Té volum del so, inici i final, bucle, i fosa des de negre i a negre amb corba. En acabar, la pantalla torna a negre. |
| **Imatge** | Mostra JPG, PNG, BMP, GIF o TIFF a la pantalla, amb fosa des de negre i a negre. Té un temps en pantalla, o 0 perquè es quedi fins que l'aturis. |
| **Fosa** | Porta una cue a un **volum i/o un panorama** en X segons, amb corba. Si vols, en acabar l'atura. En vídeos i imatges també enfosqueix la imatge (es pot desactivar per fer només el so). |
| **Atura / Pausa / Reprèn / Surt del bucle** | Actua sobre una cue concreta o sobre totes. **Si en crear-la hi ha una cue seleccionada, actua sobre aquella** (si és una fosa, sobre la cue de la fosa); sense cap cue seleccionada, sobre totes. En triar una **cue destí** actua només sobre aquella (la casella «Totes» es desmarca sola); si marques «Totes», actua sobre totes les que estan en marxa. En aturar pot fer servir la fosa de cada cue o una fosa fixa. |
| **Text** | Títols, rètols i sobretítols a la pantalla: mida, color, posició (dalt, mig, baix), franja fosca darrere, temps en pantalla i foses. Amb «Treu els altres textos» cada text nou substitueix l'anterior. |
| **Micròfon** | Entrada en directe (micròfon o línia) cap a les sortides, amb volum, panorama, foses, matriu, enviaments i ducking. Sona fins que l'atures. |
| **Espera** | Una pausa cronometrada. Combinada amb «Segueix», serveix per fer seqüències automàtiques. |
| **Grup** | **Conté** altres cues, que van just a sota, sagnades (com al QLab). Vegeu «Grups» més avall. |

Corbes de fosa: lineal, **exponencial** (passos iguals de dB, la que sona més «regular»), en S, quadràtica (lenta al principi) i quadràtica inversa (ràpida al principi).

Cada cue té un **número lliure** («1», «1.5», «2A»…; les noves en reben un automàticament) i unes **notes per a l'operador**, que es veuen a la llista i a «En espera». Si dos números es repeteixen, surt un avís.

**Editar diverses cues alhora:** selecciona'n unes quantes (Ctrl+clic o Maj+clic) i el panell de propietats mostra només les que tenen totes (per exemple, dues cues d'àudio: sortida, volum, panorama, bucle, foses i corbes; una cue d'àudio i una espera: només pre-espera, post-espera, encadenament i notes). Els camps en blanc vol dir que tenen valors diferents. El que hi posis (Retorn o canviant de camp) s'aplica a totes. També hi ha **Numera**: torna a numerar les seleccionades per ordre de llista, amb un inici i un pas (per exemple 10, 10.5, 11…).

Cada cue també pot tenir una **tecla directa** (F1, Ctrl+1, una lletra…) que la dispara a més del GO, sense moure la cue en espera. Es tria al camp «Tecla directa»: hi fas clic i prems la tecla (Retrocés la treu). Les tecles que ja fa servir Escena (Espai, Esc, P, E, fletxes…) no es poden triar.

## Grups

Un grup **conté** les seves cues («fills»): a la llista van just a sota del grup, sagnades cap a la dreta. Amb el triangle ▾ / ▸ el plegues o el desplegues; plegat funciona igual, només que no se'n veuen les fills.

**Fer grups i posar-hi cues:**
- Selecciona unes quantes cues (Ctrl+clic o Maj+clic) i prem **+ Grup** (o Ctrl+G): es crea un grup on era la primera i les conté totes. Sense res seleccionat, «+ Grup» crea un grup buit.
- **Arrossega** una cue damunt d'un grup (al mig de la fila: es marca tota amb blau) i hi entra com a última fill. Si la deixes a la vora de dalt o de baix d'una fila, va davant o darrere d'aquella cue, al seu nivell (també dins d'un grup).
- Una cue nova va després de la seleccionada i al mateix nivell: si la seleccionada és dins d'un grup, la nova també.
- **⇤ Treu del grup** (al panell de propietats) treu una cue del grup i la deixa just després. **Desagrupa** treu el grup i deixa les seves cues a la llista.
- Moure o esborrar un grup el mou o l'esborra amb tot el que conté (Escena avisa abans d'esborrar-lo).

**Modes (com al QLab):**
- **Línia de temps:** totes les fills comencen alhora; la **pre-espera** de cadascuna és el moment en què sona dins del grup. El GO passa a la cue de després del grup.
- **Llista:** les fills sonen una darrere l'altra. Es poden barrejar i fer que tornin a començar en acabar. El GO passa a la cue de després del grup.
- **Entra:** el GO dispara la primera fill i deixa en espera la segona; cada GO dispara la fill següent i, en acabar, surt del grup. Serveix per ordenar la llista en seccions.
- **Primera:** el GO dispara només la primera fill; les altres sonen si estan encadenades amb «Continua» o «Segueix». El GO passa a la cue de després del grup.
- **Aleatòria:** cada GO dispara una fill a l'atzar, sense repetir-ne cap fins que han sonat totes.

Dins de les línies de temps, les llistes i els grups aleatoris, és el grup el que decideix quan sona cada fill: els «Continua» i «Segueix» de les fills no fan res (la comprovació ho avisa). Aturar un grup atura tot el que conté.

Els grups de la versió anterior (que apuntaven a cues d'altres llocs de la llista) es converteixen sols en obrir l'espectacle: aquelles cues passen a ser dins del grup, just a sota.

## Sortides, matriu i enviaments

- Cada cue d'àudio o de micròfon té una **sortida principal** i, si vols, **enviaments** a altres sortides (monitors, efectes d'escenari…), cadascun amb el seu nivell en dB.
- El botó **Matriu…** obre una graella: files = canals de la cue, columnes = canals de la sortida. A cada casella hi poses el nivell en dB (buida = no hi va). El botó 🔊 de cada columna fa sonar un to per aquell canal, per saber quin altaveu és. Quan la matriu no és la de per defecte, el botó diu «Matriu ✱».
- Les sortides poden tenir **més de dos canals**: en WASAPI compartit es fan servir els del dispositiu (per exemple 8 en una targeta 7.1); en exclusiu i ASIO, els que diguis a «Canals».

## Ducking

Marca «Ducking» en una cue d'àudio o de micròfon (per exemple, una veu o un avís) i, mentre sona, **les altres cues baixen** els dB que diguis, amb el temps de baixada i de pujada que triïs. Quan s'atura, tornen a pujar. També baixa el so dels vídeos.

## Bucles dins del fitxer («Surt del bucle»)

A una cue d'àudio pots marcar trossos que es repeteixen (botó **+ Bucle**: inici, final i repeticions). Es veuen en groc sobre la forma d'ona.

- Amb **repeticions = 0**, el tros es repeteix fins que se'n surt: amb una cue **«Surt del bucle»** (que es dispara amb GO com qualsevol altra) o amb el botó **🔁 Surt del bucle** del panell «En curs». Llavors s'acaba la volta i la música continua fins al final.
- Amb **repeticions = N**, es repeteix N vegades més i continua sol.

## Encadenament

Cada cue té **pre-espera** i **post-espera**, i pot fer una d'aquestes tres coses en acabar:

- **Cap**: espera el GO.
- **Continua**: dispara la cue següent quan aquesta comença, després de la post-espera.
- **Segueix**: dispara la cue següent quan aquesta acaba, després de la post-espera.

## Vistes

**Hi ha dos cursors separats:**

- **En espera (▶ verd):** la cue que dispararà el proper GO. Es mou amb **↑ ↓**, amb **doble clic** sobre una cue, amb **Retorn** (posa en espera la seleccionada) o amb el botó «▶ Posa en espera».
- **Seleccionada (blau):** la que edites al panell de propietats. Es tria amb un clic o amb **Maj+↑ ↓**. Així pots editar una cue mentre corre l'espectacle sense moure el GO.

Les vistes:

- **Llista** (F5).
- **Botonera** (F6): una graella de botons. **Un clic dispara la cue**, i si ja sona, l'atura. **Amb el botó dret se selecciona** per editar-la. La vora verda marca la cue en espera. El color indica l'estat: verd sona, groc pre-espera, taronja s'està aturant, blau-gris en pausa, vermell error.

## Controls en directe

| Tecla / botó | Acció |
|---|---|
| Espai / GO | Dispara la cue en espera. **Protecció de doble GO:** s'ignoren els GO que arribin massa seguits (250 ms per defecte, a ⚙ Ajustos). |
| Esc / ATURA-HO TOT | Atura-ho tot, cada cue amb la seva fosa de sortida. Si el tornes a prémer abans de 3 s, talla de cop. |
| Maj+Esc / PÀNIC | **Dos nivells:** el primer toc fa una fosa curta de tot (0,5 s per defecte, a ⚙ Ajustos) i cancel·la els encadenaments; el segon toc talla de cop. Funciona fins i tot mentre escrius en un camp. |
| P / Pausa | Pausa o reprèn tot |
| E / 🎧 Escolta | Escolta la cue seleccionada sense tocar l'espectacle (vegeu més avall). Torna a prémer per parar. |
| Volum general | Afecta totes les sortides. Doble clic el torna a 0 dB. |
| 🔁 / ⏸ / ■ al panell «En curs» | Surt del bucle · pausa · atura una sola cue |
| ↑ ↓ · Retorn · doble clic | Mou la cue en espera · posa en espera la seleccionada |
| Maj+↑ ↓ · Supr · Ctrl+↑↓ | Canvia la seleccionada · elimina-la · mou-la (entre les del seu nivell) |
| Ctrl+G | Fa un grup amb les cues seleccionades |
| Ctrl+C · Ctrl+X · Ctrl+V · Ctrl+D | Copia, retalla, enganxa i duplica les cues seleccionades (Ctrl+clic o Maj+clic per triar-ne diverses). Un grup es copia amb tot el que conté. S'enganxen després de l'última seleccionada i al seu nivell (dins d'un grup, si és dins d'un grup). Reben números nous si els seus ja hi són, i les foses i aturades copiades amb la seva cue apunten a la còpia. Es pot enganxar en un altre espectacle o en una altra finestra d'Escena. També al menú del botó dret de la llista. Supr amb diverses seleccionades les esborra totes. |
| Arrossegar i deixar | Reordena les cues, tant a la llista com a la botonera. Una línia blava marca on quedarà. |

## Eines (botó «Eines ▾» i «✓ Comprova»)

- **✓ Comprova (abans de la funció):** busca fitxers que falten, sortides que no s'han pogut obrir, entrades de micròfon desconnectades, cues buides o amb el destí esborrat, foses que van abans de la seva cue, «Segueix» que no arribaran mai (bucles), encadenaments que s'aturen a si mateixos, tecles repetides, números repetits, volums massa alts i canvis sense desar. Doble clic en una línia per anar a la cue.
- **Comença des de… (assaig):** tries una cue i un temps («com si fóssim a 2:00 de la cue 14»). Escena calcula els encadenaments automàtics: les cues que ja sonarien comencen a mitges, les foses ja fetes s'apliquen, les que encara han de venir es disparen al seu moment i queda en espera la cue que tocaria. Si vols, també engega les cues anteriors que encara sonarien (bucles, imatges fixes, micròfons). Només pot saber el que és automàtic: els GO manuals d'entremig no els pot endevinar.
- **Registre d'assaig:** cada cue disparada amb l'hora, com s'ha disparat (GO, automàtica, tecla, botonera, grup) i el temps des de l'anterior. Selecciona un GO i prem **«Encadena amb l'anterior»**: la cue anterior passa a disparar-la sola amb la post-espera real que hi ha hagut. Es pot exportar a CSV i es desa sol cada dia a `%APPDATA%\Escena\registre\`.
- **Vista de regidor:** finestra a part amb lletra gran: la cue en curs, el temps que li queda, la següent i les seves notes, i el rellotge. F11 = pantalla completa, F10 = la mou a la pantalla següent. Espai fa GO també des d'aquí.
- **Empaqueta l'espectacle:** tries una carpeta i hi copia l'espectacle i tots els fitxers (a «Fitxers\»). A partir d'aquí treballes amb la còpia, que pots portar sencera a un altre ordinador.
- **Importa sobretítols:** un fitxer de text on cada bloc separat per una línia en blanc es converteix en una cue de text a baix de la pantalla. Cada GO en mostra un i treu l'anterior.
- **Exporta al QLab (Mac):** crea una carpeta «‹espectacle› (QLab)» amb `media\` (tots els fitxers), `Importa a QLab.applescript` i `Informe QLab.txt`. Al Mac: obre el QLab 5 amb un espai de treball, fes doble clic a l'script i prem ▶ a l'Editor de Scripts. Les cues es creen al final de la llista: números, noms, notes, esperes, Continua/Segueix, fitxers, volums, inici/final, bucles i slices, textos, foses, aturades, pauses, Devamp, esperes i grups amb el seu mode. La fosa d'entrada i el temps en pantalla es fan amb un grup «Timeline». El que no es pot traslladar (sortides i matriu, enviaments, ducking, panorama, tecles directes, corbes, correcció del projector) surt a l'informe. Si el QLab no accepta alguna propietat, l'script continua i ho diu al final. El codi és a part, a `Escena\QLab\QLabExporter.cs`. **No s'ha provat encara en un Mac amb QLab.**

## Idioma

A ⚙ Ajustos es tria l'idioma de la interfície (ara català i anglès). S'aplica en tornar a obrir Escena. Per afegir-ne un altre n'hi ha prou amb un fitxer `Escena\Lang\xx.json` amb les traduccions. Alguns missatges de la barra d'estat encara surten només en català.

## Sortides d'àudio

Botó 🔊 a dalt a la dreta. Les sortides es defineixen a part de les cues, i cada cue d'àudio tria per quina sona. Controladors disponibles:

- **WASAPI compartit**: el recomanat.
- **WASAPI exclusiu**: latència més baixa, però té el dispositiu tot per a ell.
- **ASIO**: per a targetes professionals. Pots triar quin és el primer canal de sortida.

Cada sortida té un ajust de volum propi i un botó de prova de so. Si el dispositiu no hi és (per exemple, en un altre ordinador), s'usa el predeterminat i es mostra un avís.

## Escoltar pels auriculars

Amb el botó **🎧 Escolta** d'una cue d'àudio (o la tecla **E**) l'escoltes sense tocar l'espectacle: no es mou ni la cue en espera ni res més. Torna a prémer per parar.

- **Per defecte sona per la mateixa sortida que la cue.** És el cas habitual quan l'àudio de l'espectacle ja va als teus auriculars (assajos, preparar l'espectacle a casa…). El panell «En curs» hi posa «per la sortida de l'espectacle», i Esc o PÀNIC també l'aturen.
- **Si l'espectacle surt pels altaveus de la sala**, tria a **⚙ Ajustos** un dispositiu d'escolta a part (per exemple, la sortida d'auriculars de l'ordinador). Llavors el públic no ho sent. Si aquest dispositiu no està connectat, no sona res: mai no passa als altaveus.
- Aquest ajust es desa a l'ordinador, no a l'espectacle.

## Còpia automàtica i recuperació

- Mentre treballes, **els canvis sense desar es copien cada 30 s** (es pot canviar a ⚙ Ajustos). La còpia es guarda a part i **mai no sobreescriu el teu fitxer**.
- Escena també recorda contínuament quina cue està en espera i quina s'ha disparat l'última.
- Si Escena es tanca malament (penjada, apagada…), en tornar-lo a obrir pregunta si vols **recuperar l'espectacle i continuar des de la cue on era**, recuperar-lo des del principi o començar de nou.
- Si es tanca bé, no queda res per recuperar.

## Ajustos (⚙)

| Ajust | Per defecte | On es desa |
|---|---|---|
| Protecció de doble GO | 250 ms | a l'espectacle |
| Fosa del primer toc de PÀNIC | 0,5 s | a l'espectacle |
| Còpia de seguretat cada | 30 s | a l'espectacle |
| Sortida per escoltar cues | la mateixa que la cue | a l'ordinador |
| Idioma | català | a l'ordinador |
| No deixis dormir l'ordinador ni apagar la pantalla | activat | a l'ordinador |

**Ordinador despert:** mentre Escena és obert, Windows no adorm ni hiberna l'ordinador, no apaga la pantalla i no posa l'estalvi de pantalla, encara que no s'hagi disparat cap cue. Així el projector continua rebent el negre mentre s'espera el primer GO. Es desactiva a ⚙ Ajustos, i Windows ho desfà sol en tancar Escena. Tancar la tapa del portàtil sí que l'adorm (es canvia a les opcions d'energia de Windows).

## Pantalles (vídeo i imatges)

Les cues de vídeo i d'imatge surten per una **sortida de pantalla**. N'hi ha una de nova automàticament quan afegeixes el primer vídeo o la primera imatge, i la pots canviar al botó 🔊 → «Pantalla».

- **Pantalla ampliada (projector):** a pantalla completa, sense vores ni cursor. **Està en negre des que s'obre l'espectacle i hi torna entre vídeos.** No agafa el teclat: l'espai i l'Esc continuen anant a Escena.
- **Pantalla principal:** en finestra, que pots moure i canviar de mida, i que queda sempre per sobre d'Escena. També es pot posar a pantalla completa, però llavors tapa Escena.
- El botó **«Identifica totes les pantalles»** mostra el número de cada pantalla, i **«Mostra el nom a la pantalla»** comprova on surt.
- Si obres l'espectacle en un ordinador sense el projector, el vídeo surt en finestra a la pantalla principal (mai la tapa) i es mostra un avís.
- Si poses un vídeo o una imatge mentre ja se'n veu un altre, el nou queda a sobre. Amb les foses es poden fer encadenats.
- **So del vídeo per:** cada cue de vídeo tria per on surt el so. «Predeterminat de Windows» (com abans) o qualsevol sortida d'àudio de l'espectacle (botó 🔊). Per una sortida d'Escena, el so passa pel mesclador com una cue d'àudio: segueix el volum general i l'ajust de la sortida, les foses, la pausa i el ducking, i funciona també amb WASAPI exclusiu i ASIO. Escena el manté sincronitzat amb la imatge fent servir el rellotge del VLC i la latència de la sortida. Si la sortida triada ja no existeix, surt pel predeterminat (la comprovació ho avisa).
- Pots tenir **diverses pantalles** (botó 🔊 → «+ Pantalla»), cadascuna amb la seva finestra; cada cue tria per quina surt.
- **Correcció del projector:** a cada pantalla pots moure les quatre cantonades de la imatge (en % de la pantalla) quan el projector està descentrat o inclinat. Si la pantalla ja està oberta es veu en directe, i la casella «graella de prova» hi dibuixa una quadrícula per alinear-la. ↑↓ als camps mouen 0,1 % (amb Maj, 1 %).

## Espectacles

Es desen com a fitxers `.show` (JSON). Les rutes dels sons es guarden **relatives a la carpeta de l'espectacle**: si poses l'espectacle i els sons a la mateixa carpeta, la pots moure sencera a un altre ordinador. Els `.escena` de la versió 0.1 s'obren i es desen com a `.show`.

## Com està fet

- **Cues:** totes comparteixen la mateixa base (GO, aturar, pausar, durada, estat). Les de fitxer deriven de `MediaCue` (sortida i foses): `AudioCue` i `VideoCue` passen per `PlayableCue` (volum, inici/final, bucle), i `ImageCue` hi deriva directament.
- **Sortides:** el motor només parla amb `IMediaOutput` i `IMediaPlayback`. `AudioOutput` és un mesclador NAudio, i `VideoOutput` és una finestra negra on cada vídeo o imatge és una capa.
- **Vídeo:** el VLC descodifica i els fotogrames es copien a una imatge WPF, en lloc d'una finestra nativa. Així es poden fondre a negre i superposar.
- **Matriu:** cada cue d'àudio passa per una matriu d'encaminament (canals de la cue → canals de la sortida, amb un nivell per a cada punt de creu) cap a un mesclador amb tants canals com la sortida. Una cue amb enviaments obre una reproducció per sortida i es controlen totes juntes (foses, pausa, aturada, ducking).
- **Text i correcció:** els textos són una capa més de la finestra de vídeo. La correcció del projector dibuixa tota l'escena sobre una malla deformada amb una homografia (cantonades).

## Full de ruta

**Tot fet:**
- Versió 0.4: 1 cursor d'espera separat · 2 protecció de doble GO · 3 números lliures i notes · 4 pànic de dos nivells · 5 fosa completa · 6 bucles amb «Surt del bucle» · 7 escoltar sense tocar l'espectacle · 14 còpia automàtica i recuperació.
- Versió 0.5: 8 matriu de sortides i enviaments · 9 modes de grup · 10 començar des d'un temps · 11 tecles per a cues · 12 cue de micròfon · 13 ducking · 15 comprovació abans de la funció · 16 empaquetar · 17 registre d'assaig · 18 vista de regidor · 19 traduccions (català i anglès) · vídeo avançat: diverses pantalles, correcció del projector, cues de text i sobretítols.

- Versió 0.6: exportació al QLab · so del vídeo per qualsevol sortida · copiar i enganxar cues · editar diverses cues alhora (vegeu [CHANGELOG.md](CHANGELOG.md)).

- Versió 0.7: zoom a la forma d'ona · ordinador despert mentre Escena és obert · Atura/Pausa sobre la cue seleccionada.

**Encara no provat amb aparells reals:** targetes multicanal i ASIO, micròfon en directe (latència i acoblament), projector de veritat amb la correcció.

## Icona

`Escena\Escena.ico` és un botó de «play» verd amb ones de so, sobre un fons fosc. Es genera amb `Escena\Tools\make-icon.ps1`, per si mai la vols retocar.

## Tornar a compilar

Cal el .NET 8 SDK:

```
cd Escena
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ..\Escena-Windows
```
