# Abschlussprüfung Serverleistung

Stand: 17.10.2026. Ziel aktiv; noch nicht abgeschlossen.

Die beiden verteilten 1600er-Tests bleiben die Vergleichsbasis. Sie ersetzen weder einen 2000er-Test noch aktive Spielbelastung. Originalnachweise bleiben unverändert.

| Anforderung | Abnahmebeleg | Aktueller Stand |
| --- | --- | --- |
| 2000 gleichzeitige Spieler | Strenge Rampe mit denselben Gates wie bisher, exakte Client-/ShinePlayer-Zahlen, frische Heartbeats, mindestens 30 Minuten Endlast, vollständiger Start-/Laufzeit-Logaudit | Profil und Heartbeat-Gate implementiert, Selbsttests bestanden; 400 Identitäten offline vorbereitet; Live-Test offen |
| Ressourcen unter Last | Zeitreihe für Zone, WorldManager und Lastgenerator: Prozess- und stärkste Thread-CPU, Private Bytes, Working Set, Handles, Threads; Messausfälle sichtbar | Sampler implementiert und am Testprozess geprüft; Serverlast-Messung offen |
| Bewegung | Verifiziertes NA2016-Paketformat, kleiner Pilot mit serverseitig bestätigten Positionsänderungen, anschließend kontrollierte skalierte Last und Latenzmessung | Encoder/Decoder gegen Originalfixtures geprüft; Transport für parallele Befehle getestet; Ausführung und Serverbestätigung offen |
| Kampf | Verifiziertes Ziel-/Angriffsformat, serverseitig bestätigter Effekt, Pilot und skalierte Wiederholung; keine bloße Paketanzahl als Erfolg | Offen |
| Disconnect 1669 | Nachweis des fehlerhaften Pfads bei reproduzierbarem Fehler oder überprüfte betriebliche Abhilfe mit klaren Grenzen; keine ungeprüfte Binäränderung | Verteilte Spawns bis 1600 geprüft; Ursache offen |
| Sauberes Herunterfahren | Reproduzierbarer Start-/Stopptest nach Last; bei erneutem Login-Hänger Thread-/Wait-Evidenz vor Eingriff; Korrektur und Wiederholung | Ehrliche Fehlermeldung vorhanden; Login-Ursache offen |
| Rubrik und Dokumentation | Ergebnisse und tatsächlich geprüfte Grenzen im Manager/Handbuch, Release-/Windows-Smoke/CI, bestehender Research-PR aktualisiert | Offen |

Vor Datenbankänderungen: neue geprüfte Sicherungen, exakte Manifestbindung und Positionssnapshot, gestoppte Dienste. Zusätzliche Testkonten werden separat über den vorhandenen Provisioner erstellt. Bestehende Zugangsdaten werden weder ersetzt noch veröffentlicht. Rückweg und Cleanup gehören zu jedem Live-Lauf.

Prozess-CPU und Heartbeat-Verarbeitung sind keine Tick- oder Gameplay-P95/P99-Latenzen. Solche Latenzen benötigen einen nachgewiesenen Request-/Response- bzw. serverseitigen Messpunkt. Messlücken werden nicht als Nullwerte gewertet.
