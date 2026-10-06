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
