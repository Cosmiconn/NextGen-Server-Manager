# Vorprüfung: aktive Last und Login-Stopp

Stand: 22.10.2026. Protokollvorprüfung; noch kein bestandener aktiver Lasttest.

## Bewegung und Kampf

Ein vorhandener Originalclient-Mitschnitt vom 07.10.2026 enthält im Zone-Stream 94 `CH8/25`-Bewegungsbefehle mit jeweils 16 Body-Bytes sowie neun `CH8/18`-Stopps mit acht Body-Bytes. Die vollständige TCP-Reassemblierung und der Handshake-basierte XOR-Zustand wurden wie beim bestehenden Transfer-Importer ausgewertet. Zwei andere Streams ohne vollständigen Handshake wurden ausdrücklich nicht als Protokollbeleg verwendet.

Die Zone-PDB bestätigt `NC_ACT=8`, `NC_ACT_MOVERUN_CMD=25` und `PROTO_NC_ACT_MOVEWALK_CMD` mit Größe 16: `from` an Offset 0 und `to` an Offset 8. `SHINE_XY_TYPE` enthält zwei unsigned 32-Bit-Werte für X und Y.

Ein anonymes Paketfixture aus dem Mitschnitt, einschließlich des zweiby­tigen Opcodes, lautet `19208e2d0000412a0000be2d0000982a0000`: von (11662,10817) nach (11710,10904). Das Stoppfixture `1220bb2d0000902a0000` enthält (11707,10896). Diese historischen Koordinaten sind keine Wegfreigabe für die aktuellen Testcharaktere.

Der Mitschnitt enthält außerdem Zielwahl `CH9/1` mit zwei Body-Bytes, fünf `CH9/43`-Bash-Starts ohne Body, einen `CH9/50`-Bash-Stopp ohne Body und zwei `CH9/64`-Skillbefehle mit vier Body-Bytes. Die PDB benennt diese Kommandos entsprechend. Ein Lastgenerator darf daraus keine alten Zielhandles oder Skills blind übernehmen: Zielauswahl und Schaden müssen gegen die aktuelle Serversitzung verifiziert werden.

Die Zielantwort `SH9/2` ist inzwischen gegen Originalmitschnitt und PDB geprüft: 30 Body-Bytes, `order` an Offset 0, Zielhandle als UInt16 an Offset 1, HP/MaxHP als UInt32 an Offset 3/7, SP/MaxSP an 11/15, LP/MaxLP an 19/23, Level an 27 und HP-Änderungsfolge an 28. Das Beispiel bestätigt Ziel 9062 mit 1000/1000 HP. Diese historische Kennung wird ausschließlich im Codec-Test verwendet. Der Mitschnitt enthält keinen `SH9/44`-Treffer; die gesendeten Angriffe gelten ausdrücklich nicht als bestätigter Kampf.

`FiestaCombatProtocol` stellt Encoder für Zielwahl und Basisangriff/Angriffsstopp sowie den Zielantwortdecoder bereit. Der zusätzliche `SH9/44`-Decoder basiert bisher nur auf `PROTO_NC_BAT_BASH_HIT_CMD` in der PDB: 13 Bytes, Zielhandle an 0, gepackter Schaden an 2 (Bit 0 kritisch, obere 15 Bits Schaden), Flags an 4, Rest-HP an 5, Angriffsgeschwindigkeit an 9 und HP-Änderungsfolge an 11. Sein Testfixture ist ausdrücklich synthetisch. Tests prüfen außerdem Byteordnung, Bitgrenzen, fremde Opcodes und falsche Paketgrößen. Es gibt noch keinen aktiven Kampftreiber oder einen Live-Schadensnachweis; vor dessen Freigabe fehlen die aktuelle Zielbindung und die Zuordnung bestätigter Treffer zur laufenden Sitzung.

Noch erforderlich: sichere aktuelle Wege, kleiner Bewegungspilot mit serverseitiger Positionsbestätigung, aktuelle Zielhandles und bestätigte Kampfeffekte, anschließend skalierte aktive Last. Heartbeat-Aktivität und erfolgreich gesendete Pakete allein beweisen weder Bewegung noch Kampf.

