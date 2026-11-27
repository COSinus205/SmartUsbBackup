using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SmartUsbBackup.Core;

/// <summary>
/// Один «профиль» флешки. Флешка идентифицируется по Volume Serial Number + подписи тома,
/// НИКОГДА по букве диска — буквы меняются в зависимости от порта/занятости.
/// </summary>
public sealed class DriveProfile
{
    /// <summary>Volume Serial Number, присвоен при форматировании (стабилен).</summary>
    [JsonPropertyName("serial")]
    public string Serial { get; set; } = "";

    /// <summary>Метка тома (имя флешки, видимое в Проводнике).</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>Стабильное имя подпапки бэкапа, например "USB_04A2-9F13". Генерируется один раз.</summary>
    [JsonPropertyName("folderName")]
    public string FolderName { get; set; } = "";

    /// <summary>
    /// Пользователь включил резервирование для этой флешки?
    /// По умолчанию ВЫКЛЮЧЕНО: только что вставленная флешка сначала появляется
    /// в списке, а копирование начинается лишь после явной галочки «Активна».
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>Последняя буква диска, под которой флешка была подключена (только runtime).</summary>
    [JsonIgnore]
    public char DriveLetter { get; set; } = '\0';

    /// <summary>
    /// Полный путь к папке бэкапа (внутри базовой папки).
    /// Персональный путь перепроверяется на безопасность при каждом обращении:
    /// если он некорректен (например, settings.json отредактирован вручную),
    /// используется глобальная база, а не произвольный путь.
    /// </summary>
    [JsonIgnore]
    public string BackupPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(BaseBackupPathOverride))
            {
                var safeOverride = PathSafety.CanonicalizeBase(BaseBackupPathOverride);
                if (safeOverride is not null)
                    return Path.Combine(safeOverride, FolderName);
            }

            return Path.Combine(SettingsStore.Current.BaseBackupPath, FolderName);
        }
    }

    /// <summary>Персональная папка хранения (null — глобальная).</summary>
    [JsonPropertyName("baseBackupPathOverride")]
    public string? BaseBackupPathOverride { get; set; }

    public override string ToString() => $"{Label} [{Serial}]";
}

/// <summary>
/// Корневые настройки приложения. Хранятся в %LocalAppData%\SmartUsbBackup\settings.json.
/// Пишутся лишь при реальных изменениях (не по таймеру) — нулевой фоновой I/O.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Базовая папка для бэкапов. По умолчанию — %LocalAppData%\SmartUsbBackup\Backups.</summary>
    [JsonPropertyName("baseBackupPath")]
    public string BaseBackupPath { get; set; } = "";

    /// <summary>Список известных флешек.</summary>
    [JsonPropertyName("drives")]
    public List<DriveProfile> Drives { get; set; } = new();

    /// <summary>
    /// Автоматическая проверка обновлений в фоне (раз в сутки).
    /// false = не дергать сеть автоматически; ручная проверка из трея всегда доступна.
    /// </summary>
    [JsonPropertyName("autoCheckForUpdates")]
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>Последний раз, когда мы проверяли обновления на GitHub.</summary>
    [JsonPropertyName("lastUpdateCheck")]
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;
}

/// <summary>Хранилище настроек: одна точка записи/чтения settings.json.</summary>
public static class SettingsStore
{
    private static readonly object gate = new();

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartUsbBackup");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>
    /// Дублируем базовый путь бэкапов в реестр (HKCU\Software\SmartUsbBackup).
    /// НУЖНО ДЛЯ ДЕИНСТАЛЛЯТОРА: Inno Setup не умеет надёжно парсить UTF-8 JSON,
    /// а из реестра читает Unicode-строку корректно — пользовательский путь
    /// с русскими буквами не «побьётся».
    /// </summary>
    internal const string RegKey = @"Software\SmartUsbBackup";
    internal const string RegBasePathValue = "BaseBackupPath";

