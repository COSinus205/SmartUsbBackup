# Smart USB Backup 5.0.1 (WinUI 3)

![CI](https://github.com/COSinus205/SmartUsbBackup/actions/workflows/ci.yml/badge.svg)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%2011-0078D4)

Современное приложение для зеркального резервного копирования USB-флешек. В простое приложение не держит ни дескрипторов на флешке, ни фоновых watcher'ов: копирование запускается при подключении флешки или по кнопке, поэтому «Безопасное извлечение» в Windows работает без «Устройство занято».

> **Версия 5.0.1** — исправления после 5.0.0: защита бэкапа от ошибочной очистки и переноса в собственную подпапку, безопасная ручная синхронизация, обновление пути в таблице, кардридеры/SD, ежедневная проверка обновлений и закрытые P3-дефекты. Базовая модель 5.0.0 прежняя: только зеркальное копирование, запуск при подключении и по кнопке, без CRC, live-watch, polling и тюнинга буфера.

> **Текущая версия — 5.0.1.**

<div align="center">
  <a href="https://github.com/COSinus205/SmartUsbBackup/releases/tag/v5.0.1">
    <img src="https://img.shields.io/badge/Download%20installer-5.0.1-blue?style=for-the-badge" alt="Download Smart USB Backup 5.0.1 installer">
  </a>
</div>

> Latest installer: [SmartUsbBackup-Setup-5.0.1.exe](https://github.com/COSinus205/SmartUsbBackup/releases/tag/v5.0.1)

## Основные возможности

- Отслеживание подключения/отключения USB без опроса (событийная модель).
- Зеркальное копирование: то, чего нет на флешке, удаляется из бэкапа. Сравнение по размеру и дате изменения.
- Профили флешек по серийному номеру: включение резервирования и персональная папка хранения.
- Автоматическое копирование при подключении включённой флешки и ручная синхронизация по кнопке.
- Поддержка обычных флешек и внешних USB HDD/SSD.
- Защита от ошибочной очистки: если снимок источника внезапно пуст, а в бэкапе есть файлы, зеркальная чистка пропускается.
- Адаптивный Fluent-интерфейс: одна страница, настройки в диалогах, тёмная/светлая тема, Mica.
- Работа в фоне и из системного трея, автопроверка обновлений.

## Сборка

Требования: .NET 8 SDK, Windows 11, Visual Studio 2022 17.8+ (по желанию).

Сборка приложения:

```bat
dotnet publish -c Release -p:Platform=x64
```

Результат: `bin/publish/SmartUsbBackup.exe` и все необходимые файлы (self-contained).

Для разработки достаточно:

```bat
dotnet build -c Release -p:Platform=x64
```

## Установщик

```bat
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\SmartUsbBackup.iss
```

Результат: `installer/dist/SmartUsbBackup-Setup-5.0.1.exe`.

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
| `baseBackupPath` | `%LocalAppData%\SmartUsbBackup\Backups` | Папка хранения копий. |
| `autoCheckForUpdates` | `true` | Автопроверка обновлений (не чаще раза в сутки). |
| `lastUpdateCheck` | `DateTime.MinValue` | Время последней проверки обновлений. |
| `drives` | `[]` | Профили флешек: `serial`, `label`, `folderName`, `enabled`, `baseBackupPathOverride`. |

Автозапуск с Windows хранится не в JSON, а в `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

Новые флешки добавляются с `Enabled = false`; включить резервирование можно в строке таблицы или в настройках флешки.

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
| `Core/BackupPresenceCache.cs` | Кэш «в папке бэкапа есть содержимое» (2 с), чтобы не опрашивать диск на каждое обновление строки. |
| `Core/DirectoryMover.cs` | Перенос папки бэкапа: запрет вложенного назначения, копирование со сверкой размера, удаление исходника только после проверки. |
| `Core/DriveRow.cs` | Модель строки таблицы флешек (INotifyPropertyChanged). |
| `Core/PathSafety.cs` | Проверки путей: запрет корня/системных каталогов, device-путей, раскрытие 8.3-алиасов. |
| `Core/Settings.cs` | `AppSettings`/`SettingsStore`, чтение/сохранение `settings.json`. |
| `Core/UpdateService.cs` | Проверка и скачивание обновлений через GitHub Releases. |
| `Sync/BackupOrchestrator.cs` | Оркестрация синхронизации (подключение/отключение, sync all/one, wipe, safe-remove). |
| `Sync/FileSyncService.cs` | Зеркальное копирование (ArrayPool, `.tmp`, Move, защита недоступных каталогов). |
| `Usb/UsbDeviceWatcher.cs` | `WM_DEVICECHANGE` через subclass окна. |
| `Tray/NativeTrayIcon.cs` | Трей: `Shell_NotifyIcon`, меню, balloon. |
| `Interop/NativeMethods.cs` | Win32-interop/helper'ы (ShowWindow, SetForegroundWindow и др.). |
| `Interop/WinTrust.cs` | Authenticode-проверка установщика перед запуском. |
| `Interop/WindowMinSize.cs` | Минимальный размер окна через `WM_GETMINMAXINFO`. |
| `Properties/AssemblyInfo.cs` | `InternalsVisibleTo("SmartUsbBackup.Tests")`. |
| `.github/workflows/ci.yml` | CI: restore, build и тесты на `windows-latest`. |
| `SmartUsbBackup.Tests/` | xUnit-тесты: 106 тестовых случаев. |
| `installer/SmartUsbBackup.iss` | Скрипт Inno Setup (per-user install, деинсталляция, Pascal Script). |
| `installer/dist/SmartUsbBackup-Setup-5.0.1.exe` | Собранный установщик (релиз). |
| `CHANGELOG.md` | История изменений. |
| `BUGS.md` | Открытые дефекты, исправления 5.0.x и что не проверялось. |

> Тестовый харнесс `ejecttest` **не входит в репозиторий**; для локальных копий действуют правила `.gitignore` (`tools/ejecttest/`, `ejecttest/`, `*.ejecttest.*`).

## CI / Сборка

![CI](https://github.com/COSinus205/SmartUsbBackup/actions/workflows/ci.yml/badge.svg)

Запуск тестов локально:

```bat
dotnet test SmartUsbBackup.Tests/SmartUsbBackup.Tests.csproj -c Release -p:Platform=x64
```

Все 106 тестовых случаев.

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
- [BUGS.md](BUGS.md) — открытые дефекты и что не проверялось.

## Лицензия

MIT.