Die Mob-Sichtbarkeit ist zusätzlich gegen PDB und denselben Originalstream geprüft: `SH7/8` enthält einen 149-Byte-Mob-Eintrag; `SH7/9` enthält einen Byte-Zähler und entsprechend viele dieser Einträge. Handle liegt an Offset 0, Modus an 2, Mob-Template-ID an 3 und X/Y/Richtung an 5/9/13. `SH7/14` entfernt einen zweiby­tigen Handle. Alle drei Listen, 28 Einzelmeldungen und 13 Entfernungen im Mitschnitt entsprechen diesen Längen. Das historische Ziel 9062 erschien bei Serverpaket 57 als Template 16402 an (12308,10250), war bei der Zielantwort 113 noch sichtbar und wurde bei Paket 203 entfernt. Das bestätigt die Zuordnung im Mitschnitt, keine aktuelle Zielverfügbarkeit oder Angreifbarkeit.

`FiestaMobVisibilityProtocol` decodiert diese Felder und prüft vor Rückgabe die vollständige Listenlänge sowie eindeutige Handles. Selbsttests prüfen das Originalfixture, mehrere gepackte Einträge, leere Listen, falsche Zähler, abgeschnittene/überlange Bodies und doppelte Handles.

Die ergänzende `FiestaMobVisibilitySession` verwaltet Bindungen innerhalb genau einer Zone-Verbindung. Entfernung, erneutes Erscheinen desselben Handles, beobachtete Walk-/Run-Broadcasts und Verbindungsende verwerfen alte Bindungen. Fehlerhafte Sichtbarkeitspakete entwerten die ganze Sitzung. Lokale Tests prüfen auch zwei getrennte Verbindungen mit identischem Spieler-/Mobhandle und das Wiederverwenden desselben Templates am selben Ort. Eine Bindung reserviert kein Ziel und bestätigt weder Angreifbarkeit noch Reichweite.

Der Headless-Client kann mit `ObserveMobVisibility` nach der Zone-Authentifizierung passiv Mob-Erscheinungen sammeln. Die Beobachtung ist standardmäßig aus und für die gemeinsame Rampe gesperrt. Nur der Zone-Empfangspfad speist die Sitzung; nach erfolgreichem Hold wird eine historische Momentaufnahme der verbliebenen Erscheinungskandidaten zurückgegeben. Beim Verlassen der Verbindung werden die nutzbaren Bindungen verworfen. Erscheinungskoordinaten beweisen weder Stillstand noch aktuelle Reichweite. Ein TCP-Integrationstest prüft World-/Zone-Trennung, Erscheinung und Entfernung, gleichzeitige Heartbeats sowie den sofortigen Abbruch bei einer fehlerhaften Liste trotz weiter offenem World-Socket.

Ein separater 15-Sekunden-Pilot ist vorbereitet: exakte bestehende Identität und Startposition, leere verifizierte Zone, positive HP; danach unveränderte gespeicherte Position, genau ein zusätzlicher Login, derselbe Zone-Prozess, leerer Pool und sauberer Logaudit. Ohne beobachteten Kandidaten gilt die Beobachtung als unvollständig. Der Negativtest bei gestopptem Runtime verweigert den Start vor jedem Login. Dieser Live-Pilot ist wegen der offenen Host-Absturzdiagnose noch nicht ausgeführt. Aktive Zielwahl, geprüftes angreifbares Template, Reichweite und bestätigte Treffer fehlen weiterhin.

Die Bewegungs-/Stopppakete sind inzwischen als reine Encoder implementiert und bytegenau gegen die anonymen Originalfixtures geprüft. Ein Decoder liest die 22-Byte-Bewegungsbroadcasts mit aktuellem Handle, Von-/Nach-Koordinaten und Geschwindigkeit. Ein fremder Mob-Broadcast wird dadurch noch nicht zur Bestätigung eines Testspielers.

Die aktuelle Sitzungsidentität ist inzwischen aus `SH6/2` lesbar. Die PDB beschreibt den verwendeten Body als `PROTO_NC_CHAR_MAPLOGIN_ACK` mit 242 Bytes: `charhandle` (UInt16) an Offset 0, 232 Parameterbytes ab Offset 2 und `logincoord` (UInt32 X/Y) an Offset 234/238. Die Disassembly der zugehörigen Füllfunktion bestätigt die Koordinatenoffsets. Der Originalmitschnitt enthält Handle 12000 und Start (11662,10817), exakt den Ausgangspunkt des ersten Bewegungsbefehls. Dieser historische Handle ist ausschließlich ein Testfixture, kein Ziel für neue Sitzungen.

