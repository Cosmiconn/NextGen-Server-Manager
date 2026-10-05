# Verbindliches UI-Ziel

Stand: 2026-10-05

Die vom Nutzer freigegebene Mockup-Ansicht ist das **verbindliche visuelle Ziel**. Der Manager soll am Ende **100 % auf dieses Zielbild hinarbeiten**. Keine bestehende Funktion darf beim Umbau verloren gehen oder unzugänglich werden.

## Hauptnavigation

1. **Dashboard** – Übersicht & Status
2. **Serverleistung** – große Hauptkategorie mit Untertabs
   - Zone Auslastung / Scaling
   - Performance / Vertical Scaling
   - Limits / Capacity
   - Adaptive Hooks
3. **Diagnostic**
   - Logs & Diagnose
   - Live Timeline
   - PDB / Symbole
4. **Tools**
   - Client / Map Safety
   - später Spielfunktionen / DLL Hook
   - später OPTool

## Globale Utilities

Diese Funktionen gehören bewusst **nicht** in die große Haupttab-Leiste und sollen kleiner rechts oben bzw. in einem Utility-Bereich liegen:

- Einstellungen
- Handbuch
- Credits

## Visuelle Regeln

- Dark-Blue/Charcoal-Ops-Look mit kräftigem Blau als aktivem Akzent.
- Aktive **Hauptkategorie** muss sofort erkennbar sein: gefüllter blauer Zustand, Icon, Titel, Untertitel.
- Darunter klar getrennte **Subtab-Leiste**; aktiver Subtab ebenfalls gefüllt/blau.
- Navigation muss zusätzliche zukünftige Tabs aufnehmen können, ohne horizontal zu überlaufen.
- Server Root, Health, Scan/Refresh und Admin-Funktion bleiben im oberen Rahmen jederzeit erreichbar.
- Hauptinhalte verwenden klare Karten, Summary-Metriken, Status-Pills und besser lesbare Tabellen.
- Statusfarben: OK grün, Warnung gelb/orange, kritisch rot, gestoppt neutral/grau.
- Kritische Informationen dürfen nicht allein über Farbe vermittelt werden; Text/Pill bleibt sichtbar.
- Tabellen bleiben kompakt, erhalten aber konsistente Abstände und klare Zeilenhierarchie.
- `Live-Zone-Kapazität` zeigt Summary-Karten, Zone-Tabelle und Provisionierungsplan wie im Zielbild.
- Aktionen wie `Auslastung aktualisieren`, `Neue Zone planen`, `Zone jetzt anlegen` bleiben prominent.
- Neue Module müssen dieselbe visuelle Sprache verwenden; die UI darf mit dem Projekt wachsen, ohne zur endlosen Tab-Leiste zu werden.

## Abnahmekriterium

Das Programm soll am Ende **100 %ig wie das freigegebene Zielbild wirken**: gleiche visuelle Hierarchie, Dark-Blue/Charcoal-Farbwelt, klare Haupt-/Subtabs, Karten, Badges, Tabellenabstände, Toolbar und Utility-Anordnung. Funktionale Erweiterungen dürfen das Zielbild erweitern, aber nicht verwässern.

Abweichungen sind nur zulässig, wenn sie technisch zwingend sind oder für zusätzliche zukünftige Module benötigt werden. Dabei muss die visuelle Sprache unverändert bleiben und jede bestehende Funktion erreichbar bleiben.

## Funktionale Abnahme

Beim UI-Umbau werden bestehende Commands, Bindings, Services und Diagnosepfade **nicht entfernt**. Falls Controls neu gruppiert werden, muss jede bestehende Funktion weiterhin erreichbar und testbar sein.

Die UI-Arbeit erfolgt auf `ui/navigation-redesign`, nicht direkt auf `main`.
