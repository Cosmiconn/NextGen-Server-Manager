# 0.2.9 – Capacity / Map Limits

Diese Version erweitert den Server Manager um eine eigenständige Kapazitäts- und Kartenanalyse.

## Neue verifizierte Zone-Objektpools

Aus `ShineObjectManager::som_Initialize` in der NA2016 `Zone.exe` wurden Count und Objekt-Stride korreliert:

- ShinePlayer: 1500 × 180312 B
- ShineMob: 8000 × 9576 B
- ShineNPC: 256 × 9580 B
- ShineBandit: 2048 × 9836 B
- ShinePet: 1500 × 9684 B
- ShineMover: 1000 × 8236 B
- ShineServant: 500 × 9624 B
- ShineMiniHouse: 1000 × 53504 B
- ShineMagicField: 250 × 456 B
- ShineDoor: 1000 × 7956 B
- ShineEffectObject: 1000 × 467 B
- ShineDropItem: 3000 × 651 B
- ShineAxialFlag: 3584 × 392 B

Die angezeigte Pool-RAM-Zahl ist nur `Count × Objekt-Stride`, nicht der gesamte Prozessverbrauch.

## Kartenanalyse

`*.shbd` wird als zwei Little-Endian Int32-Headerfelder plus Payload ausgewertet:

- X = Bytes pro Zeile
- Y = Zeilen
- Payload = X × Y Bytes
- Kollisionsgrid = X × 8 mal Y Zellen

Die in der Zone-MapBlock-Logik verwendete Blockfläche wird als `X × 50` und `Y × 6.25` berechnet. `Field.txt` xsize/ysize wird separat angezeigt, weil diese Metadaten nicht bei allen Karten 1:1 der SHBD-Fläche entsprechen.
