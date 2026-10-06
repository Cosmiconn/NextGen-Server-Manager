# UI Redesign Plan – ui/navigation-redesign

Ziel: die bestehende 0.3.4-WPF-Oberfläche auf das vom Nutzer freigegebene Dark-Blue/Charcoal-Mockup umbauen, **ohne eine bestehende Funktion, Command-Bindung, Diagnose oder Serviceaktion zu verlieren**.

Die verbindliche visuelle Quelle ist `docs/UI_TARGET.md`. Die dort dokumentierte 1536×864-Ansicht ist die Referenzgröße für den Pixelvergleich, **nicht** eine feste Fenstergröße. Das Fenster bleibt skalierbar.

## Navigationsziel

### Dashboard
Bestehende Dashboard-Funktionen bleiben vollständig erhalten:
- Serviceübersicht / Health
- Serviceauswahl
- Start / Stop / Neustart
- Recovery setzen
- Neu installieren
- Dienst löschen
- Ordner öffnen
- Config öffnen
- Smart Start / Smart Stop / Smart Restart

Status: **Ziel-Layout umgesetzt, real im Windows-Smoke geöffnet und gerendert.**

### Serverleistung
In der freigegebenen Referenz sind genau drei Subtabs sichtbar:
1. Zone Auslastung / Scaling
2. Limits / Capacity
3. Adaptive Hooks

`Performance / Vertical Scaling` bleibt vollständig erhalten und ist über den Chevron-/Overflow-Pfad erreichbar, ohne einen vierten sichtbaren Referenz-Subtab einzuführen.

Status:
- Zone Auslastung / Scaling: **Ziel-Layout umgesetzt und gerendert**
- Limits / Capacity: **responsive Ziel-Ansicht umgesetzt und gerendert**
- Adaptive Hooks: **responsive Ziel-Ansicht umgesetzt und gerendert**
- Performance / Vertical Scaling: **responsive Ziel-Ansicht im Overflow umgesetzt und gerendert**

### Diagnostic
Subtabs:
1. Logs & Diagnose
2. Live Timeline
3. PDB / Symbole

Status: **alle drei Ansichten auf Card-/Toolbar-/Tabellen-Sprache umgestellt, real geöffnet und gerendert; PDB-Enter-Suche erhalten.**

### Tools
Subtabs:
1. Client / Map Safety
2. später Spielfunktionen / DLL Hook
3. später OPTool

Status: `Client / Map Safety` **responsive Ziel-Ansicht umgesetzt und im Windows-Smoke gerendert**. Zukünftige Module werden innerhalb von Tools ergänzt und dürfen die vier Hauptkategorien nicht aufblasen.

### Globale Utilities
Klein und außerhalb der Hauptnavigation:
- Einstellungen
- Handbuch
- Credits

Status: **umgesetzt und real gerendert**. Die bisherigen Einstellungen-/Hinweis-Inhalte werden in den Utility-Bereich umgehängt, nicht verworfen. Ein durch die Render-Abnahme entdeckter Foreground-Verlust der alten Settings-Inhalte wurde zentral im Utility-Host behoben.

## Verbindliche Funktionszuordnung aus 0.3.4

| 0.3.4 Ansicht | Zielort | Status |
|---|---|---|
| Dashboard | Dashboard | erhalten, neu aufgebaut, gerendert |
| Zone Auslastung / Scaling | Serverleistung > Zone Auslastung / Scaling | erhalten, neu aufgebaut, gerendert |
| Performance / Vertical Scaling | Serverleistung > Overflow | erhalten, neu aufgebaut, gerendert |
| Adaptive Hooks | Serverleistung > Adaptive Hooks | erhalten, neu aufgebaut, gerendert |
| Limits / Capacity | Serverleistung > Limits / Capacity | erhalten, neu aufgebaut, gerendert |
| Logs & Diagnose | Diagnostic > Logs & Diagnose | erhalten, neu aufgebaut, gerendert |
| Live Timeline | Diagnostic > Live Timeline | erhalten, neu aufgebaut, gerendert |
| PDB / Symbole | Diagnostic > PDB / Symbole | erhalten, neu aufgebaut, gerendert |
| Client / Map Safety | Tools > Client / Map Safety | erhalten, neu aufgebaut, gerendert |
| Einstellungen / Hinweise | globaler Einstellungen-Button | Inhalte erhalten, gerendert |
| Handbuch | globale Utility | gerendert |
| Credits | globale Utility | gerendert |

## Visuelles Ziel

- Dark-Blue/Charcoal
- Header mit App-Icon, Titel, Untertitel
- Server Health rechts oben
- Server-Root-Toolbar darunter
- vier große Hauptkategorie-Tabs mit Icon, Titel und Untertitel
- aktiver Haupttab als stark gefüllter blauer Zustand
- eigene Subtab-Leiste darunter; aktiver Subtab ebenfalls klar blau
- Summary-Cards für Kapazitäts-/Performanceansichten
- Status-Pills für OK/Warnung/Kritisch/Gestoppt
- kompakte, lesbare technische Tabellen
- Provisionierungsplan bleibt prominent
- zusätzliche Module dürfen später wachsen, ohne die Hauptleiste horizontal zu überfüllen

## Responsive Regeln

