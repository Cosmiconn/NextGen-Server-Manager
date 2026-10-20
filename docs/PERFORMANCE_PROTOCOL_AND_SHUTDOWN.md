# Vorprüfung: aktive Last und Login-Stopp

Stand: 20.10.2026. Offline-Evidenz; noch kein bestandener aktiver Lasttest.

## Bewegung und Kampf

Ein vorhandener Originalclient-Mitschnitt vom 07.10.2026 enthält im Zone-Stream 94 `CH8/25`-Bewegungsbefehle mit jeweils 16 Body-Bytes sowie neun `CH8/18`-Stopps mit acht Body-Bytes. Die vollständige TCP-Reassemblierung und der Handshake-basierte XOR-Zustand wurden wie beim bestehenden Transfer-Importer ausgewertet. Zwei andere Streams ohne vollständigen Handshake wurden ausdrücklich nicht als Protokollbeleg verwendet.

Die Zone-PDB bestätigt `NC_ACT=8`, `NC_ACT_MOVERUN_CMD=25` und `PROTO_NC_ACT_MOVEWALK_CMD` mit Größe 16: `from` an Offset 0 und `to` an Offset 8. `SHINE_XY_TYPE` enthält zwei unsigned 32-Bit-Werte für X und Y.

Ein anonymes Paketfixture aus dem Mitschnitt, einschließlich des zweiby­tigen Opcodes, lautet `19208e2d0000412a0000be2d0000982a0000`: von (11662,10817) nach (11710,10904). Das Stoppfixture `1220bb2d0000902a0000` enthält (11707,10896). Diese historischen Koordinaten sind keine Wegfreigabe für die aktuellen Testcharaktere.

Der Mitschnitt enthält außerdem Zielwahl `CH9/1` mit zwei Body-Bytes, fünf `CH9/43`-Bash-Starts ohne Body, einen `CH9/50`-Bash-Stopp ohne Body und zwei `CH9/64`-Skillbefehle mit vier Body-Bytes. Die PDB benennt diese Kommandos entsprechend. Ein Lastgenerator darf daraus keine alten Zielhandles oder Skills blind übernehmen: Zielauswahl und Schaden müssen gegen die aktuelle Serversitzung verifiziert werden.

Noch erforderlich: sichere aktuelle Wege, kleiner Bewegungspilot mit serverseitiger Positionsbestätigung, aktuelle Zielhandles und bestätigte Kampfeffekte, anschließend skalierte aktive Last. Heartbeat-Aktivität und erfolgreich gesendete Pakete allein beweisen weder Bewegung noch Kampf.

Die Bewegungs-/Stopppakete sind inzwischen als reine Encoder implementiert und bytegenau gegen die anonymen Originalfixtures geprüft. Ein Decoder liest die 22-Byte-Bewegungsbroadcasts mit aktuellem Handle, Von-/Nach-Koordinaten und Geschwindigkeit. Ein fremder Mob-Broadcast wird dadurch noch nicht zur Bestätigung eines Testspielers.

Die aktuelle Sitzungsidentität ist inzwischen aus `SH6/2` lesbar. Die PDB beschreibt den verwendeten Body als `PROTO_NC_CHAR_MAPLOGIN_ACK` mit 242 Bytes: `charhandle` (UInt16) an Offset 0, 232 Parameterbytes ab Offset 2 und `logincoord` (UInt32 X/Y) an Offset 234/238. Die Disassembly der zugehörigen Füllfunktion bestätigt die Koordinatenoffsets. Der Originalmitschnitt enthält Handle 12000 und Start (11662,10817), exakt den Ausgangspunkt des ersten Bewegungsbefehls. Dieser historische Handle ist ausschließlich ein Testfixture, kein Ziel für neue Sitzungen.

Der Lastclient liest Handle und Startposition aus jeder aktuellen Login-Antwort, protokolliert sie beim Übergang zu `ZoneAuthenticated` und liefert sie im erfolgreichen Probe-Ergebnis als `ZoneActor`. Abweichende Bodygrößen werden verworfen. Loopback-TCP-Tests prüfen die echte Login-Lesefunktion einschließlich vorangehendem Heartbeat und Ablehnung eines verkürzten ACK. Die Parameterregion im Test ist synthetisch mit Nullen gefüllt; übernommen sind nur die belegten Handle-/Koordinatenbytes. Ein Live-Pilot, die Zuordnung empfangener Bewegungen zu diesen Handles und die Bestätigung einer Positionsänderung bleiben offen.

## Transport für aktive Last

Heartbeat und aktive Befehle müssen denselben XOR-Zustand und dieselbe Sendereihenfolge verwenden. Die Verbindung serialisiert jetzt Verschlüsselung und vollständigen Schreibvorgang gemeinsam. Ein abgebrochener Schreibvorgang nach Fortschreiben des XOR-Zustands schließt die Verbindung; sie kann nicht unbemerkt mit falschem Verschlüsselungszustand weiterverwendet werden. Ungültige Paketlängen werden vor der Verschlüsselung verworfen.

Außerdem unterschied der alte Leser nicht zwischen einem stillen Socket und einem Timeout mitten im Paket. Im zweiten Fall waren bereits Bytes verbraucht; der nächste Leseversuch konnte den Rest als neues Paket interpretieren. Nun bleibt nur ein Timeout vor dem ersten Frame-Byte wiederaufnehmbar. Ein unvollständiger Header oder Body nach Fristablauf beendet die Verbindung mit einem Fehler.

