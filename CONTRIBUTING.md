# Contributing

## Как собрать

```bat
dotnet build -c Release -p:Platform=x64
dotnet publish -c Release -p:Platform=x64
```

Результат публикации — папка `bin/publish/` (self-contained WinUI 3, .NET 8).

## Как запустить тесты

```bat
dotnet test SmartUsbBackup.Tests/SmartUsbBackup.Tests.csproj -c Release -p:Platform=x64
```

Все 106 тестовых случаев должны проходить.

## Как собрать установщик

Требуется Inno Setup 6:

```bat
build-installer.bat
```

Если Inno Setup установлен в нестандартное место, задайте переменную окружения `INNO_SETUP_ISCC` с полным путём к `ISCC.exe`.

Результат — `installer/dist/SmartUsbBackup-Setup-5.0.1.exe` (версия берётся из `installer/SmartUsbBackup.iss`, `#define MyAppVersion`).

## Публикация релиза

1. Поднять версию в `SmartUsbBackup.WinUI.csproj` и `installer/SmartUsbBackup.iss`.
2. Обновить `CHANGELOG.md`, `README.md`, `BUGS.md` и счётчик тестов.
3. Прогнать `dotnet test SmartUsbBackup.Tests/SmartUsbBackup.Tests.csproj -c Release -p:Platform=x64`.
4. Собрать установщик через `build-installer.bat` и записать SHA-256 рядом с exe.
5. Создать тег `vX.Y.Z` и релиз на GitHub с `SmartUsbBackup-Setup-X.Y.Z.exe` и `.sha256`.

## Стиль кода

- Придерживаться существующего стиля и структуры проекта.
- Не рефакторить работающий код без необходимости.
- Бизнес-логика в `Sync/`, `Usb/`, `Core/` меняется только при реальной необходимости, с тестами.

## Сообщения о багах

- GitHub Issues.
- По возможности приложить логи из `AppLog` (`%LocalAppData%\SmartUsbBackup\`, если доступны).
- Опишите версию приложения, шаги воспроизведения, ожидаемое и фактическое поведение.

## Тестовый харнесс

Тестовый харнесс `ejecttest` (и аналогичные диагностические инструменты) **не публикуется** в репозитории: это локальные инструменты, они игнорируются через `.gitignore` (`tools/ejecttest/`, `ejecttest/`).
