# Smart USB Backup 5.0.0 (WinUI 3)

![CI](https://github.com/COSinus205/SmartUsbBackup/actions/workflows/ci.yml/badge.svg)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D4)

Современное приложение для зеркального резервного копирования USB-флешек. В простое приложение не держит ни дескрипторов на флешке, ни фоновых watcher'ов: копирование запускается при подключении флешки или по кнопке, поэтому «Безопасное извлечение» в Windows работает без «Устройство занято».

> **Версия 5.0.0** — упрощение и переосмысление приложения: только зеркальное копирование, запуск при подключении флешки и по кнопке, без CRC, live-watch, polling и тюнинга буфера. Интерфейс без вкладок: общие настройки и настройки флешки открываются диалогами. Скрытые и системные файлы не копируются и больше не удаляются из существующих копий. Новые флешки добавляются выключенными. Старый `settings.json` читается совместимо; удалённые настройки игнорируются.

> **Последний релиз — 5.0.0.** Мажорная версия: поведение изменилось осознанно (см. `CHANGELOG.md`).

<div align="center">
  <a href="https://github.com/COSinus205/SmartUsbBackup/releases/tag/v5.0.0">
    <img src="https://img.shields.io/badge/Download%20installer-5.0.0-blue?style=for-the-badge" alt="Download Smart USB Backup 5.0.0 installer">
  </a>
</div>

> Latest installer: [SmartUsbBackup-Setup-5.0.0.exe](https://github.com/COSinus205/SmartUsbBackup/releases/tag/v5.0.0)

## Основные возможности

- Отслеживание подключения/отключения USB без опроса (событийная модель).
- Зеркальное копирование: то, чего нет на флешке, удаляется из бэкапа. Сравнение по размеру и дате изменения.
- Профили флешек по серийному номеру: включение резервирования и персональная папка хранения.
- Автоматическое копирование при подключении включённой флешки и ручная синхронизация по кнопке.
- Поддержка обычных флешек и внешних USB HDD/SSD.
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

Результат: `installer/dist/SmartUsbBackup-Setup-5.0.0.exe`.

Установка per-user, без прав администратора. При удалении предлагается выбрать, какие копии флешек удалить (в списке — полные пути, включая персональные папки профилей).

## Безопасное извлечение (safe-remove)

- `FileSystemWatcher` не используется: приложение не следит за изменениями на уже вставленной флешке. Копирование запускается при подключении включённой флешки и по кнопке «Синхронизировать».
- В простое на томе нет открытых файлов и каталогов, поэтому «Безопасное извлечение» в Windows проходит без «Устройство занято».
- При получении `DBT_DEVICEQUERYREMOVE` приложение отменяет активное копирование и ждёт завершения задачи (таймаут 1 секунда); при превышении возвращается `BROADCAST_QUERY_DENY`, то есть извлечение временно запрещается.
- `ConnectedSession` идемпотентен при повторном `Dispose`.

Если Windows всё равно показывает «Устройство занято»:

- Откройте Resource Monitor (resmon) → вкладка CPU → «Связанные дескрипторы», найдите процессы, держащие файлы на букве диска флешки, и закройте их.
- Либо используйте `handle.exe -a -u D:` из Sysinternals, чтобы найти и закрыть хэндлы.

## Настройки

Глобальные настройки `AppSettings` в `settings.json` (старые файлы читаются совместимо, неизвестные поля игнорируются):

| Настройка | По умолчанию | Назначение |
|---|---|---|
| `BaseBackupPath` | `%LocalAppData%\SmartUsbBackup\Backups` | Папка хранения копий. |
| `RunAtStartup` | `false` | Запускать приложение с Windows. |
| `StartMinimized` | `false` | Скрывать окно при автозапуске. |
| `AutoCheckForUpdates` | `true` | Автопроверка обновлений раз в сутки. |
| `LastUpdateCheck` | `DateTime.MinValue` | Время последней проверки обновлений. |
| `Drives` | `[]` | Профили флешек: `Serial`, `Label`, `FolderName`, `Enabled`, `FirstSeen`, `BaseBackupPathOverride`. |

Новые флешки добавляются с `Enabled = false`; включить резервирование можно в строке таблицы или на вкладке флешки.

## Структура проекта

| Путь | Назначение |
|---|---|
| `SmartUsbBackup.WinUI.csproj` | Основной проект WinUI 3 (.NET 8, unpackaged, self-contained). |
| `app.manifest` | Манифест приложения (DPI-aware, UAC). |
| `App.xaml` / `App.xaml.cs` | Точка входа: одиночный экземпляр, аргумент `-hidden`, исключения. |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Главное окно, таблица флешек, диалоги настроек, логика UI. |
| `Assets/appicon.ico` | Иконка приложения/окна/установщика. |
| `build-installer.bat` | Сборка через `dotnet publish` + Inno Setup. |
| `README.md` | Документация проекта. |
| `Core/AppLog.cs` | Логирование (`AppLog.Info/Error`). |
| `Core/BackupPresenceCache.cs` | Кэш «в папке бэкапа есть содержимое» — без дискового I/O на UI-потоке. |
| `Core/DriveRow.cs` | Модель строки таблицы флешек (INotifyPropertyChanged). |
| `Core/Settings.cs` | `AppSettings`/`SettingsStore`, чтение/сохранение `settings.json`. |
| `Core/UpdateService.cs` | Проверка и скачивание обновлений через GitHub Releases. |
| `Sync/BackupOrchestrator.cs` | Оркестрация синхронизации (подключение/отключение, sync all/one, wipe, safe-remove). |
| `Sync/FileSyncService.cs` | Зеркальное копирование (ArrayPool, `.tmp`, Move, защита недоступных каталогов). |
| `Usb/UsbDeviceWatcher.cs` | `WM_DEVICECHANGE` через subclass окна. |
| `Tray/NativeTrayIcon.cs` | Трей: `Shell_NotifyIcon`, меню, balloon. |
| `Interop/NativeMethods.cs` | Win32-interop/helper'ы (ShowWindow, SetForegroundWindow и др.). |
| `Interop/WindowMinSize.cs` | Минимальный размер окна через `WM_GETMINMAXINFO`. |
| `Properties/AssemblyInfo.cs` | `InternalsVisibleTo("SmartUsbBackup.Tests")`. |
| `SmartUsbBackup.Tests/` | xUnit-тесты: 90 тестовых случаев. |
| `installer/SmartUsbBackup.iss` | Скрипт Inno Setup (per-user install, деинсталляция, Pascal Script). |
| `installer/dist/SmartUsbBackup-Setup-5.0.0.exe` | Собранный установщик (релиз). |
| `CHANGELOG.md` | История изменений. |

> Тестовый харнесс `ejecttest` **не входит в репозиторий**; для локальных копий действуют правила `.gitignore` (`tools/ejecttest/`, `ejecttest/`, `*.ejecttest.*`).

## CI / Сборка

![CI](https://github.com/COSinus205/SmartUsbBackup/actions/workflows/ci.yml/badge.svg)

Запуск тестов локально:

```bat
dotnet test SmartUsbBackup.Tests/SmartUsbBackup.Tests.csproj
```

Все 90 тестовых случаев.

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
