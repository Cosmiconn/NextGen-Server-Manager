# UI Redesign Plan – ui/navigation-redesign

Ziel: die bestehende 0.3.4-WPF-Oberfläche auf das vom Nutzer freigegebene Dark-Blue/Charcoal-Mockup umbauen, **ohne eine bestehende Funktion, Command-Bindung, Diagnose oder Serviceaktion zu verlieren**.

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

### Serverleistung
Subtabs:
1. Zone Auslastung / Scaling
2. Performance / Vertical Scaling
3. Limits / Capacity
4. Adaptive Hooks

### Diagnostic
Subtabs:
1. Logs & Diagnose
2. Live Timeline
3. PDB / Symbole

### Tools
Subtabs:
1. Client / Map Safety
2. später Spielfunktionen / DLL Hook
3. später OPTool

### Globale Utilities
Klein und außerhalb der Hauptnavigation:
- Einstellungen
- Handbuch
- Credits

## Verbindliche Funktionszuordnung aus 0.3.4

| 0.3.4 Ansicht | Zielort | Status |
|---|---|---|
| Dashboard | Dashboard | muss vollständig erhalten bleiben |
| Zone Auslastung / Scaling | Serverleistung > Zone Auslastung / Scaling | erhalten |
| Performance / Vertical Scaling | Serverleistung > Performance / Vertical Scaling | erhalten |
| Adaptive Hooks | Serverleistung > Adaptive Hooks | erhalten |
| Limits / Capacity | Serverleistung > Limits / Capacity | erhalten |
| Logs & Diagnose | Diagnostic > Logs & Diagnose | erhalten |
| Live Timeline | Diagnostic > Live Timeline | erhalten |
| PDB / Symbole | Diagnostic > PDB / Symbole | erhalten |
| Client / Map Safety | Tools > Client / Map Safety | erhalten |
| Einstellungen / Hinweise | globaler Einstellungen-Button | alle Inhalte erhalten |

## Visuelles Ziel

- Dark-Blue/Charcoal
- Header mit App-Icon, Titel, Untertitel
- Server Health rechts oben
- Server-Root-Toolbar darunter
- große Hauptkategorie-Tabs mit Icon, Titel und Untertitel
- aktiver Haupttab als stark gefüllter blauer Zustand
- eigene Subtab-Leiste darunter; aktiver Subtab ebenfalls klar blau
- Summary-Cards für Kapazitäts-/Performanceansichten
- Status-Pills für OK/Warnung/Kritisch/Gestoppt
- kompakte, lesbare technische Tabellen
- Provisionierungsplan bleibt prominent
- zusätzliche Module dürfen später wachsen, ohne die Hauptleiste horizontal zu überfüllen

## Implementierungsstrategie

1. Styles/Brushes und reusable Navigation-/Card-Styles aufbauen.
2. Bestehende Tabs zunächst **nur neu gruppieren**, nicht funktional verändern.
3. Alle bestehenden `Command`- und `Binding`-Ausdrücke erhalten.
4. Haupt-/Subnavigation einführen.
5. Dashboard und Live-Zone-Kapazität visuell ans Zielbild angleichen.
6. Restliche Ansichten in dieselbe visuelle Sprache überführen.
7. Nach jedem zusammenhängenden Block Windows-CI prüfen.
8. Vor Merge reale Windows-Laufzeittests und Screenshotvergleich durchführen.

## Abnahmekriterien

- Windows-CI grün.
- Alle bestehenden Funktionen erreichbar.
- Kein bestehender Command/Servicepfad entfernt.
- Aktiver Haupttab und Subtab sofort erkennbar.
- Fenster bleibt bei Mindestgröße benutzbar.
- Zielbild wird visuell so exakt wie technisch sinnvoll reproduziert.
- Erst nach realer Windows-Abnahme PR nach `main` mergen.
