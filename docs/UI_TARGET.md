# Verbindliches UI-Ziel

Stand: 2026-10-06

## Absolute Referenz

Die vom Nutzer am **06.10.2026** erneut bestätigte **1536×864**-Ansicht ist die verbindliche visuelle Referenz für `ui/navigation-redesign`.

Referenz-Metadaten:

- Auflösung: **1536 × 864 Pixel**
- Format der freigegebenen Referenz: JPEG
- SHA-256 der vom Nutzer freigegebenen Datei: `d38173c6fd52643288f1269cb60e460cd2ebaaf7dc6b40c460198721b12eda51`
- Nutzerentscheidung: **Keine visuelle Abweichung wird akzeptiert.**

Diese Bildreferenz hat Vorrang vor älteren textlichen Beschreibungen, Mockup-Interpretationen oder allgemeinen WPF-Konventionen. Eine Abweichung darf nicht mit „ähnlicher Optik“, „besserer Standard-UX“ oder einer vereinfachten Standard-WPF-Darstellung begründet werden.

Das Ziel ist eine möglichst pixelnahe Reproduktion der freigegebenen Ansicht bei weiterhin vollständig funktionaler Anwendung. Keine bestehende Funktion, Command-Bindung, Diagnose oder Serviceaktion darf durch den Umbau verloren gehen.

## Verbindlicher sichtbarer Shell-Aufbau

Von oben nach unten:

1. Custom Dark-Blue/Charcoal Header
   - Server-Rack-App-Symbol links
   - `NextGen Fiesta Server Manager`
   - `NA2016 Diagnose · Service Control · Smart Start · PDB/Symbolanalyse`
   - rechts kompakte Server-Health-Karte
   - `Als Administrator neu starten`
   - eigene Window-Caption-Buttons rechts oben
2. Server-Root-Toolbar
   - Server Root + Pfad
   - Folder/Browse
   - `Ordner…`
   - `Scannen`
   - deutlich blauer Primärbutton `Status aktualisieren`
   - kleine globale Utilities `Einstellungen`, `Handbuch`, `Credits`
3. Vier große Hauptkategorien in genau dieser sichtbaren Reihenfolge:
   - **Dashboard** – `Übersicht & Status`
   - **Serverleistung** – `Zonen · Ressourcen · Scaling`
   - **Diagnostic** – `Logs · Timeline · PDB`
   - **Tools** – `Spielfunktionen · DLL Hook · OPTool`
4. Unter `Serverleistung` zeigt die freigegebene Referenz **genau drei sichtbare Subtabs**:
   - **Zone Auslastung / Scaling**
   - **Limits / Capacity**
   - **Adaptive Hooks**
5. Danach die Inhaltsfläche, in der die freigegebene Referenz `Live-Zone-Kapazität` zeigt.
6. Ganz unten eine schmale Statusleiste mit Status links sowie `NextGen Fiesta Server Manager | NA2016` rechts.

### Performance / Vertical Scaling

`Performance / Vertical Scaling` bleibt eine bestehende und zu erhaltende Funktion. Die freigegebene Referenz zeigt ihn jedoch **nicht als vierten sichtbaren Subtab** in der Serverleistungs-Zeile. Er muss deshalb über einen skalierbaren Overflow-/Chevron-Pfad erreichbar bleiben, ohne die sichtbare Referenzzeile zu verändern.

## Live-Zone-Kapazität – verbindlicher Aufbau

Die Ansicht reproduziert die Referenz in dieser Reihenfolge:

1. Header-Card `Live-Zone-Kapazität`
   - Summary-/Empfehlungstexte links
   - `Auslastung aktualisieren` rechts
   - blauer Primärbutton `Neue Zone planen` rechts
2. Sechs Summary-Cards in einer Reihe:
   - Laufende Zonen
   - Clients gesamt
   - CPU (WorldManager)
   - RAM (WorldManager)
   - Höchste Zonen-Auslastung
   - Status