Lokale TCP-Integrationstests auf einem dynamischen Loopback-Port prüfen 128 konkurrierende Sender über den echten XOR-/Framingpfad, verworfene und vorab abgebrochene Sendungen ohne XOR-Verschiebung, Wiederaufnahme nach stillem Timeout, Abbruch bei Teilheader/-body sowie korrekt zusammengesetzte fragmentierte Frames. Die Tests verwenden keine Fiesta-Dienste und gehören zum Windows-Smoke. Sie ersetzen die weiterhin ausstehenden Serverlasttests nicht.

Ein weiterer TCP-Test reproduzierte eine verzögerte Fehlerweitergabe: Nach geschlossenem Zone-Socket wartete `Task.WhenAll` weiterhin auf den noch offenen World-Socket. Ein Clientfehler konnte dadurch bis zum Ende der Haltezeit verborgen bleiben. Bei einem Pump-Fehler wird jetzt der andere Pump abgebrochen und der ursprüngliche Fehler sofort weitergegeben. Der Test scheiterte vor der Korrektur am Zwei-Sekunden-Limit und bestand danach. Gegenproben prüfen, dass ein normaler World-Handoff-Abschluss die Zone-Heartbeats weiterlaufen lässt und ein Benutzerabbruch nicht als erfolgreicher Hold gemeldet wird.

## Login-Stopp

Analysierter Originalstand: SHA-256 `15132FE91F76B61BEFF877F0A80725A909FAB4923CD9C7E9DA76FE87FE1CCF99`.

Der Ablauf aus PDB und Disassembly:

1. `LoginServer::OnStop_Service` setzt das Stoppflag und wartet ohne eigenes Zeitlimit auf das Ende des Mainthreads.
2. Der Mainthread protokolliert `EXIT MAIN LOOP` und ruft `ProcessStop` auf.
3. `ProcessStop` beendet den Acceptor und ruft unmittelbar danach den IOCP-Destruktor auf. Erst nach dessen Rückkehr folgen Session-Freigaben und weitere Abschlussmeldungen.
4. `CIOCP::Stop(-1)` setzt Workerzustände, sendet Completion-Ereignisse und wartet in Schleifen auf die Zustandsübergänge. Der Worker schreibt beim Austritt Zustand 4; der Stopppfad schließt dessen Handle und setzt den Zustand auf 0.

Beim beobachteten Hänger vom 16.10. endete das Log nach `STOPPED ACCEPTOR` und mehreren Worker-Freigaben. Das passt zu einem Stillstand in diesem Abschnitt. Es beweist noch nicht, welcher Workerzustand oder welche Synchronisationsbedingung ihn ausgelöst hat. Insbesondere ist eine Race-Condition derzeit eine Hypothese, keine bestätigte Ursache.

Für die nächste Reproduktion werden vor jeder Bereinigung Thread-Stacks, Workerzustände und Prozessidentität benötigt. Erst daraus darf eine gezielte Korrektur mit Wiederholungstests abgeleitet werden. Die vorhandene Manager-Korrektur meldet einen unvollständigen Stopp bereits zuverlässig; die originale Login.exe wurde nicht verändert.

## Neue Abnahmevorbereitung

Das erweiterte Profil erhält alle 26 bisherigen Laststufen und ergänzt 1700, 1800, 1900, 1950 und 2000. Der vorbereitete Lauf hält die Endlast 30 Minuten. Vier Stunden Session-Haltezeit decken auch die vollständigen Timeout-Budgets ab. Für diesen Lauf werden frische Zone-Heartbeats nach einer maximal 90-sekündigen Aufnahmefrist verlangt. Jede Stabilitätsprobe enthält Clientzahl, tatsächliche ShinePlayer-Belegung und Heartbeat-Zahlen.

`LoadProcessSampler` erfasst PID-/Startzeit-gebunden Prozess-CPU, den stärksten beobachteten Thread, Private Bytes, Working Set, virtuellen Speicher, Handles und Threads. Prozess-CPU wird in Prozent eines logischen Kerns angegeben und kann größer als 100 sein. Beendete Threads sind nicht nachträglich messbar. Fehlende Messungen bleiben als Lücken/Fehler sichtbar. Ressourcen-P95/P99 sind keine Gameplay-Latenzen.

Release-Build und lokale Protokoll-, Zeitbudget-, Heartbeat- und Ressourcen-Selbsttests bestanden. Die 400 zusätzlichen Zugangssätze wurden lokal vorbereitet; Accounts und Charaktere wurden noch nicht angelegt. Neue Account- und Charakterdatenbanksicherungen wurden mit CHECKSUM erstellt und per RESTORE VERIFYONLY geprüft. Der erhöhte Start wurde von Windows vor Prozessstart abgebrochen; der Nutzer sah keinen UAC-Dialog. Alle Fiesta-Dienste blieben gestoppt. Der Live-Nachweis steht aus.

Siehe [vollständige Abschlusskriterien](PERFORMANCE_COMPLETION_PLAN.md). Private Mitschnitte, Symboldumps, Datenbanksicherungen und Zugangsdaten bleiben lokal.
