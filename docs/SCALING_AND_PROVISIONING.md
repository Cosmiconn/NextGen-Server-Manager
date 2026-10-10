# Live Capacity, Scaling und neue Zonen

## Messbare Live-Auslastung

Der Manager bewertet jede laufende Zone aus mehreren voneinander unabhängigen Signalen:

- etablierte TCP-Verbindungen am Client-Port (`ESTABLISHED`) gegen 1500
- CPU-Gesamtanteil und Core-äquivalente CPU-Zeit
- Working Set, Private Bytes und Virtual Memory des Zone-Prozesses
- Zahl der in `Field.txt` der Zone zugewiesenen Maps gegen den verifizierten `MapBlockInformation`-Container von 256
- harte Limitmeldungen aus Logs

Die Messung der etablierten Client-Sessions ist ein Netzwerk-Livewert. Sie ist näher an der realen Spielerbelegung als ein statischer Portwert, kann aber kurzlebige Verbindungen enthalten. Mob- und NPC-Poolbelegung wird derzeit nicht aus dem Prozessspeicher gelesen und deshalb nicht erfunden. Wird dagegen ein harter Überlauf im Log gemeldet, wird die Zone sofort als kritisch behandelt.

## Verifizierte harte Zone-Limits, die in die Bewertung einfließen

- ShinePlayer: 1500
- ShineMob: 8000
- ShineNPC: 256
- MapBlockInformation: 256
- MapCluster Registry: 512
- BlockDistribute: 64

Weitere statische Limits stehen in `NA2016_LIMITS.md`.

## Betriebs-Schwellen

Standardmäßig:

- 70 %: Warnung
- 85 %: Ausbau / neue Zone vorbereiten
- 95 %: kritisch

Für Private Memory wird standardmäßig ein **Betriebsbudget von 3072 MiB** verwendet. Das ist bewusst eine konservative Warnschwelle für den 32-Bit-Prozess und kein aus der Binary behauptetes Hardlimit.

CPU wird für die Skalierungsentscheidung zusätzlich als Core-äquivalenter Wert betrachtet. Das verhindert, dass ein einzelner ausgelasteter Zone-Thread auf einem 16- oder 32-Core-Host nur als wenige Prozent Gesamt-CPU erscheint.

## Neue Zone planen

`Neue Zone planen`:

1. ermittelt die nächste Zone-ID oberhalb der vorhandenen `ZoneNN`-Ordner,
2. wählt eine vorhandene gültige Zone als Template,
3. liest alle Ports aus `ServerInfo.txt`,
4. berücksichtigt zusätzlich aktuell belegte TCP-Listener,
5. sucht drei freie aufeinanderfolgende Ports,
6. prüft Zielordner und Windows-Service-Namen.

## Neue Zone anlegen

Die Aktion ist absichtlich bestätigungspflichtig und benötigt Administratorrechte.

Transaktion:

1. `9Data/ServerInfo/ServerInfo.txt` nach `.nextgen-backups/provisioning/...` sichern.
2. Template-Zone kopieren; Logs, DebugMessage und NextGen-Caches werden ausgelassen.
3. `ZoneServerInfo.txt` auf `_ZoneN` und Zone-ID `N` umschreiben.
4. Drei `SERVER_INFO`-Zeilen für ConnectionKind 20/6/8 ergänzen.
5. Zone-Dienst mit dem nativen NA2016-Mechanismus registrieren.
6. optional Service-Recovery setzen.
7. Windows-Firewallregel **nur für den Client-Port** anlegen.

Bei einem Fehler wird die Operation soweit möglich zurückgerollt: Firewallregel, neu registrierter Dienst, ServerInfo und neuer Zone-Ordner.

## Firewall-Sicherheitsmodell

Automatisch geöffnet wird ausschließlich:

- TCP inbound: neuer Zone-Client-Port (ConnectionKind 20)

Nicht automatisch extern geöffnet werden:

- Zone-intern (ConnectionKind 6)
- OPTool (ConnectionKind 8)

Diese beiden Endpunkte sind Server-intern bzw. administrativ und sollen nicht durch den Provisionierungsassistenten unnötig nach außen exponiert werden.

## Was nach der Provisionierung absichtlich nicht passiert

Die neue Zone wird nicht automatisch gestartet und erhält keine Maps automatisch. Eine leere Zone entlastet eine volle Zone noch nicht. Erst eine bewusste Änderung der Field-/Instanz-Zuordnung verlagert tatsächliche Last.

## WorldManager Live-Kapazität

Zusätzlich misst der Scaling-Tab den WorldManager separat:

