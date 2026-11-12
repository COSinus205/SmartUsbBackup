# Contributing

## Как собрать

```bat
dotnet build -c Release
dotnet publish -c Release
```

Результат публикации — папка `bin/publish/` (self-contained WinUI 3, .NET 8).

## Как запустить тесты

```bat
dotnet test SmartUsbBackup.Tests/SmartUsbBackup.Tests.csproj
```

Все 71 тестовый случай (30 `[Fact]` + 41 `[InlineData]` в 7 `[Theory]`) должны проходить.

## Как собрать установщик

Требуется Inno Setup 6:

```bat
build-installer.bat
```

Если Inno Setup установлен в нестандартное место, задайте переменную окружения `INNO_SETUP_ISCC` с полным путём к `ISCC.exe`.

Результат — `installer/dist/SmartUsbBackup-Setup-4.3.1.exe` (версия берётся из `installer/SmartUsbBackup.iss`, `#define MyAppVersion`).

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
