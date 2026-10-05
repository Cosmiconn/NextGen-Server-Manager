# Git-Workflow

Repository: `Cosmiconn/NextGen-Server-Manager`

## Grundregel

`main` ist die zuletzt bekannte, funktionsfähige und unter Windows getestete Baseline. Neue Funktionen, neue Tabs, größere UI-Umbauten und riskante Hook-/Binary-Arbeiten erfolgen **nie direkt auf `main`**.

## Branches

- `feature/<name>` – neue Funktion oder neuer Tab
- `ui/<name>` – UI-/Navigationsumbauten
- `fix/<name>` – Fehlerbehebungen
- `research/<name>` – Reverse Engineering / experimentelle Hook-Arbeit ohne Produktionsfreigabe

Aktueller nächster Branch nach der 0.3.4-Baseline:

`ui/navigation-redesign`

## Ablauf

1. aktuellen `main`-HEAD und Windows-CI prüfen
2. Branch vom aktuellen `main` erstellen
3. Änderung implementieren, ohne bestehende Funktionen zu entfernen
4. Windows-CI muss grün sein
5. reale Windows-Laufzeittests durchführen
6. Screenshots/Testergebnis dokumentieren
7. Pull Request gegen `main`
8. erst nach Abnahme mergen

## Windows-CI

`.github/workflows/windows-build.yml` baut auf `windows-latest` mit .NET 8 und erzeugt ein win-x64-Artefakt.

## Branch-Disziplin

Ein Feature-/UI-Branch enthält nur den zugehörigen Arbeitsblock. Neue unabhängige Funktionen werden nicht in denselben Branch hineingemischt. Dadurch können wir einzelne Tabs/Funktionen separat testen, zurückrollen und mergen.
