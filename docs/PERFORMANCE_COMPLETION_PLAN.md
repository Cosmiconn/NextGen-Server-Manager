# Abschlussprüfung Serverleistung

Stand: 21.10.2026. Noch nicht abgeschlossen.

Die beiden verteilten 1600er-Tests bleiben die Vergleichsbasis. Sie ersetzen weder einen 2000er-Test noch aktive Spielbelastung. Originalnachweise bleiben unverändert.

Der erhöhte Vorbereitungslauf am 21.10. gelang nach dem ausdrücklich genehmigten vorübergehenden Stoppen eines ASUS-Socketprozesses, der die Ports 9012 und 9013 belegte. Alle 400 zusätzlichen Accounts/Charaktere wurden über das Originalprotokoll verifiziert; das kombinierte Manifest enthält 2000 bestehende Identitäten ohne Auto-Create. Alle Dienste wurden regulär gestoppt. Die separate Datenbankprüfung bestätigte 2000 Charaktere mit positiven HP; die ursprünglichen 1600 Positionen blieben unverändert. Der ASUS-Prozess lief später wieder, seine beiden ursprünglichen Listener konnten jedoch nicht bestätigt werden.

Der Anschlusscontroller startete keinen Lasttest, weil der Vorbereitungsaudit REVIEW meldete. Die kleine Zone-Startübersicht `Message.txt` war beim Neustart um eine wiederholte Verbindungszeile kürzer geworden. Der Audit vergleicht nun bei diesem bekannten Dateityp auch unterschiedlich lange, vollständig gesicherte Snapshots. Neue Fehler am Dateianfang, zusätzliche Warnungen und unbekannte Formate bleiben sichtbar; normale Logkürzungen behalten ihre Evidenzlücke. Regression vor der Korrektur fehlgeschlagen, danach bestanden. Eine spätere erneute Auswertung hat keine Snapshot-Evidenzlücke mehr, enthält aber bereits die regulären Shutdown-Meldungen und bleibt daher REVIEW. Der ursprüngliche Bericht bleibt unverändert.

Der erneute erhöhte Kapazitätsstart um 11:26 Uhr wurde von Windows vor Prozessstart abgebrochen; der Nutzer sah erneut keinen UAC-Dialog. Aktuell läuft kein Live-Test. Vor dem nächsten Lastlauf werden Startlogs separat geprüft; die Abschlussprüfung und der verifizierte ASUS-Rückstart bleiben Teil des vorbereiteten Ablaufs.

Der Logaudit blockiert nun beide tatsächlich beobachteten `FAILED Listen_Add`-/`FAILED LISTEN ADD`-Formate als `NG-NET-0006`. Die Regression prüft neue Fehler, historische Einträge und erfolgreiche Listener getrennt. Eine separate erneute Auswertung des fehlgeschlagenen Starts erkannte beide Portfehler; die ursprünglichen Nachweise wurden nicht überschrieben. Daraus folgt noch kein erfolgreicher Lasttest.

| Anforderung | Abnahmebeleg | Aktueller Stand |
| --- | --- | --- |
| 2000 gleichzeitige Spieler | Strenge Rampe mit denselben Gates wie bisher, exakte Client-/ShinePlayer-Zahlen, frische Heartbeats, mindestens 30 Minuten Endlast, vollständiger Start-/Laufzeit-Logaudit | Profil und Heartbeat-Gate implementiert, Selbsttests bestanden; alle 2000 Identitäten provisioniert und Datenbankbindung geprüft; Live-Test offen |
| Ressourcen unter Last | Zeitreihe für Zone, WorldManager und Lastgenerator: Prozess- und stärkste Thread-CPU, Private Bytes, Working Set, Handles, Threads; Messausfälle sichtbar | Sampler implementiert und am Testprozess geprüft; Serverlast-Messung offen |
| Bewegung | Verifiziertes NA2016-Paketformat, kleiner Pilot mit serverseitig bestätigten Positionsänderungen, anschließend kontrollierte skalierte Last und Latenzmessung | Einzelpilot implementiert, Sitzungs-/Startpositionsbindung und Abbruchpfade im TCP-Test bestanden; lokaler Runner mit separater Datenbankprüfung vorbereitet; Live-Pilot, Skalierung und Latenzmessung offen |
| Kampf | Verifiziertes Ziel-/Angriffsformat, serverseitig bestätigter Effekt, Pilot und skalierte Wiederholung; keine bloße Paketanzahl als Erfolg | Zielwahl-/Basisangriffsencoder und Zielantwortdecoder gegen Originalmitschnitt geprüft; Trefferdecoder anhand der PDB mit synthetischen Tests vorbereitet. Aktuelle Zielbindung, Live-Treffer, Pilot und Skalierung offen |
| Disconnect 1669 | Nachweis des fehlerhaften Pfads bei reproduzierbarem Fehler oder überprüfte betriebliche Abhilfe mit klaren Grenzen; keine ungeprüfte Binäränderung | Verteilte Spawns bis 1600 geprüft; Ursache offen |
| Sauberes Herunterfahren | Reproduzierbarer Start-/Stopptest nach Last; bei erneutem Login-Hänger Thread-/Wait-Evidenz vor Eingriff; Korrektur und Wiederholung | Ehrliche Fehlermeldung vorhanden; Login-Ursache offen |
| Rubrik und Dokumentation | Ergebnisse und tatsächlich geprüfte Grenzen im Manager/Handbuch, Release-/Windows-Smoke/CI, bestehender Research-PR aktualisiert | Offen |

Vor Datenbankänderungen: neue geprüfte Sicherungen, exakte Manifestbindung und Positionssnapshot, gestoppte Dienste. Zusätzliche Testkonten werden separat über den vorhandenen Provisioner erstellt. Bestehende Zugangsdaten werden weder ersetzt noch veröffentlicht. Rückweg und Cleanup gehören zu jedem Live-Lauf.

Prozess-CPU und Heartbeat-Verarbeitung sind keine Tick- oder Gameplay-P95/P99-Latenzen. Solche Latenzen benötigen einen nachgewiesenen Request-/Response- bzw. serverseitigen Messpunkt. Messlücken werden nicht als Nullwerte gewertet.
