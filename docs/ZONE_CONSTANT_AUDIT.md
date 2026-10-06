# NA2016 Zone – Pool Constant Audit

Stand: 2026-10-06

Dieses Dokument ergänzt `ZONE_HANDLE_REBASE.md`. Ziel ist, gleiche Zahlenwerte im Binärfile **semantisch** zu trennen. Ein Wert wie `8000` bedeutet nicht automatisch ShineMob: derselbe Wert wird z. B. als 8-KB-Paketlimit verwendet.

Verifizierter Zone-Build: SHA-256 `db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5`.

## Neu verifizierte Pflichtabhängigkeiten in `ShineObjectManager::som_Initialize`

Die große Objektinitialisierung allokiert für jeden Typ einen zusammenhängenden Backing-Arrayblock. Für Player, NPC und Mob existieren jeweils vier voneinander unabhängige harte Stellen:

1. `operator new`: `capacity * objectStride + 4`
2. Elementanzahl für den Array-Constructor
3. gespeicherte Elementanzahl im Arrayheader
4. Initialisierungsschleife: `capacity * 12`

| Pool | Allocation bytes | Count arg | Count header | Init-loop bytes | Stride |
|---|---:|---:|---:|---:|---:|
| ShinePlayer | `0x55C5B4` | `0x55C5D6` | `0x55C5E4` | `0x55C6AB` | `0x2C058` |
| ShineNPC | `0x55C6B6` | `0x55C6D8` | `0x55C6E6` | `0x55C7AD` | `0x256C` |
| ShineMob | `0x55C9BD` | `0x55C9DF` | `0x55C9ED` | `0x55CAB4` | `0x2568` |

Stockwerte stimmen exakt mit den Formeln überein:

- Player: `1500 * 0x2C058 + 4 = 0x101F03A4`, Initspan `1500 * 12 = 0x4650`
- NPC: `256 * 0x256C + 4 = 0x256C04`, Initspan `256 * 12 = 0x0C00`
- Mob: `8000 * 0x2568 + 4 = 0x0490F204`, Initspan `8000 * 12 = 0x17700`

Diese Stellen sind **funktional zwingend**. Nur die Handlegrenzen anzuheben, ohne die Backing-Arrays zu vergrößern, wäre ein sicherer Speicherfehler.

## Player-spezifische Zusatzabhängigkeit

`0x5AF1CC` enthält `cmp esi, 1500` in der Startsequenz des Quest-Systems. Der umgebende Fehlertext lautet `Fail to player quest bf alloc`. Die Schleife läuft über die Player-Slots und muss daher bei einer ShinePlayer-Erhöhung dieselbe neue Player-Kapazität erhalten.

`0x5AE571` übergibt `1500` an die Diagnosezeile `Player Buffer size : %d`. Diese Stelle ist nicht kapazitätssteuernd, wird aber als Diagnoseabhängigkeit verfolgt, damit die Startausgabe nach einem späteren Profil nicht lügt.

## 1500: Player oder Pet?

Der Wert `1500` ist im Binärfile absichtlich doppelt belegt. Deshalb darf niemals global nach `0x5DC` gesucht/ersetzt werden.

**Player – muss mit ShinePlayer skalieren:**

- `0x548E36` generischer Player-Handle-Limitcheck
- `0x557622` Player-Pool-Maximum
- `0x559D50` klassenspezifischer Player-Limitcheck
- `0x55C5D6`, `0x55C5E4` Player-Backing-Array Count
- `0x5AF1CC` Player Quest-Buffer Loop
- `0x5AE571` Player-Buffer-Diagnose (nur Anzeige)

**Pet – bleibt bei 1500, wenn nur Player/Mob/NPC verändert werden:**

- `0x549046` generischer Pet-Limitcheck
- `0x557320` klassenspezifischer Pet-Limitcheck
- `0x55782C` Pet-Pool-Maximum
- `0x55CEEB`, `0x55CEF9` Pet-Backing-Array Count
- `0x55CFC1` gehört zum Pet-Initialisierungsspan `1500 * 12 = 0x4650`

Damit ist ein globales `1500 -> 2000` ausdrücklich verboten.

## 8000: Mob vs. 8-KB Buffer

Die direkten `0x1F40`-Treffer wurden funktionsbezogen geprüft.

**Mob/Handle-relevant:**

