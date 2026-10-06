# 0.3.1 – Live Capacity & Zone Scaling

Diese Version verbindet die zuvor extrahierten NA2016-Hardlimits erstmals mit echter Laufzeitmessung und einem sicheren Zone-Provisionierungsworkflow.

Wichtig für den ersten Test:

1. Manager als Administrator starten.
2. Server Root scannen.
3. `Zone Auslastung / Scaling` öffnen.
4. Einige Refresh-Zyklen abwarten, damit der CPU-Trend gelernt wird.
5. `Neue Zone planen` darf noch nichts verändern.
6. Vor `Zone jetzt anlegen` den angezeigten Zone-Namen und die drei Ports prüfen.

Die Provisionierung verändert `ServerInfo.txt`, erzeugt einen neuen Zone-Ordner, registriert einen Windows-Dienst und legt eine Firewallregel an. Vor der Konfigurationsänderung wird ein Backup erstellt. Die neue Zone wird nicht automatisch gestartet und erhält keine Maps automatisch.