- etablierte Client-Sessions am WM-Clientlistener gegen `nMaxAccept=1500`
- etablierte Zone-/S2S-Sessions am internen WM-Listener gegen `nMaxAccept=100`
- Core-äquivalente CPU-Last
- Private Memory

Der 100er Wert ist weiterhin **ein Socket-Session-Limit und kein Beweis für maximal 100 Zone-IDs**. Mehrere S2S-Sessions pro Zone sind möglich. Eine zusätzliche Zone kann außerdem den WorldManager stärker belasten statt ihn zu entlasten; deshalb wird die WM-Kapazität getrennt bewertet.


## NA2016 Headless-ShinePlayer-Benchmark (Messstand 2026-10-10)

Dieser Abschnitt dokumentiert die **gesonderte, streng nachgewiesene 2000er-Testprofil-Runtime**
auf dem Forschungsbranch `research/client-load-simulator`. Er ersetzt **nicht** die
oben dokumentierten konservativen Standard-/Baselinewerte. Die aktuellen Limits
werden vor jedem Test aus dem *laufenden* Zielprozess plus verifiziertem Listener gelesen.

- Die serielle Identitätsvorprovisionierung hat 1600/1600 Accounts und Charaktere
  inklusive `SH3/20` und `SH4/3` verifiziert. Diese Phase hält keine 1600 Sitzungen.
- Im Ramp-B-Test werden alle Sitzungen gehalten und die Zone muss in jeder Stufe
  zwei aufeinanderfolgende **exakte** `ShinePlayer`-Runtime-Zähler liefern.
- Trace `player-ramp-trace-20261010-105407-012.log`: normale Reihenfolge,
  2-s-Starttakt, Stufen bis einschließlich **800 PASS**.
  Bei Stufe 900: **899 ClientReady und 899 ShinePlayer**, Account `r_ngl000806`
  nach vier vollständigen Zone-Handoffs **vor SH6/2** getrennt.
- Trace `player-ramp-trace-20261010-112917-438.log`: diagnostische Reihenfolge
  (Account 515 zuerst), 2-s-Starttakt, Stufen bis einschließlich **700 PASS**.
  Bei Stufe 800: **798 ClientReady und 798 ShinePlayer**, Accounts
  `r_ngl000750`/`r_ngl000787` nach vier Zone-Handoffs vor `SH6/2` getrennt.
- Die identischen Accounts `750` und `787` waren in der normalen Reihenfolge
  erfolgreich. Somit ist bislang weder ein statisches Accountproblem noch ein hartes
  800-/900-Spieler-Poollimit nachgewiesen. Der Abbruch nach
  `CH6/1`, Initialisierungspaketen und vor `SH6/2` bleibt offen.
- Der zweite Lauf hatte einen **LOG DELTA REVIEW** wegen
  `WorldManager.Session::wms_NC_KQ_W2Z_MAKED_CMD: Buffer full[0]`
  (`Zone00Assert`, Meldungszeit 13:00:00 Serverzeit).
  Das ist **kein CLEAN-Gesamtserveraudit**, aber eine unmittelbare Kausalität zum
  späteren Zone-Login-Abbruch ist nicht belegt.
- Der Nachweis umfasst gleichzeitige Sitzungsaufnahme und passive
  Protokoll-Halteverbindungen, noch **keine** 1600 gleichzeitig aktiv kämpfenden Spieler.
  Der finale 5-Minuten-Stabilitätsnachweis bei 1600 steht weiterhin aus.

### Kontrollierte Diagnose statt stiller Erfolgsaufwertung

Die Ramp-UI bietet `Starttakt (s)` (1..3), `B: zuerst #` (0=Standard) und
`B: Nachaufnahme` (0=Baseline, 1=Diagnose). Die **optionale**, standardmäßig
deaktivierte Nachaufnahme greift nur nach vier transienten
`CH6/1 -> SH6/2`-Socketabbrüchen ein, wartet 20 Sekunden und beginnt
mit exakt demselben Account einen einzigen zusätzlichen vollständigen
`Login -> World -> Zone`-Zyklus. Explizite Server-Ablehnungen werden dabei
**nicht** übergangen.

Die Nachaufnahme ist in Trace und Ergebnis als **DIAGNOSE** sichtbar.
Sie beweist keine saubere Erstaufnahme: Jede Stufe muss trotzdem alle
ursprünglichen eindeutigen Identitäten gleichzeitig `ClientReady` halten,
und `ShinePlayer` muss unverändert zweimal exakt der Zielstufe entsprechen.
Wird die Nachaufnahme ebenfalls abgewiesen, bleibt die Stufe **BLOCKED**.

Für einen abschließenden Kapazitätsvergleich Nachaufnahme wieder auf `0`
setzen und sämtliche Log-Audit-REVIEW-Befunde gesondert abklären.