- `0x548E07` generischer Mob-Limitcheck
- `0x548E49` Player-Handle-Basis (= Ende des Mob-Bereichs)
- `0x556A51` klassenspezifischer Mob-Limitcheck
- `0x5576D0` Mob-Pool-Maximum
- `0x559D71` klassenspezifische Player-Handle-Basis
- `0x55C9DF`, `0x55C9ED` Mob-Backing-Array Count
- `0x633656` zentrale HandleSplit-Grenze Mob -> Player
- zusätzlich abgeleitet: `0x55C9BD` Allocation bytes und `0x55CAB4` Initspan

**Klassifizierte False Positives / nicht als Mob patchen:**

- `0x44E008`, `0x44E08F`, `0x559A7B`: Serialisierungs-/Packet-Buffer-Limit; `0x559A7B` ruft denselben Serializer `0x643D40` auf, dessen Parameter ein maximales Ausgabebuffer-Limit ist.
- `0x417D62`: `AbstateBuffer::ab_SaveAbstate`, Save-/Packetbuffer.
- `0x44D828`: `ItemSmallBag<SmallItemInform>::isb_FullBufferItem2Client`, Buffergrenze.
- `0x454068`, `0x4541E7`, `0x45485F`, `0x454D4A`, `0x4551F4`: Character-/Abstate-/Skill-Savebuffer.
- `0x4560D0`, `0x4561A0`, `0x456350`, `0x456430`: `ShinePlayer::so_SaveItem_Part`, expliziter `Packet Too Long`-Pfad.
- `0x4CBC6D`, `0x4CBDBA`: `ChangeByConditionParam` Sendbuffer, `Packet Too Long`.
- `0x50E68D`, `0x50E86D`: SellItem Server/Client FillBuffer.
- `0x5BB930`, `0x5BBB77`, `0x5BBCA1`: Quest-Save/WorldManager-Paketaufbau.
- `0x5BC810`: `WholeSaver::ws_save2worldmanager`, expliziter `Packet Too Long`-Pfad.
- `0x4392D1`, `0x4392DF`, `0x4392E7`: BMP-/AreaBMP-Dateiverarbeitung, nicht Objektpool.
- `0x67CB6E`, `0x6814A6`: Member-Offset-Dispatchtabellen; umgebende Werte laufen in 8/16-Byte-Schritten und sind keine Handle-Basen.
- `0x63E12C`: separater 16-Bit-Fallback-/Sentinelpfad (7808/8000), nicht der ShineMob-Objektpool.

## 1500 als falscher Member-Offset

`0x67A8C2` und `0x67ED32` addieren `0x5DC`, liegen aber in Member-Offset-Dispatchtabellen mit Nachbarwerten `0x5BC`, `0x5C4`, `0x5CC`, `0x5D4`, `0x5DC`, `0x5E4` usw. Diese beiden Stellen sind keine Player-Kapazität.

## NPC = 256

`256` ist im Programm ein extrem häufiger generischer Wert (Buffergrößen, Flags, Tabellen, Protokollfelder). Ein Value-Scan ist deshalb nicht als Beweis geeignet.

Im verifizierten Objektmanager-/NPC-Pfad sind derzeit eindeutig:

- `0x548F26` generischer NPC-Limitcheck
- `0x55765C` NPC-Pool-Maximum
- `0x557C80` klassenspezifischer NPC-Limitcheck
- `0x55C6D8`, `0x55C6E6` NPC-Backing-Array Count
- abgeleitet `0x55C6B6` Allocation bytes und `0x55C7AD` Initspan

Weitere `0x100`-Vorkommen werden **nicht** aufgrund des Zahlenwertes aufgenommen. Die Vollabdeckung bleibt gesperrt, bis der Symbol-/Call-Graph-Audit bestätigt, dass keine zusätzliche NPC-spezifische Schleife oder Hilfstabelle existiert.

## Aktueller Sicherheitsstatus

- Handle-Rebase-Kern: **69/69** bytegenau gegen die originale Zone.exe verifiziert.
- Zusätzliche bekannte Backing-/Player-Abhängigkeiten: **14 Stellen** in `ZoneBinaryDependencyAudit` modelliert.
- Gesamt bekannte adressgebundene Prüfpunkte: **83**.
- Echter Binärpatch: **weiterhin BLOCKIERT**.

Der nächste Gate ist kein weiteres Suchen nach Zahlenwerten, sondern die semantische Restprüfung der NPC-Hilfspfade und sämtlicher direkten Benutzer der Player/Mob/NPC-Listen. Erst danach darf `CoverageComplete` jemals auf `true` gesetzt werden.
