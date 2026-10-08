# NA2016 Zone client session / listener audit

Stand: 2026-10-06

Ziel dieses Audits ist ausschließlich die Frage, ob das stockmäßige `nMaxAccept = 1500` der Zone ein zweiter, fest in `Zone.exe` verdrahteter Network-/Session-Hardcap ist oder ob die Sessionverwaltung dynamisch aus `ServerInfo.txt` dimensioniert wird.

Untersuchte Referenz:

- `Zone.exe` SHA-256: `DB1CB42912556A4EA5CDE5C18F15F2495B81465C70CA9C18AD5BC7E36611AFF5`
- zugehörige NA2016 `Zone.pdb`
- PE32/x86, Preferred Image Base `0x00400000`

## Ergebnis

Für den verifizierten NA2016-Build ist der Zone-Client-Sessionmanager **dynamisch aus `SERVER_INFO.nMaxAccept` dimensioniert**. Im untersuchten Start-/Sessionpfad wurde kein unabhängiger 1500er Network-/Session-Hardcap gefunden.

Das bedeutet ausdrücklich **nicht**, dass eine Stock-Zone sicher mehr als 1500 Spieler tragen darf: Der stockmäßige `ShinePlayer`-Objektpool bleibt 1500. Ein `nMaxAccept > 1500` darf deshalb nur zusammen mit einem passend verifizierten Player-Pool-Profil freigegeben werden.

## 1. PDB-Symbole und Adressen

Die PDB-Public-/Procedure-Records binden die relevanten Funktionen an folgende `.text`-Offsets bzw. Preferred VAs:

| Funktion | `.text`-Offset | Preferred VA |
|---|---:|---:|
| `ClientSessionManager::InitSessions(int)` | `0x0005B4C0` | `0x0045C4C0` |
| `ZoneBaseSessionManager::InitSessions(int)` | `0x001A5AB0` | `0x005A6AB0` |
| `ZoneServer::zs_Start_Acceptor()` | `0x001A8C60` | `0x005A9C60` |
| `ZoneServer::zs_GetClientSessionManager()` | `0x001A9040` | `0x005AA040` |
| `CSocket_Acceptor::Listen_Add(...)` | `0x00251820` | `0x00652820` |
| `CSocket_Acceptor::Listen_Start()` | `0x00251FA0` | `0x00652FA0` |

Die Adressen sind nur für den oben gepinnten Baseline-Build gültig.

## 2. `SERVER_INFO.nMaxAccept` wird direkt an `InitSessions` übergeben

Die PDB-Typinformation für `CServerInfo::SERVER_INFO` enthält die Mitglieder in dieser Reihenfolge:

1. `pName`
2. `nServerID`
3. `nWorldNo`
4. `nZoneNo`
5. `nServerIDFrom`
6. `pIP`
7. `nPort`
8. `nBackLog`
9. `nMaxAccept`

Bei 32-Bit-Pointern/Integern liegt `nMaxAccept` damit bei Offset `+0x20` innerhalb der 36-Byte-Struktur.

`ZoneServer::zs_Start_Acceptor()` legt den Client-`SERVER_INFO`-Datensatz lokal ab `EBP-0x48` ab. Direkt nach dem erfolgreichen `CServerInfo::GetServerInfo(...)` folgt im verifizierten Binary:

```text
005A9CA9  mov ecx,[ebp-28h]
005A9CAC  push ecx
005A9CAD  lea  ecx,[esi+9848h]
005A9CB3  call 0045C4C0  ; ClientSessionManager::InitSessions
```

`EBP-0x28` ist relativ zum Struct-Anfang `EBP-0x48` exakt `+0x20`: also `SERVER_INFO.nMaxAccept`.

Damit ist der Datenfluss belegt:

`ServerInfo.txt -> SERVER_INFO.nMaxAccept -> ClientSessionManager::InitSessions(max)`.

## 3. `ClientSessionManager::InitSessions` hat keinen 1500er Vergleich

`ClientSessionManager::InitSessions(int MaxSessions)`:

- verwirft den alten Zustand,
- akzeptiert jeden positiven `MaxSessions`-Wert (`>= 1`),
- ruft `ZoneBaseSessionManager::InitSessions(MaxSessions)` mit demselben Wert auf,
- berechnet die abgeleitete Session-Speichergröße dynamisch als `MaxSessions * 0x110 + 4`,
- konstruiert/initialisiert exakt `MaxSessions` Einträge,
- vergleicht die Initialisierungsschleife gegen den übergebenen Parameter, nicht gegen `1500`.

