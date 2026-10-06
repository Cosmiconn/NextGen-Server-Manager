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

Status: **Ziel-Layout umgesetzt, CI-validiert.**

### Serverleistung
In der freigegebenen Referenz sind genau drei Subtabs sichtbar:
1. Zone Auslastung / Scaling
2. Limits / Capacity
3. Adaptive Hooks

`Performance / Vertical Scaling` bleibt vollständig erhalten und ist über den Chevron-/Overflow-Pfad erreichbar, ohne einen vierten sichtbaren Referenz-Subtab einzuführen.

Status:
- Zone Auslastung / Scaling: **Ziel-Layout umgesetzt**
- Limits / Capacity: **responsive Ziel-Ansicht umgesetzt**
- Adaptive Hooks: **responsive Ziel-Ansicht umgesetzt**
- Performance / Vertical Scaling: **responsive Ziel-Ansicht im Overflow umgesetzt**

### Diagnostic
Subtabs:
1. Logs & Diagnose
2. Live Timeline
3. PDB / Symbole

Status: **alle drei Ansichten auf Card-/Toolbar-/Tabellen-Sprache umgestellt; PDB-Enter-Suche erhalten.**

### Tools
Subtabs:
1. Client / Map Safety
2. später Spielfunktionen / DLL Hook
3. später OPTool

Status: `Client / Map Safety` **responsive Ziel-Ansicht umgesetzt**. Zukünftige Module werden innerhalb von Tools ergänzt und dürfen die vier Hauptkategorien nicht aufblasen.

### Globale Utilities
Klein und außerhalb der Hauptnavigation:
- Einstellungen
- Handbuch
- Credits

Status: **umgesetzt**; die bisherigen Einstellungen-/Hinweis-Inhalte werden in den Utility-Bereich umgehängt, nicht verworfen.

## Verbindliche Funktionszuordnung aus 0.3.4

| 0.3.4 Ansicht | Zielort | Status |
|---|---|---|
| Dashboard | Dashboard | erhalten, neu aufgebaut |
| Zone Auslastung / Scaling | Serverleistung > Zone Auslastung / Scaling | erhalten, neu aufgebaut |
| Performance / Vertical Scaling | Serverleistung > Overflow | erhalten, neu aufgebaut |
| Adaptive Hooks | Serverleistung > Adaptive Hooks | erhalten, neu aufgebaut |
| Limits / Capacity | Serverleistung > Limits / Capacity | erhalten, neu aufgebaut |
| Logs & Diagnose | Diagnostic > Logs & Diagnose | erhalten, neu aufgebaut |
| Live Timeline | Diagnostic > Live Timeline | erhalten, neu aufgebaut |
| PDB / Symbole | Diagnostic > PDB / Symbole | erhalten, neu aufgebaut |
| Client / Map Safety | Tools > Client / Map Safety | erhalten, neu aufgebaut |
| Einstellungen / Hinweise | globaler Einstellungen-Button | Inhalte erhalten |

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

## Implementierungsfortschritt

1. [x] Styles/Brushes und reusable Navigation-/Card-Styles.
2. [x] Bestehende Tabs ohne Funktionsverlust neu gruppiert.
3. [x] Bestehende Commands/Bindings beim Umbau erhalten bzw. explizit neu gebunden.
4. [x] Haupt-/Subnavigation inklusive Performance-Overflow.
5. [x] Dashboard und Live-Zone-Kapazität in Zieloptik überführt.
6. [x] Limits, Adaptive Hooks, Performance, Diagnostic und Client / Map Safety in dieselbe visuelle Sprache überführt.
7. [x] Responsive Breakpoints und schmale Inhaltslayouts implementiert.
8. [x] Nach zusammenhängenden Blöcken Windows-CI / win-x64 Publish geprüft.
9. [ ] Realer Windows-Laufzeittest bei 1536×864 mit Screenshotvergleich.
10. [ ] Resize-Smoke-Test unterhalb der Referenzbreite und bei maximiertem Fenster.
11. [ ] Sichtbare Abweichungen aus den Screenshots korrigieren.
12. [ ] Erst danach Merge-Freigabe nach `main`.

## Abnahmekriterien

- Windows-CI grün.
- Alle bestehenden Funktionen erreichbar.
- Kein bestehender Command/Servicepfad entfernt.
- Aktiver Haupttab und Subtab sofort erkennbar.
- Referenzansicht bei 1536×864 bleibt geometrisch stabil.
- Fenster bleibt beim Vergrößern und Verkleinern bedienbar; Inhalte werden umgeordnet oder scrollbar statt abgeschnitten.
- Zielbild wird visuell pixelnah reproduziert.
- Ein grüner Compiler ist **keine** visuelle Abnahme.
- Vor Merge sind ein realer Windows-Screenshot der Referenzgröße und mindestens ein schmalerer Resize-Screenshot erforderlich.
- Erst nach realer Windows-Abnahme nach `main` mergen.
