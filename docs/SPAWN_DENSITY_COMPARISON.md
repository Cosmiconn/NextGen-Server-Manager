# Zone00: gleicher Startpunkt gegen verteilte Startpunkte

Dieser Versuch untersucht lokale Spielerdichte als möglichen Auslöser von
Disconnect 1669. Er ist kein Kapazitätsnachweis und behebt den Fehler nicht.

## Vorbereitung im Manager

1. Bestehendes **vorprovisioniertes** Manifest auswählen (mindestens 1100
   NG-Testcharaktere, r_-Accounts, Auto-Create aus).
2. **E · Startpunkt-Vergleich vorbereiten** anklicken.
3. Ohne geprüfte Positionen den Dateidialog abbrechen. Der Manager öffnet einen
   neuen Ordner mit `01-inspect.sql`, `points.template.json` und `README.txt`.
4. `01-inspect.sql` in SSMS auf der lokalen Testdatenbank `World00_Character`
   ausführen. Es liest ausschließlich die ersten 1100 Testcharaktere und zeigt
   ihre gespeicherten Startpunkte sowie die Anzahl pro Punkt. Es erstellt nur
   eine temporäre Zielliste, keine dauerhaften Datenbankänderungen.
5. Mindestens zwei unterschiedliche Punkte derselben Map im normalen Client
   prüfen. Möglichst mehrere räumlich getrennte, vergleichbare sichere Punkte
   verwenden. Koordinaten aus derselben Server-Koordinatendarstellung verwenden;
   keine UI-Kartenkoordinaten oder geschätzten Rasterpunkte einsetzen.
6. JSON-Vorlage mit den geprüften Punkten füllen (`Map`, `X`, `Y`,
   `VerifiedInGame: true`), erneut E wählen und diese Datei auswählen.
   Der erste Punkt ist der gemeinsame Startpunkt. Für den verteilten Lauf werden
   die Punkte zyklisch in Manifestreihenfolge zugeteilt.

Die Software prüft Zahlenbereiche, gleiche Map und unterschiedliche Punkte.
Sie kann **keine Begehbarkeit oder Sicherheit gegen Angriffe beweisen**.
Eine leere/unbestätigte Vorlage erzeugt keine schreibenden SQL-Dateien.

## Durchführung

Die erzeugte README enthält die Reihenfolge. SQL wird niemals vom Manager
automatisch ausgeführt. Für Snapshot, Anwendung und Wiederherstellung müssen
Zone-, WorldManager- und Character-Prozesse nach vollständigem Logout und
Speichern beendet sein. Neue Logins verhindern. Erst dann in SSMS
`@OfflineConfirmed = 1` setzen. SQL selbst erkennt laufende Spielprozesse nicht.

- `02-snapshot.sql`: einmal die Originalpositionen sichern. Neue eindeutige
  Backup-Tabelle pro Plan, vorhandenes Backup wird nicht überschrieben.
- `03-cluster.sql`: alle ausgewählten Charaktere auf den ersten Punkt setzen.
- `04-spread.sql`: dieselben Charaktere auf die geprüften Punkte verteilen.
- `05-restore.sql`: ursprüngliche X/Y aus dem Snapshot wiederherstellen.

Alle vier Dateien laufen standardmäßig als Transaktion mit Rollback.
Nach Prüfung ist `@Commit = 1` zum Übernehmen erforderlich; eine Vorschau von
02 erzeugt noch kein dauerhaftes Backup. Apply/Restore verlangen das vollständige
Backup und unveränderte Identität/Map aller 1100 Charaktere. Andere Charaktere,
Mapzuordnung, Blickrichtung, Skills und Inventare bleiben unverändert.
Backup nicht löschen, solange der Versuch nicht abgeschlossen ist.

Zwischen Cluster und Spread normal herunterfahren und neu starten. Für beide
Läufe **C · Zone00-Stock/Patch-Vergleich 1 → 1100** mit demselben Manifest
verwenden; `B: zuerst #` muss 0 sein. Derselbe Binärstand, Listener, Starttakt
(2 s), Retry-Verhalten, Mob-/NPC-Bestand und dieselbe Skillumgebung. Nicht parallel
Stock/Patch wechseln: deren unterschiedliche Mob-/NPC-Grenzen wären ein zweiter
Einflussfaktor. Beide Läufe separat als Cluster/Spread ablegen. Wenn möglich die
Reihenfolge umkehren und wiederholen.

## Bewertung und Grenzen

Vergleichen: Ready und ShinePlayer, erste fehlerhafte Stufe, Handoff-Retries,
1669-Disconnects, betroffene IDs, Mob/NPC-Zähler, WinDbg-FirstHit (nur passend
verifizierte Binary). Server-Zeit und UTC im Clienttrace berücksichtigen.
Gespeicherte Startpunkte sind kein Beweis der tatsächlichen Laufzeitposition:
Koordinatenreparatur, Tod, KQ/Prison oder Skills können sie verändern. Tatsächliche
Ankunft/Positionen kontrollieren; sonst ist der Vergleich nicht aussagekräftig.
Verteilung ändert auch Sichtbereich und Kontakte zu Mobs. Eine Verbesserung
belegt daher noch kein hartes Limit für Spieler auf einer Kachel.

DB-Felder wurden in `p_Char_SetAllData`/`p_Char_GetAllData` des bereitgestellten
NA2016-Backups identifiziert: `dbo.tCharacter`, `sID`, `nCharNo`, `nUserNo`,
`sLoginZone`, `nLoginZoneX`, `nLoginZoneY`, `nLoginZoneD`, `bDeleted`.
Die aktuelle lokale DB muss dasselbe Schema haben; bei Fehlern nicht improvisieren.
`p_Char_GetAllData` wird absichtlich **nicht aufgerufen**: die mitgelieferte
Variante enthält Rebirth-/State-Änderungen. Direkte SELECTs vermeiden diese.

CI prüft Generator, Verteilung von 1100 Identitäten, ungültige Punkte,
Credential-Geheimnisse und UI-Build. Echter SQL-Serverlauf und tatsächliche
Spielerpositionen sind damit noch nicht validiert.
