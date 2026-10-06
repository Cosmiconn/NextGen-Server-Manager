# NextGen Fehlercodes

Die `NG-*` Codes sind **eigene Diagnosecodes des Managers**. Sie ersetzen keine originalen Fiesta-Fehlernummern, sondern fassen Log-, Prozess-, Netzwerk- und Konfigurationsbelege verständlich zusammen.

## Service / Windows

- `NG-SVC-0001` – Windows-Dienst fehlt
- `NG-SVC-0002` – Dienst ist gestoppt
- `NG-SVC-0003` – Service Control Manager meldet unerwartetes Dienstende (Event 7031/7034)
- `NG-OS-0001` – Windows Application Log meldet Fiesta-Prozessabsturz

## WorldManager / Zone

- `NG-WM-0002` – Session blieb `SERVER_ID_UNKNOWN`
- `NG-WM-0003` – Zone S2S Ready fehlgeschlagen
- `NG-ZONE-0002` – Zone-Dienst gestoppt
- `NG-ZONE-0008` – Zone versucht WM-Verbindung neu aufzubauen
- `NG-ZONE-0010` – ungültige `machine number[-1]` / Ziel-Zone nicht auflösbar
- `NG-ZONE-0014` – BlockInfo-Limit
- `NG-ZONE-0015` – Mob-/Spawn-Limit
- `NG-ZONE-0016` – Crash-/Restart-Schleife (>=3 Stops in 10 Minuten)
- `NG-ZONE-0017` – wahrscheinliches WM↔Zone Startup-Race
- `NG-ZONE-0018` – kein Zone-Session-Puffer
- `NG-ZONE-0019` – WorldManager schließt Zone-Session
- `NG-ZONE-0020` – Zone wurde nach Stop kurzfristig erfolgreich wiederhergestellt

## Netzwerk / Protokoll

- `NG-NET-0004` – einzelner S2S-Reconnect
- `NG-NET-0005` – interne Verbindung fehlgeschlagen
- `NG-NET-0010` – Reconnect-Burst (mindestens 5 Reconnects in 60 Sekunden)
- `NG-PROTO-0002` – unerwartetes Zone-Paket am aktiven WM-Parserzustand
- `NG-PROTO-0003` – Paketlänge überschritten
- `NG-PROTO-0010` – Parser-/Sessionfehler außerhalb der Startup-Phase

## Core / Daten

- `NG-CORE-0001` – fataler Serverfehler
- `NG-CORE-0002` – ASSERT
- `NG-CORE-0003` – unbehandelte Ausnahme
- `NG-DATA-0001` – doppelte Field-Serial
- `NG-DATA-0002` – Checksum-Hinweis in Serverdaten

## Start / Orchestrierung

- `NG-START-0001` – Smart-Start/Stop konnte eine Abhängigkeit oder einen Health-Check nicht erfüllen

## Interpretation

`Info` bedeutet nicht automatisch Fehler. Beispiel: `NG-ZONE-0020` dokumentiert eine Recovery-Sequenz. `Warning` während der ersten Minuten nach dem Start kann Startup-Rauschen sein; derselbe Parser-/Sessionfehler im laufenden Betrieb wird durch die Korrelation höher bewertet.
