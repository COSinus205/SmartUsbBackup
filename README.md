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

- `MainWindow.xaml` — главное окно с вкладками.
- `Core/` — модель строк и настроек.
- `Usb/` — отслеживание устройств и changed-событий.
- `Sync/` — оркестратор и сервис копирования.
- `Tray/` — иконка и меню трея.
- `installer/` — скрипт Inno Setup.

## Лицензия

MIT.