Der relevante Binärpfad liegt bei `0x0045C50F..0x0045C5A6`.

## 4. Auch die Basisklasse ist dynamisch

`ZoneBaseSessionManager::InitSessions(int MaxSessions)`:

- prüft ebenfalls nur `MaxSessions >= 1`,
- alloziert dynamisch `MaxSessions * 0x0C` Bytes für die Basissession-Liste,
- initialisiert exakt `MaxSessions` Listenelemente,
- speichert `MaxSessions` in `this+0x08`,
- setzt `m_NumSessions` (`this+0x0C`) auf 0.

Der Pfad bei `0x005A6AD3..0x005A6B4F` enthält keinen Vergleich gegen `0x5DC` / 1500.

Auch `ZoneBaseSessionManager::ActiveSession(...)` entscheidet anhand der gespeicherten dynamischen Werte `m_MaxSessions` und `m_NumSessions`; der Erschöpfungspfad verwendet keinen separaten 1500er Konstantvergleich.

## 5. Speicherwirkung von 1500 -> 2000

Nur die in den beiden verifizierten `InitSessions`-Allokationen direkt sichtbaren Managerstrukturen wachsen ungefähr um:

- Basissession-Liste: `500 * 0x0C = 6,000` Bytes
- ClientSession-Bereich: `500 * 0x110 = 136,000` Bytes
- zusammen: ca. `142,000` Bytes bzw. rund `0.14 MiB`

Das ist **nicht** der gesamte reale Speicher pro verbundenem Client (Socket-/Protokoll-/Spielerzustand kommt zusätzlich hinzu). Es zeigt nur, dass die Sessionmanager-Allokation selbst bei 2000 nicht durch einen großen statischen Block oder einen 1500er Array-Hardcode begrenzt ist.

## 6. Kontextprüfung der Konstanten `0x5DC` (1500)

Die im `.text` gefundenen relevanten 1500er Konstantstellen liegen im verifizierten Build überwiegend im `ShinePlayer`-/`ShinePet`-Handle-/Objektmanagerbereich sowie in nicht zum Client-Sessionmanager gehörenden Routinen. Im Funktionscluster

- `ZoneServer::zs_Start_Acceptor`,
- `ClientSessionManager::InitSessions`,
- `ZoneBaseSessionManager::InitSessions`,
- `ZoneBaseSessionManager::ActiveSession`

existiert kein separater `cmp ..., 0x5DC`-Hardcap.

## 7. Sicherheitsinvariante für NextGen

Aus diesem Audit folgt für die Implementierung:

- Stock `Zone.exe` + Stock `ShinePlayer=1500`: `nMaxAccept` bleibt auf maximal 1500 begrenzt.
- Das zertifizierte Profil `ShinePlayer=2000 / ShineMob=12000 / ShineNPC=512` darf für einen kontrollierten Test `nMaxAccept` bis maximal **2000** verwenden.
- **Kein** Listenerziel oberhalb des verifizierten Player-Pools. Insbesondere wird 2200 nicht freigegeben.
- Die 2000er Freigabe muss build-/hashgebunden und an den tatsächlich deployten zertifizierten Zone-Binary-Stand gekoppelt sein.
- Eine globale Änderung aller Zone-`SERVER_INFO`-Zeilen ist für einen Ein-Zonen-Test nicht ausreichend sicher, solange weitere Zonen noch Stock-Binaries verwenden. Der Testpfad muss die konkrete Zielzone isolieren bzw. nur deren Client-Listener-Konfiguration ändern.
- Keine Live-/In-Place-Binary-Patches für diesen Schritt.

## 8. Nächster Schritt

Vor dem Loadgenerator wird ein zielzonenspezifischer, transaktionaler 2000er Listener-Testpfad implementiert. Er darf nur dann freigeben, wenn:

1. die Ziel-`Zone.exe` exakt dem zertifizierten 2000/12000/512 SHA entspricht,
2. Deployment-Metadaten und Baseline-Backup gültig sind,
3. die konkrete `SERVER_INFO`-Zone eindeutig aufgelöst wurde,
4. `nMaxAccept <= 2000` bleibt,
5. die Änderung mit Backup/Manifest rückrollbar ist.

Erst danach wird der Headless-Client schrittweise bis über 1500 echte `ShinePlayer` skaliert.