    /// <summary>Список всех баз хранения (глобальная + персональные) — читается деинсталлятором.</summary>
    internal const string RegBackupRootCountValue = "BackupRootCount";
    internal const string RegBackupRootValuePrefix = "BackupRoot";

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Папка бэкапов по умолчанию — %LocalAppData%\SmartUsbBackup\Backups.</summary>
    public static string DefaultBasePath => Path.Combine(Dir, "Backups");

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s is not null)
                {
                    // SEC: D-01 — каноническая проверка вместо чёрного списка подстрок.
                    var canonicalBase = PathSafety.CanonicalizeBase(s.BaseBackupPath);
                    if (canonicalBase is null)
                    {
                        AppLog.Warn("BaseBackupPath из settings.json небезопасен/некорректен — используется путь по умолчанию: " + s.BaseBackupPath);
                        s.BaseBackupPath = DefaultBasePath;
                    }
                    else
                    {
                        s.BaseBackupPath = canonicalBase;
                    }

                    // Нормализуем Profile.FolderName и персональные пути хранения.
                    // Явные null в JSON не должны превращаться в NRE дальше по коду.
                    s.Drives ??= new List<DriveProfile>();
                    // `"drives": [null]` в JSON: без фильтра foreach ниже падает,
                    // а внешний catch сбрасывает все настройки.
                    s.Drives.RemoveAll(d => d is null);
                    foreach (var d in s.Drives)
                    {
                        d.Serial ??= "";
                        d.Label ??= "";

                        if (IsUnsafeFolderName(d.FolderName))
                        {
                            AppLog.Warn("Небезопасное имя папки профиля — перегенерировано: " + d.FolderName);
                            d.FolderName = BuildFolderName(d.Label, d.Serial);
                        }

                        if (!string.IsNullOrWhiteSpace(d.BaseBackupPathOverride))
                        {
                            var canonicalOverride = PathSafety.CanonicalizeBase(d.BaseBackupPathOverride);
                            if (canonicalOverride is null)
                            {
                                AppLog.Warn("Небезопасный персональный путь хранения — сброшен: " + d.BaseBackupPathOverride);
                                d.BaseBackupPathOverride = null;
                            }
                            else
                            {
                                d.BaseBackupPathOverride = canonicalOverride;
                            }
                        }
                    }

                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось прочитать settings.json — использую настройки по умолчанию");
        }

        var fresh = new AppSettings { BaseBackupPath = DefaultBasePath };
        return fresh;
    }

    /// <summary>Сохранение: JSON атомарно (tmp → move) + дубль базового пути в реестр.</summary>
    public static void Save()
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
                // Атомарная замена: при сбое посреди записи старый файл остаётся целым.
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Не удалось сохранить settings.json");
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
                if (key is not null && !string.IsNullOrEmpty(Current.BaseBackupPath))
                    key.SetValue(RegBasePathValue, Current.BaseBackupPath);

                // Деинсталлятору нужен список ВСЕХ баз: иначе персональные папки
                // профилей (BaseBackupPathOverride) остаются вне его поля зрения.
                var roots = new List<string>();
                void AddRoot(string? path)
                {
                    var canonical = PathSafety.CanonicalizeBase(path);
                    if (canonical is null) return;
                    if (!roots.Exists(r => string.Equals(r, canonical, StringComparison.OrdinalIgnoreCase)))
                        roots.Add(canonical);
                }
                AddRoot(Current.BaseBackupPath);
                foreach (var d in Current.Drives) AddRoot(d.BaseBackupPathOverride);

                if (key is not null)
                {
                    key.SetValue(RegBackupRootCountValue, roots.Count, RegistryValueKind.DWord);
                    for (int i = 0; i < roots.Count; i++)
                        key.SetValue(RegBackupRootValuePrefix + i, roots[i]);

                    // Подчищаем значения, оставшиеся от прошлых сохранений
                    // (без ограничения «первые 16»).
                    foreach (var name in key.GetValueNames())
                    {
                        if (!name.StartsWith(RegBackupRootValuePrefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var suffix = name.AsSpan(RegBackupRootValuePrefix.Length);
                        if (int.TryParse(suffix, out int index) && index >= roots.Count)
                        {
                            try { key.DeleteValue(name, throwOnMissingValue: false); }
                            catch { /* не критично */ }
                        }
                    }
                }
            }
            catch { /* реестр не критичен */ }
        }
    }

    /// <summary>Найти профиль по серийному номеру (или создать новый, если отсутствует).</summary>
    public static DriveProfile GetOrCreate(string serial, string label)
    {
        // SEC: C-02 — пустой/whitespace серийный номер заменяем на дневной fallback-ключ.
        if (string.IsNullOrWhiteSpace(serial))
        {
            AppLog.Warn("Пустой серийный номер USB — используется fallback-ключ");
            using var sha = System.Security.Cryptography.SHA256.Create();
            var labelPart = string.IsNullOrWhiteSpace(label) ? "NO_LABEL" : label.Trim();
            var hashInput = labelPart + "|NOSERIAL";
            serial = "NOSERIAL-" + Convert.ToHexString(
                sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashInput)))[..8];
        }

        lock (gate)
        {
            var found = Current.Drives.Find(d =>
                string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                found = new DriveProfile
                {
                    Serial = serial,
                    Label = string.IsNullOrWhiteSpace(label) ? "USB-накопитель" : label,
                    FolderName = BuildFolderName(label, serial),
                    Enabled = false,
                };
                Current.Drives.Add(found);
                Save();
                AppLog.Info($"Новая флешка: «{label}» серийный {serial} → папка {found.FolderName}");
            }
            else if (!string.IsNullOrWhiteSpace(label) && label != found.Label)
            {
                // Метку обновляем в UI, но папку НЕ переименовываем — бэкап уже «привязан» к ней.
                found.Label = label;
                Save();
            }

            // SEC: C-02 — перегенерация небезопасного FolderName (fail-safe).
            if (IsUnsafeFolderName(found.FolderName))
            {
                found.FolderName = BuildFolderName(found.Label, found.Serial);
                Save();
                AppLog.Warn("FolderName перегенерирован как небезопасный: " + found.FolderName);
            }

            return found;
        }
    }

    /// <summary>Имя подпапки небезопасно: содержит разделители, «..», недопустимые символы или является зарезервированным.</summary>
    private static bool IsUnsafeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return PathSafety.IsReservedWindowsName(name) ||
               name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
               name.Contains("..", StringComparison.Ordinal) ||
               name.Contains(Path.DirectorySeparatorChar) ||
               name.Contains(Path.AltDirectorySeparatorChar);
    }

    /// <summary>"USB_Метка_Serial" — безопасное для файловой системы имя подпапки.</summary>
    private static string BuildFolderName(string? label, string? serial)
    {
        var INVALID = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var chars = (label ?? string.Empty).ToCharArray();
        var sb = new System.Text.StringBuilder("USB_");
        foreach (var ch in chars)
        {
            if (Array.IndexOf(INVALID, ch) >= 0 || char.IsControl(ch)) sb.Append('_');
            else sb.Append(ch);
        }
        sb.Append('_').Append((serial ?? string.Empty).Replace("-", "").Replace(":", ""));

        // SEC: C-01 — финальная санитизация.
        var name = sb.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name))
        {
            using var sha0 = System.Security.Cryptography.SHA256.Create();
            var bytes0 = System.Text.Encoding.UTF8.GetBytes(serial ?? string.Empty);
            var hash0 = sha0.ComputeHash(bytes0);
            name = "USB_" + Convert.ToHexString(hash0)[..8];
        }
        if (PathSafety.IsReservedWindowsName(name))
            name = "_" + name;
        if (name.Length > 80)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hashInput = (label ?? string.Empty) + (serial ?? string.Empty);
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashInput));
            var suffix = Convert.ToHexString(hash)[..8];
            name = name.Substring(0, Math.Min(name.Length, 80)) + "_" + suffix;
        }
        return name;
    }
}
