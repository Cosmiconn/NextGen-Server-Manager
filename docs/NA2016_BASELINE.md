# NA2016-Basis des aktuellen Projekts

Die erste Diagnosebibliothek wurde gegen die vom Nutzer bereitgestellten NA2016-Dateien aufgebaut.

## Stock-Ports

- Login: 9010 / 9011 / 9012
- WorldManager: 9013 / 9014 / 9015
- Zone00: 9016 / 9017 / 9018
- Zone01: 9019 / 9020 / 9021
- Zone02: 9022 / 9023 / 9024
- Zone03: 9025 / 9026 / 9027
- Zone04: 9028 / 9029 / 9030
- Account DB: 9031
- AccountLog DB: 9032
- Character DB: 9033
- GameLog DB: 9034

Der Manager liest diese Werte nicht hart aus dieser Dokumentation, sondern aus der installierten `ServerInfo.txt`. Damit funktionieren zusätzliche Zonen wie Zone05 dynamisch.

## Stock-Service-Skript

Das mitgelieferte `Start Services.ps1` startet Core-Dienste und WorldManager und wartet danach nur fünf Sekunden, bevor Zone00 bis Zone04 direkt hintereinander gestartet werden. Genau deshalb verwendet das Tool einen Readiness-basierten Smart Start.

## Referenz-Binaries (SHA-256)

- `WorldManager.exe`: `23e94c78840a80f874adffa15792ae29f5d25df950418b3633f6e5f5ba68ede1`
- `Zone.exe`: `db1cb42912556a4ea5cde5c18f15f2495b81465c70ca9c18ad5bc7e36611aff5`
- `Login.exe`: `15132fe91f76b61beff877f0a80725a909fab4923cd9c7e9da76fe87fe1ccf99`
- `Account.exe`: `0d421d1a338d9f23886b97f2313489aea2723a636b9e8ffeb143fc3c59e9aaa9`
- `AccountLog.exe`: `b01f455cf566fab9e889e51b6fa8b81ee78440db95b4aac54e4b2ff139f278d0`
- `Character.exe`: `2889640536a271468ef17cc6c74bd6f8799222e3bd95b81d53ea59d41c5a9e1c`
- `GameLog.exe`: `6e855b2330577065935f0cc2326d14a0b126c4d3de4880a0aed6c03db8eeb7c6`

Alle Zone00–Zone04-Binaries der gelieferten Basis sind byteidentisch. Zusätzliche Zonen werden deshalb beim Referenz-Audit gegen dieselbe Zone.exe geprüft.
