using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using SmartUsbBackup.Core;

namespace SmartUsbBackup.Tests;

/// <summary>
/// Изолирует тест от реальных пользовательских данных: сохраняет содержимое
/// settings.json и значения HKCU\Software\SmartUsbBackup, а по завершении
/// восстанавливает их. Нужно, потому что <see cref="SettingsStore"/> — статическое
/// хранилище с фиксированными путями.
/// </summary>
internal sealed class SettingsIsolation : IDisposable
{
    private const string RegKey = @"Software\SmartUsbBackup";

    private readonly string _settingsPath;
    private readonly string? _settingsJson;
    private readonly Dictionary<string, object?> _registryValues = new(StringComparer.OrdinalIgnoreCase);

    public SettingsIsolation()
    {
        _settingsPath = Path.Combine(SettingsStore.Dir, "settings.json");

        try
        {
            _settingsJson = File.Exists(_settingsPath) ? File.ReadAllText(_settingsPath) : null;
        }
        catch
        {
            _settingsJson = null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey);
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                    _registryValues[name] = key.GetValue(name);
            }
        }
        catch
        {
            // Реестр недоступен — изолируем хотя бы файл.
        }
    }

    public void Dispose()
    {
        try
        {
            if (_settingsJson is null)
            {
                if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
            }
            else
            {
                File.WriteAllText(_settingsPath, _settingsJson);
            }
        }
        catch
        {
            // Не мешаем тесту завершиться.
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                if (!_registryValues.ContainsKey(name))
                    key.DeleteValue(name, throwOnMissingValue: false);
            }

            foreach (var kv in _registryValues)
            {
                if (kv.Value is not null)
                    key.SetValue(kv.Key, kv.Value);
            }
        }
        catch
        {
            // Не критично для теста.
        }
    }
}