3. kompakte Zone-Tabelle mit:
   - Zone
   - Status
   - Clients
   - Client %
   - CPU
   - RAM
   - Maps / BlockInfo
   - Gesamt
   - Druck
   - Trend
   - Empfehlung
   - trailing Aktions-/Overflow-Spalte
4. farbige Status-/Pressure-Pills und farbige Zeilenmarkierung links
5. darunter `Provisionierungsplan`
   - Neue Zone
   - Template
   - Ports
   - Status
   - Bereit/Zielpfad
   - Folder-Aktion
   - blauer Primärbutton `Zone jetzt anlegen`
6. darunter der bestehende Plan-/Statushinweis.

## Visuelle Regeln

- Dark-Blue/Charcoal-Ops-Look wie in der Referenz, nicht generisches Schwarz/Grau.
- Kräftiges Azure-/Electric-Blue für aktive Haupt-/Subnavigation und Primäraktionen.
- Aktive Hauptkategorie sofort eindeutig als stark gefüllter blauer Zustand erkennbar.
- Aktiver Subtab ebenfalls klar gefüllt/blau.
- Header, Toolbar, Karten und Tabellen nutzen dünne blau-graue Borders und kompakte Radien wie im Zielbild.
- Summary-Cards besitzen eigene farbige Akzente links und große Werte.
- Statusfarben: OK grün, Warnung gelb/orange, kritisch rot, gestoppt neutral/grau.
- Kritische Informationen werden zusätzlich durch Text/Pill transportiert, nicht ausschließlich durch Farbe.
- Tabellen bleiben technisch dicht und kompakt; keine unnötig großen Standard-WPF-Zeilen.
- Hauptnavigation darf durch spätere Module nicht zu einer endlosen sichtbaren Tab-Leiste anwachsen.
- Bestehende Werte bleiben datengetrieben. Das Mockup darf niemals als Begründung dienen, reale Messwerte kosmetisch zu überschreiben.

## Funktionale Preservation-Matrix

| 0.3.4 Ansicht | Zielort | Anforderung |
|---|---|---|
| Dashboard | Dashboard | vollständig erhalten |
| Zone Auslastung / Scaling | Serverleistung | sichtbar wie Referenz |
| Performance / Vertical Scaling | Serverleistung Overflow | vollständig erreichbar, nicht als vierter sichtbarer Referenz-Subtab |
| Limits / Capacity | Serverleistung | sichtbar wie Referenz |
| Adaptive Hooks | Serverleistung | sichtbar wie Referenz |
| Logs & Diagnose | Diagnostic | vollständig erhalten |
| Live Timeline | Diagnostic | vollständig erhalten |
| PDB / Symbole | Diagnostic | vollständig erhalten |
| Client / Map Safety | Tools | vollständig erhalten |
| Einstellungen / Hinweise | global `Einstellungen` | alle bisherigen Inhalte erhalten |
| Handbuch | global `Handbuch` | kleiner Utility-Pfad |
| Credits | global `Credits` | kleiner Utility-Pfad |

## Abnahmekriterium

Ein grüner Build allein ist **keine visuelle Abnahme**.

Vor Merge nach `main` sind zwingend erforderlich:

1. Windows-CI grün.
2. Realer Start unter Windows.
3. Screenshot der laufenden Anwendung in der Referenzgröße bzw. einem vergleichbaren maximierten Zustand.
4. Direkter visueller Vergleich mit der freigegebenen 1536×864-Referenz.
5. Abweichungen bei Anordnung, Größenhierarchie, Farben, Navigation, Cards, Tabellen, Toolbar und Utilities werden vor Merge korrigiert.
6. Alle bestehenden Commands, Bindings, Services und Diagnosepfade bleiben erreichbar und testbar.

Die UI-Arbeit erfolgt auf `ui/navigation-redesign`. `main` bleibt bis zur realen Windows-Abnahme die letzte abgenommene Baseline.