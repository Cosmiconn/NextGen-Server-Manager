# Git-Workflow

Repository: `Cosmiconn/NextGen-Server-Manager`

## Grundregel

`main` ist der stabile Release-/Produktionszweig. Er enthält nur ausdrücklich freigegebene, unter Windows getestete Release-Stände. Laufende Entwicklung erfolgt **nie direkt auf `main`**.

`develop` ist der gemeinsame Integrationszweig. Dort liegt der neueste zusammenhängende Stand aller bereits fertiggestellten und abgenommenen Entwicklungsblöcke. Neue Arbeitszweige starten grundsätzlich von einem aktuellen, grünen `develop`.

Der erste `develop`-Stand wurde aus der eingefrorenen 0.3.4-Baseline plus dem abgenommenen UI-/Navigationsumbau aufgebaut. Damit ist `develop` ab jetzt die Fortsetzung von **0.3.4 + neuer UI**.

## Branches

- `develop` – Integrationszweig für fertiggestellte und abgenommene Entwicklungsblöcke
- `feature/<name>` – neue Funktion oder neuer Tab
- `ui/<name>` – UI-/Navigationsumbauten
- `fix/<name>` – Fehlerbehebungen
- `research/<name>` – Reverse Engineering / experimentelle Hook-Arbeit ohne Produktionsfreigabe
- `release/<version>` – Release-Kandidat, ausschließlich Stabilisierung, Versions-/Dokuarbeit und Abnahme
- `main` – freigegebener stabiler Release-Stand

Der bisherige Block `ui/navigation-redesign` ist in den initialen `develop`-Stand integriert. Weitere unabhängige Arbeit wird nicht auf diesem UI-Branch fortgesetzt.

## Normaler Entwicklungsablauf

1. aktuellen `develop`-HEAD prüfen
2. Windows-CI des aktuellen `develop`-HEADs prüfen
3. neuen Arbeitsbranch vom aktuellen `develop` erstellen
4. Änderung implementieren, ohne bestehende Funktionen oder Diagnosen zu entfernen
5. vor jeder Änderung erneut Branch-HEAD und CI prüfen, da parallel entwickelt werden kann
6. Windows-CI muss auf dem Arbeitsbranch grün sein
7. für UI-/Runtime-relevante Änderungen reale Windows-Laufzeittests durchführen
8. Testergebnis, Limits und offene Risiken dokumentieren
9. fertigen, zusammenhängenden Block nach Abnahme in `develop` integrieren
10. nach Integration `develop` erneut vollständig unter Windows bauen/testen

Damit ist `develop` immer der Ausgangspunkt für den nächsten Entwicklungsblock.

## Release-Ablauf

Wenn `develop` einen zusammenhängenden Stand erreicht, den wir als neue Version veröffentlichen wollen:

1. aktuellen `develop`-HEAD und Windows-CI prüfen
2. `release/<version>` vom freigegebenen `develop`-HEAD erstellen
3. auf dem Release-Branch nur Stabilisierung, Release-Dokumentation, Versionsnummern und echte Abnahmekorrekturen durchführen
4. keine neuen großen Features in einen laufenden Release-Kandidaten aufnehmen
5. vollständigen Windows-Build, Publish, Runtime-/Smoke-Tests und gegebenenfalls reale Server-Abnahme durchführen
6. erst nach Freigabe `release/<version>` nach `main` integrieren
7. Release markieren/taggen
8. Release-spezifische Fixes anschließend wieder nach `develop` zurückführen, damit `develop` nicht hinter `main` zurückfällt

Direkte Feature-Merges nach `main` sind nicht vorgesehen.

## Windows-CI

`.github/workflows/windows-build.yml` baut auf `windows-latest` mit .NET 8 und erzeugt ein win-x64-Artefakt.

Der Workflow läuft für:

- `main`
- `develop`
- `feature/**`
- `fix/**`
- `ui/**`
- `research/**`
- `release/**`
- Pull Requests gegen `develop` und `main`

Die aktuelle WPF-CI prüft zusätzlich den echten Programmstart, responsive Resize-Pfade, Navigation und gerenderte UI-Screenshots. Ein reiner Compiler-Erfolg ersetzt die Windows-Laufzeitprüfung nicht.

## Branch-Disziplin

Ein Feature-/UI-/Fix-Branch enthält nur den zugehörigen Arbeitsblock. Neue unabhängige Funktionen werden nicht in denselben Branch hineingemischt. Dadurch können einzelne Funktionen separat getestet, zurückgerollt und nach `develop` integriert werden.

Riskante Hook-/Binary-Arbeit bleibt auf `research/**`, bis Build-/Hash-Bindung, Preflight, Rollback und die erforderlichen technischen Invarianten vollständig verifiziert sind. Erst dann darf der produktionsfähige Teil in einen normalen Entwicklungsblock und anschließend nach `develop`.

## Aktueller Integrationsstand

- `main`: eingefrorene, stabile 0.3.4-Baseline vor dem UI-Umbau
- `develop`: 0.3.4 + abgenommene neue skalierbare UI + Windows Runtime-/Render-Smoke
- nächster Entwicklungsblock: von `develop` abzweigen