## 9. Real-Capture-Beleg: Zone-Heartbeat-Richtung

Ein lokaler Originalclient-Mitschnitt vom 2026-10-07 wurde zusätzlich byte-/streamgenau gegen den Headless-Pfad geprüft. Der relevante Zone-TCP-Stream zeigt nach dem unverschlüsselten SH2/7-XOR-Handshake folgende Reihenfolge:

| Ereignis | Richtung | Zeitpunkt relativ zum Zone-TCP-Start |
|---|---|---:|
| CH6/1 MapLogin | Client -> Zone | ca. 0.029 s |
| SH6/2 MapLoginAck | Zone -> Client | ca. 0.541 s |
| CH6/3 MapLoginComplete | Client -> Zone | ca. 4.514 s |
| SH2/4 HeartbeatReq | Zone -> Client | ca. 34.588 s |
| CH2/5 HeartbeatAck | Client -> Zone | ca. 34.596 s |

Damit ist der erste Heartbeat im Mitschnitt rund **30.07 Sekunden nach CH6/3** servergetrieben. Im untersuchten Stream existiert **kein CH2/4 vom Client** und **kein SH2/5 von der Zone**.

Konsequenzen für den Loadgenerator:

- Holding darf **kein aktives CH2/4** erzeugen.
- Der Originalclient wartet auf **SH2/4** und antwortet darauf mit **CH2/5**.
- Liveness-Telemetrie muss den Roundtrip **SH2/4 -> CH2/5** erfassen.
- Eine Freshness-Schwelle unter 30 Sekunden ist für diesen Build falsch; der Manager verwendet deshalb ein 45-Sekunden-Diagnosefenster und unterscheidet frisch, noch nicht fällig und überfällig.
- **SH2/5** darf nicht als notwendiger Zone-Heartbeat-Ack dieses Originalclients interpretiert werden.

Dieser Capture-Beleg ersetzt die frühere, nur aus Symbolnamen abgeleitete Annahme eines client-initiierten CH2/4 -> SH2/5-Heartbeats.

## 10. Empirischer Player-Laststand: 500 ShinePlayer verifiziert

Am 2026-10-08 erreichte der capture-basierte Headless-Lasttest erstmals eine vollständig
serverseitig verifizierte Stufe von **500 gleichzeitigen ShinePlayern** auf der zertifizierten
Test-Zone. Die Stufe wurde nur als PASS akzeptiert, nachdem der PDB-/Runtime-Observer den
ShinePlayer-Zähler passend zur Anzahl der Ready-Simulatoren mehrfach exakt bestätigt hatte.

Der anschließende Aufbau der Stufe 600 scheiterte nicht im Zone-Login oder im ShinePlayer-Pool,
sondern bereits bei den neu hinzukommenden World-Charakterlogins ab Identität 501. Der Server
antwortete dort mit Department 4 / Opcode 2. Dieses Paket ist protocol-definiert als
`NC_CHAR_LOGINFAIL_ACK` und enthält einen 16-Bit-`err`-Wert; die frühere generische
Bezeichnung `SH4/2 ConnectError` war daher zu ungenau.

Für die weitere Kapazitätszertifizierung gilt deshalb:

- **500 gleichzeitige ShinePlayer sind empirisch PASS** für den getesteten Zustand.
- Ein Fehler beim World-Charakterlogin während der Erzeugung neuer Testidentitäten ist **kein
  Nachweis eines Zone-/ShinePlayer-Limits**.
- Account-/Character-Provisionierung wird vom eigentlichen Rampenbenchmark getrennt.
- Die Vorprovisionierung beweist pro Identität: Login, autoritative SH3/20 CharacterList,
  erfolgreiche Charakterauswahl und SH4/3 ZoneRedirect; anschließend wird die World-Verbindung
  wieder geschlossen.
- Erst ein daraus erzeugtes Benchmark-Manifest mit deaktiviertem `CreateCharacterIfMissing`
  darf für den vollständigen Ramp B verwendet werden.
- `NC_CHAR_LOGINFAIL_ACK` wird einschließlich seines `ushort err` protokolliert und bei
  `r_`-Testidentitäten nur begrenzt mit Backoff/Jitter erneut versucht.

Damit vermischt der 1600er Ramp künftig nicht mehr Datenbank-/Character-Erstellung mit der
eigentlichen Messung von World-/Zone-/ShinePlayer-Kapazität.