Der Lastclient liest Handle und Startposition aus jeder aktuellen Login-Antwort, protokolliert sie beim Übergang zu `ZoneAuthenticated` und liefert sie im erfolgreichen Probe-Ergebnis als `ZoneActor`. Abweichende Bodygrößen werden verworfen. Loopback-TCP-Tests prüfen die echte Login-Lesefunktion einschließlich vorangehendem Heartbeat und Ablehnung eines verkürzten ACK. Die Parameterregion im Test ist synthetisch mit Nullen gefüllt; übernommen sind nur die belegten Handle-/Koordinatenbytes. Ein Live-Pilot, die Zuordnung empfangener Bewegungen zu diesen Handles und die Bestätigung einer Positionsänderung bleiben offen.

## Transport für aktive Last

Der Headless-Client unterstützt nun einen optionalen Einzelpilot über `MovementPilot`. Ohne Plan bleiben die bisherigen Lastläufe unverändert. Der Pilot prüft den aktuellen Login-Ausgangspunkt, sendet einen Laufbefehl über höchstens zehn Karteneinheiten und danach einen Stopp am Ziel. Start-, Lauf- und Nachbeobachtungszeit müssen vollständig in die Session-Haltezeit passen. Der Aufrufer muss vorher den gesamten Weg auf der aktuellen Karte prüfen. `SH8/27` beendet den Versuch sofort als Bewegungsablehnung; ein Benutzerabbruch stoppt auch den Befehlsablauf. Die gemeinsame Rampe lehnt einen einzelnen Bewegungsplan ausdrücklich ab, weil jeder Spieler einen eigenen Weg benötigt.

`MovementCommands` enthält ausschließlich Sendezeiten, Handle und Koordinaten und meldet `ServerPositionVerified=false`. Ein erfolgreicher Protokolllauf beweist weiterhin keine Positionsänderung. Lokale TCP-Tests prüfen die tatsächlichen Bewegungs-/Stoppframes zusammen mit Heartbeats im selben XOR-Strom, eine abweichende Loginposition ohne Bewegungspaket, Serverablehnung und Abbruch während der Bewegung. Ein lokaler Runner-Modus ist zusätzlich für einen bestehenden Testcharakter vorbereitet: leere verifizierte Zone, genaue Account-/Charakterbindung und TSStart-Ausgangspunkt vorab; anschließend frische gespeicherte Zielposition, genau ein zusätzlicher Login, positive HP, unveränderte Zone-Prozessidentität, leerer Pool und sauberer Logaudit. Dieser Live-Ablauf wurde noch nicht ausgeführt. Er belegt im Erfolgsfall nur einen kurzen Weg und die gespeicherte Logoutposition, keine Gameplay-Latenz oder skalierte Bewegungslast.

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

Release-Build und lokale Protokoll-, Zeitbudget-, Heartbeat- und Ressourcen-Selbsttests bestanden. Am 21.10. wurden nach frischen CHECKSUM-Sicherungen und RESTORE VERIFYONLY alle 400 zusätzlichen Accounts/Charaktere über das Originalprotokoll verifiziert. Der anschließende reguläre Dienststopp gelang einschließlich Login. Der geplante Lastlauf blieb wegen eines offenen Startlog-Audits aus; dessen Behandlung unterschiedlich langer Zone-Startsnapshots ist inzwischen korrigiert und getestet. Der spätere Start um 20:29 Uhr gelang: 32 neue Identitäten bestanden den Pilot mit zwei Minuten Endlast, 24 exakten Stabilitätsproben, erfülltem Heartbeat-Gate und CLEAN-Audits. Gespeicherte Positionen und ein zusätzlicher Login je Charakter wurden bestätigt; der reguläre Stopp einschließlich Login gelang. Die anschließende 2000er-Rampe endete nach bestandener 350er-Stufe und ClientReady 367 durch einen Windows-Bugcheck 0x7C. Endlast und Abschlussaudit fehlen. Derselbe Bugcheck war am Vortag aufgetreten; die lokale Treiberdiagnose ist vor weiterer Last erforderlich. Nach dem Neustart sind alle Fiesta-Dienste gestoppt, alle 2000 geplanten Positionen bestätigt und beide ASUS-Ports wieder belegt. 2000er-Last, aktive Spielbelastung und Wiederholungsprüfung des zuvor beobachteten Login-Hängers stehen weiterhin aus.

Siehe [vollständige Abschlusskriterien](PERFORMANCE_COMPLETION_PLAN.md). Private Mitschnitte, Symboldumps, Datenbanksicherungen und Zugangsdaten bleiben lokal.