- **1536×864 bleibt unverändert die Referenzanordnung.**
- Größere Fenster geben Tabellen und Inhaltsflächen zusätzlichen Platz; kein Bitmap-/Viewbox-Stretching.
- Kleinere Fenster verdichten Toolbar- und Aktionsbuttons zu Icons mit Tooltips.
- Hauptnavigation wird unterhalb des Referenzbereichs proportional schmaler.
- Unterhalb der Desktopbreite werden Haupttab-Untertitel ausgeblendet, bevor sie abgeschnitten werden; Icon + Haupttitel bleiben sichtbar.
- Serverleistung-/Diagnostic-Subtabs werden bei Bedarf proportional verkleinert; Performance bleibt im Overflow.
- Die sechs Live-Capacity-Karten brechen auf kleineren Fenstern kontrolliert in mehrere Reihen um.
- Dashboard-Karten wechseln bei schmaler Breite von 4 auf 2 Spalten.
- Dashboard sowie Logs/Diagnose wechseln bei sehr schmaler Breite von Zwei-Spalten- auf gestapelte Inhaltsbereiche.
- Adaptive-Hook-Werte verwenden Wrap-Layout statt einer starren, extrem breiten Eingabezeile.
- Client-/Terrain-, Capacity-, Diagnostic- und Performance-Tabellen behalten alle Spalten und erhalten horizontales Scrollen.
- Der Provisionierungsplan wechselt von Referenz-Pixelbreiten auf proportionale Spalten; die Anlegen-Aktion kann bei knapper Breite icon-kompakt werden.
- Initiale Fenstergröße wird an die verfügbare Windows-Arbeitsfläche angepasst, wenn 1536×864 nicht vollständig hineinpasst.
- Windows/WPF-DPI-Skalierung bleibt unabhängig von den Layout-Breakpoints aktiv.

Die Responsive-Logik ist bewusst in separaten Partial-Dateien gehalten:
- `MainWindow.Responsive.cs`
- `MainWindow.ResponsiveNarrow.cs`
- `MainWindow.ResponsiveModules.cs`

Damit bleibt die 1536×864-Referenzgeometrie vom schmalen Fallback nachvollziehbar getrennt.

## Automatische Windows-UI-Abnahme

Der Windows-Workflow prüft nicht mehr nur den Compiler. Die veröffentlichte WPF-EXE wird auf dem Windows-Runner real gestartet und muss `MainWindow successfully shown.` melden. Danach fährt der echte Dispatcher mehrere Fenstergrößen und Navigationspfade ab.

Aktuell verpflichtend getestet:
- Resize: 1180×760, 900×700, **exakt 720×620**, anschließend wieder größer.
- Dashboard.
- Serverleistung: Zone Capacity, Limits, Adaptive Hooks, Performance-Overflow.
- Diagnostic: Logs, Live Timeline, PDB/Symbole.
- Tools: Client/Map Safety.
- Utilities: Einstellungen, Handbuch, Credits.
- Keine `FATAL`-Meldung und kein unerwartetes Prozessende.
- Mindestens **16 echte `RenderTargetBitmap`-PNG-Screenshots** als CI-Artefakt.

Der GitHub-Windows-Runner stellt nur ungefähr 1024 px Arbeitsbreite bereit. Deshalb kann dort die verbindliche 1536×864-Referenzgröße nicht als echtes Top-Level-Windows-Fenster dargestellt werden. Das ist der verbleibende visuelle Abnahmepunkt auf einem ausreichend großen realen Windows-Desktop.

## Implementierungsfortschritt

1. [x] Styles/Brushes und reusable Navigation-/Card-Styles.
2. [x] Bestehende Tabs ohne Funktionsverlust neu gruppiert.
3. [x] Bestehende Commands/Bindings beim Umbau erhalten bzw. explizit neu gebunden.
4. [x] Haupt-/Subnavigation inklusive Performance-Overflow.
5. [x] Dashboard und Live-Zone-Kapazität in Zieloptik überführt.
6. [x] Limits, Adaptive Hooks, Performance, Diagnostic und Client / Map Safety in dieselbe visuelle Sprache überführt.
7. [x] Responsive Breakpoints und schmale Inhaltslayouts implementiert.
8. [x] Windows-CI / win-x64 Publish geprüft.
9. [x] Reale Windows-Startup-, Resize- und Navigation-Smokes bis 720×620.
10. [x] Gerenderte CI-Screenshots aller migrierten Hauptansichten und Utilities.
11. [ ] Realer Windows-Laufzeittest bei 1536×864 mit direktem Referenzbildvergleich.
12. [ ] Sichtbare Restabweichungen aus diesem 1536×864-Vergleich korrigieren.
13. [ ] Erst danach Merge-Freigabe nach `main`.

## Abnahmekriterien

- Windows-CI grün.
- Alle bestehenden Funktionen erreichbar.
- Kein bestehender Command/Servicepfad entfernt.
- Aktiver Haupttab und Subtab sofort erkennbar.
- Referenzansicht bei 1536×864 bleibt geometrisch stabil.
- Fenster bleibt beim Vergrößern und Verkleinern bedienbar; Inhalte werden umgeordnet oder scrollbar statt abgeschnitten.
- Zielbild wird visuell pixelnah reproduziert.
- Ein grüner Compiler ist **keine** visuelle Abnahme.
- Vor Merge ist weiterhin ein echter 1536×864-Screenshot auf ausreichend großem Windows-Desktop mit direktem Vergleich zur freigegebenen Referenz erforderlich.
- Erst nach realer Windows-Abnahme nach `main` mergen.
