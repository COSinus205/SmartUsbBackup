# Changelog

Все заметные изменения в проекте документируются в этом файле.

Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/),
проект придерживается [Semantic Versioning](https://semver.org/lang/ru/).

## [Unreleased]

## [4.3.0] - 2026-11-05

### Added
- Ленивое создание `FileSystemWatcher`: watcher открывается только на время активной синхронизации и закрывается по завершении.
- Polling в простое с настраиваемым интервалом (`PollingIntervalSeconds`, default 30 с, min 5 с) — отслеживает изменения в корне и папках первого уровня без постоянных дескрипторов на флешке.
- Настройки `UsePollingInIdle` (default `true`), `HandleSafeRemoval` (default `true`), `ForceLegacyWatcher` (default `false`, только для отладки).
- Диагностика «Устройство занято»: подсказка про Resource Monitor / `handle.exe` в README.

### Changed
- **Поведение safe-remove:** приложение больше не удерживает `ReadDirectoryChangesW`-хэндл на флешке в простое. «Безопасное извлечение» в Windows теперь проходит без «Устройство занято» в типичном сценарии «вставил — извлёк».
- `RunDriveSettingsTest` принимает session-scoped `CancellationToken`, отменяемый при `BeginDetachForSafeRemoval`.
- `ConnectedSession.Dispose` идемпотентен.
- `ConnectedSession.FileWatcher` может быть `null` (watcher создаётся лениво).

### Fixed
- Обработка `DBT_DEVICEQUERYREMOVE` / `DBT_DEVICEQUERYREMOVEFAILED` / `DBT_DEVICEQUERYREMOVEPENDING` (Variant 1, страховка для сценария активной синхронизации).
- `BROADCAST_QUERY_DENY` при таймауте закрытия хэндлов (1500 мс).

### Removed
- Постоянный `FileSystemWatcher` в конструкторе `ConnectedSession`.

## [4.2.0]

История изменений до 4.3.0 — см. [GitHub Releases](https://github.com/COSinus205/SmartUsbBackup/releases).
