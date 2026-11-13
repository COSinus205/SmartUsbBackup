# Smart USB Backup 4.3.2 (WinUI 3)

![CI](https://github.com/COSinus205/SmartUsbBackup/actions/workflows/ci.yml/badge.svg)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D4)

Современное приложение для зеркального резервного копирования USB-флешек. В режиме ожидания приложение не держит постоянных дескрипторов на флешке; изменения отслеживаются лёгким polling с настраиваемым интервалом (по умолчанию 30 секунд), что обеспечивает корректную работу «Безопасного извлечения» в Windows.

> **Версия 4.3.2** — исправления и небольшие улучшения: безопасная проверка путей хранения (включая персональные папки профилей), корректное сравнение версий при обновлении, обязательный SHA-256 для автообновления, поддержка внешних USB HDD/SSD, перенос существующих копий при смене папки и более информативный деинсталлятор. Формат `settings.json` совместим с 4.3.1.

> **Последний релиз — 4.3.2.**

<div align="center">
  <a href="https://github.com/COSinus205/SmartUsbBackup/releases/tag/v4.3.2">
    <img src="https://img.shields.io/badge/Download%20installer-4.3.2-blue?style=for-the-badge" alt="Download Smart USB Backup 4.3.2 installer">
  </a>
</div>

> Latest installer: [SmartUsbBackup-Setup-4.3.2.exe](https://github.com/COSinus205/SmartUsbBackup/releases/tag/v4.3.2)

## Основные возможности

- Отслеживание подключения/отключения USB без опроса (событийная модель).
- Зеркальное копирование с проверкой CRC и режимом «залить как есть».
- Настраиваемые профили для каждой флешки: зеркало, CRC32, автозапуск, обновление сразу при подключении.
- Персональные вкладки настроек для каждой флешки.
- Поддержка обычных флешек и (опционально) внешних USB HDD/SSD.
- Адаптивный Fluent-интерфейс с вкладками, тёмной/светлой темой и Mica.
- Работа в фоне и из системного трея, автопроверка обновлений.

## Сборка

Требования: .NET 8 SDK, Windows 11, Visual Studio 2022 17.8+ (по желанию).

Сборка приложения:

```bat
dotnet publish -c Release
```

Результат: `bin/publish/SmartUsbBackup.exe` и все необходимые файлы (self-contained).

Для разработки достаточно:

```bat
dotnet build -c Release
```

## Установщик

```bat
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\SmartUsbBackup.iss
```

Результат: `installer/dist/SmartUsbBackup-Setup-4.3.2.exe`.

Установка per-user, без прав администратора. При удалении предлагается выбрать, какие копии флешек удалить (в списке — полные пути, включая персональные папки профилей).

## Безопасное извлечение (safe-remove)

- `FileSystemWatcher` не создаётся в конструкторе сессии и не удерживается на флешке постоянно. Watcher открывается только на время активной синхронизации и закрывается после её завершения.
- В простое отслеживание изменений идёт через лёгкий **polling**: периодический обход корня и папок первого уровня с расчётом подписи (количество элементов, `LastWriteTime`, размер). Хэндлы на том не удерживаются, поэтому «Безопасное извлечение» в Windows в типичном сценарии «вставил — извлёк» проходит без «Устройство занято».
- При получении `DBT_DEVICEQUERYREMOVE` / `DBT_DEVICEQUERYREMOVEFAILED` / `DBT_DEVICEQUERYREMOVEPENDING` приложение останавливает синхронизацию и освобождает хэндлы в пределах таймаута 2000 мс (`BROADCAST_QUERY_DENY` при превышении).
- `ConnectedSession` идемпотентен при повторном `Dispose`; `ConnectedSession.FileWatcher` может быть `null` (watcher создаётся лениво). `RunDriveSettingsTest` принимает session-scoped `CancellationToken`, отменяемый при `BeginDetachForSafeRemoval`.

Если Windows всё равно показывает «Устройство занято»:

- Откройте Resource Monitor (resmon) → вкладка CPU → «Связанные дескрипторы», найдите процессы, держащие файлы на букве диска флешки, и закройте их.
- Либо используйте `handle.exe -a -u D:` из Sysinternals, чтобы найти и закрыть хэндлы.
- Проверьте, что не включена настройка `ForceLegacyWatcher=true` (она только для отладки).

## Настройки

Новые поля `AppSettings` в `settings.json`. Обратная совместимость сохранена — старые файлы десериализуются с дефолтами:

| Настройка | По умолчанию | Назначение |
|---|---|---|
| `HandleSafeRemoval` | `true` | Обработка `DBT_DEVICEQUERYREMOVE`: синхронное освобождение хэндлов и отмена sync/watcher. `false` — полный no-op. |
| `UsePollingInIdle` | `true` | Включить polling в простое. |
| `PollingIntervalSeconds` | `30` (мин. `5`) | Интервал polling в секундах. |
| `ForceLegacyWatcher` | `false` | Только для отладки/возврата старого поведения (постоянный `FileSystemWatcher`). |

Остальные настройки (зеркало, CRC, live-watch и т.д.) — см. `Core/Settings.cs`.

## Структура проекта

| Путь | Назначение |
|---|---|
| `SmartUsbBackup.WinUI.csproj` | Основной проект WinUI 3 (.NET 8, unpackaged, self-contained). |
| `app.manifest` | Манифест приложения (DPI-aware, UAC). |
| `App.xaml` / `App.xaml.cs` | Точка входа: одиночный экземпляр, аргумент `-hidden`, исключения. |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Главное окно, вкладки, таблица флешек, логика UI. |
| `Assets/appicon.ico` | Иконка приложения/окна/установщика. |
| `build-installer.bat` | Сборка через `dotnet publish` + Inno Setup. |
| `README.md` | Документация проекта. |
| `Core/AppLog.cs` | Логирование (`AppLog.Info/Error`). |
| `Core/BackupPresenceCache.cs` | Кэш «в папке бэкапа есть содержимое» — без дискового I/O на UI-потоке. |
| `Core/DriveRow.cs` | Модель строки таблицы флешек (INotifyPropertyChanged). |
| `Core/Settings.cs` | `AppSettings`/`SettingsStore`, чтение/сохранение `settings.json`. |
| `Core/UpdateService.cs` | Проверка и скачивание обновлений через GitHub Releases. |
| `Sync/BackupOrchestrator.cs` | Оркестрация синхронизации (подключение/отключение, sync all/one, wipe). |
| `Sync/FileSyncService.cs` | Копирование файлов (ArrayPool, `.tmp`, Move, CRC32). |
| `Sync/Crc32.cs` | Расчёт CRC32. |
| `Usb/UsbDeviceWatcher.cs` | `WM_DEVICECHANGE` через subclass окна. |
| `Usb/UsbFileWatcher.cs` | `FileSystemWatcher` для live-watch (создаётся лениво). |
| `Tray/NativeTrayIcon.cs` | Трей: `Shell_NotifyIcon`, меню, balloon. |
| `Interop/NativeMethods.cs` | Win32-interop/helper'ы (ShowWindow, SetForegroundWindow и др.). |
| `Interop/WindowMinSize.cs` | Минимальный размер окна через `WM_GETMINMAXINFO`. |
| `Properties/AssemblyInfo.cs` | `InternalsVisibleTo("SmartUsbBackup.Tests")`. |
| `SmartUsbBackup.Tests/` | xUnit-тесты: 92 тестовых случая (47 `[Fact]`, 8 `[Theory]`/45 `[InlineData]`). |
| `installer/SmartUsbBackup.iss` | Скрипт Inno Setup (per-user install, деинсталляция, Pascal Script). |
| `installer/dist/SmartUsbBackup-Setup-4.3.2.exe` | Собранный установщик (релиз). |
| `CHANGELOG.md` | История изменений. |

> Тестовый харнесс `ejecttest` **не входит в репозиторий**; для локальных копий действуют правила `.gitignore` (`tools/ejecttest/`, `ejecttest/`, `*.ejecttest.*`).

## CI / Сборка

![CI](https://github.com/COSinus205/SmartUsbBackup/actions/workflows/ci.yml/badge.svg)

Запуск тестов локально:

```bat
dotnet test SmartUsbBackup.Tests/SmartUsbBackup.Tests.csproj
```

Все 92 тестовых случая (47 `[Fact]` + 45 `[InlineData]` в 8 `[Theory]`).

Сборка без Visual Studio:

```bat
dotnet build SmartUsbBackup.WinUI.csproj -c Release -p:Platform=x64
```

## Безопасность

Проверяется при автообновлении:
- SHA-256 скачанного файла (обязательно; без хеша обновление не предлагается, при несовпадении — отказ).
- Authenticode-подпись, если она есть. Сертификат издателя пока не получен, поэтому отсутствие подписи не блокирует установку (целостность уже подтверждена SHA-256); подпись будет включена как обязательная после получения сертификата.
- allowlist хостов загрузки (только `github.com` и GitHub CDN).

Осознанно НЕ проверяется:
- TLS-pinning (доверяем стандартной проверке сертификатов Windows).
- Authenticode-издатель (будет включено после получения сертификата).

## Флешки без серийного номера

Такие флешки получают профиль `NOSERIAL-XXXXXXXX` на основе метки тома; две разные флешки без серийника и с одинаковой меткой могут слиться в один профиль.

## Документация

- [CHANGELOG.md](CHANGELOG.md) — история изменений.
- [CONTRIBUTING.md](CONTRIBUTING.md) — как собрать, запустить тесты и собрать установщик.
- [SECURITY.md](SECURITY.md) — политика безопасности.

## Лицензия

MIT.
