# Smart USB Backup 3.0 (WinUI 3)

Современное приложение для зеркального резервного копирования USB-флешек с нулевой нагрузкой на процессор в режиме ожидания.

<div align="center">
  <a href="https://github.com/COSinus205/SmartUsbBackup/releases/tag/v3.0.6">
    <img src="https://img.shields.io/badge/Download%20installer-3.0.6-blue?style=for-the-badge" alt="Download Smart USB Backup 3.0.6 installer">
  </a>
</div>

> Latest installer: [SmartUsbBackup-Setup-3.0.6.exe](https://github.com/COSinus205/SmartUsbBackup/releases/tag/v3.0.6)

## Основные возможности

- Отслеживание подключения/отключения USB без опроса (событийная модель).
- Зеркальное копирование с проверкой CRC и режимом «залить как есть».
- Настраиваемые профили для каждой флешки: зеркало, CRC32, автозапуск, обновление сразу при подключении.
- Персональные вкладки настроек для каждой флешки.
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

Результат: `installer/dist/SmartUsbBackup-Setup-3.0.6.exe`.

Установка per-user, без прав администратора. При удалении предлагается выбрать, какие копии флешек удалить.

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
| `Core/DriveRow.cs` | Модель строки таблицы флешек (INotifyPropertyChanged). |
| `Core/Settings.cs` | `AppSettings`/`SettingsStore`, чтение/сохранение `settings.json`. |
| `Core/UpdateService.cs` | Проверка и скачивание обновлений через GitHub Releases. |
| `Sync/BackupOrchestrator.cs` | Оркестрация синхронизации (подключение/отключение, sync all/one, wipe). |
| `Sync/FileSyncService.cs` | Копирование файлов (ArrayPool, `.tmp`, Move, CRC32). |
| `Sync/Crc32.cs` | Расчёт CRC32. |
| `Usb/UsbDeviceWatcher.cs` | `WM_DEVICECHANGE` через subclass окна (без polling). |
| `Usb/UsbFileWatcher.cs` | `FileSystemWatcher` для live-watch. |
| `Tray/NativeTrayIcon.cs` | Трей: `Shell_NotifyIcon`, меню, balloon. |
| `Interop/NativeMethods.cs` | Win32-interop/helper'ы (ShowWindow, SetForegroundWindow и др.). |
| `Interop/WindowMinSize.cs` | Минимальный размер окна через `WM_GETMINMAXINFO`. |
| `installer/SmartUsbBackup.iss` | Скрипт Inno Setup (per-user install, деинсталляция, Pascal Script). |
| `installer/dist/SmartUsbBackup-Setup-3.0.6.exe` | Собранный установщик (релиз). |

## Лицензия

MIT.