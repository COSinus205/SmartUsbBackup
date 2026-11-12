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

    /// <summary>Когда флешка впервые была видна приложению.</summary>
    [JsonPropertyName("firstSeen")]
    public DateTime FirstSeen { get; set; } = DateTime.Now;

    /// <summary>Полный путь к папке бэкапа (внутри базовой папки).</summary>
    [JsonIgnore]
    public string BackupPath => Path.Combine(
        string.IsNullOrWhiteSpace(BaseBackupPathOverride) ? SettingsStore.Current.BaseBackupPath : BaseBackupPathOverride!,
        FolderName);

    [JsonPropertyName("settingsTested")]
    public bool SettingsTested { get; set; }

    [JsonPropertyName("mirrorModeOverride")]
    public bool? MirrorModeOverride { get; set; }

    [JsonPropertyName("verifyByCrcOverride")]
    public bool? VerifyByCrcOverride { get; set; }

    [JsonPropertyName("skipHiddenSystemOverride")]
    public bool? SkipHiddenSystemOverride { get; set; }

    [JsonPropertyName("liveWatchOverride")]
    public bool? LiveWatchOverride { get; set; }

    [JsonPropertyName("bufferSizeKbOverride")]
    public int? BufferSizeKbOverride { get; set; }

    [JsonPropertyName("flushAfterFileOverride")]
    public bool? FlushAfterFileOverride { get; set; }

    [JsonPropertyName("fastSmallFileCopyOverride")]
    public bool? FastSmallFileCopyOverride { get; set; }

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

    /// <summary>Режим «зеркало»: файлы, удалённые на флешке, удаляются и в бэкапе.</summary>
    [JsonPropertyName("mirrorMode")]
    public bool MirrorMode { get; set; } = true;

    /// <summary>
    /// Способ сравнения:
    ///     false: «Размер + Дата изменения» (не читает содержимое — быстро, чуть менее надёжно)
    ///   true : CRC32 поверх Size+Date (читает оба файла чанками — медленнее, но 100% точно)
    /// </summary>
    [JsonPropertyName("verifyByCrc")]
    public bool VerifyByCrc { get; set; } = false;

    /// <summary>Автозапуск вместе с Windows (HKCU\...\Run — не требует прав администратора).</summary>
    [JsonPropertyName("runAtStartup")]
    public bool RunAtStartup { get; set; } = false;

    /// <summary>Скрыть окно на старте при автозапуске.</summary>
    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; } = false;

    /// <summary>Инкрементальное копирование — игнорировать системные/скрытые файлы флешки.</summary>
    [JsonPropertyName("defaultBufferSizeKb")]
    public int DefaultBufferSizeKb { get; set; } = 4096;

    /// <summary>
    /// Физический сброс (fsync) после каждого файла. По умолчанию выключен:
    /// на USB-флешках это десятки миллисекунд на файл, т.е. минуты на тысячах
    /// мелких файлов. Копия и так атомарна (.tmp → Move), поэтому включение
    /// нужно только для максимальной устойчивости к выдёргиванию флешки.
    /// </summary>
    [JsonPropertyName("defaultFlushAfterFile")]
    public bool DefaultFlushAfterFile { get; set; } = false;

    /// <summary>
    /// Если включено — мелкие файлы копируются прямо в целевой файл (быстрее,
    /// но менее безопасно при резком извлечении флешки). Если выключено —
    /// всегда используется атомарный путь .tmp → Move.
    /// </summary>
    [JsonPropertyName("defaultFastCopy")]
    public bool DefaultFastCopy { get; set; } = false;

    [JsonPropertyName("skipHiddenSystem")]
    public bool SkipHiddenSystem { get; set; } = true;

    [JsonPropertyName("autoTestNewDrives")]
    public bool AutoTestNewDrives { get; set; } = true;

    [JsonPropertyName("defaultEnabled")]
    public bool DefaultEnabled { get; set; } = false;

    /// <summary>
    /// Мгновенная синхронизация изменений (FileSystemWatcher): если true —
    /// добавление/удаление/изменение файла на флешке сразу запускает копирование.
    /// По умолчанию ВЫКЛЮЧЕНО: полный прогон только при подключении флешки или
    /// по кнопке «Синхронизировать» (пользователь просит предсказуемость).
    /// </summary>
    [JsonPropertyName("liveWatch")]
    public bool LiveWatch { get; set; } = false;

    /// <summary>
    /// Обработка Windows-запроса безопасного извлечения тома (DBT_DEVICEQUERYREMOVE):
    /// синхронное освобождение хэндлов и cancel sync/watcher. false = полный no-op, только TRUE.
    /// </summary>
    [JsonPropertyName("handleSafeRemoval")]
    public bool HandleSafeRemoval { get; set; } = true;

    [JsonPropertyName("pollingIntervalSeconds")]
    public int PollingIntervalSeconds { get; set; } = 30;

    [JsonPropertyName("usePollingInIdle")]
    public bool UsePollingInIdle { get; set; } = true;

    [JsonPropertyName("forceLegacyWatcher")]
    public bool ForceLegacyWatcher { get; set; } = false;

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

                    // Нормализуем Profile.FolderName для старых записей without folder name.
                    foreach (var d in s.Drives ?? new List<DriveProfile>())
                    {
                        if (string.IsNullOrWhiteSpace(d.FolderName))
                            d.FolderName = BuildFolderName(d.Label, d.Serial);
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
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Не удалось сохранить settings.json");
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
                if (!string.IsNullOrEmpty(Current.BaseBackupPath))
                    key?.SetValue(RegBasePathValue, Current.BaseBackupPath);
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
                    Enabled = Current.DefaultEnabled,
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
            if (PathSafety.IsReservedWindowsName(found.FolderName) ||
                found.FolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                found.FolderName.Contains("..", StringComparison.Ordinal) ||
                found.FolderName.Contains(Path.DirectorySeparatorChar) ||
                found.FolderName.Contains(Path.AltDirectorySeparatorChar))
            {
                found.FolderName = BuildFolderName(found.Label, found.Serial);
                Save();
                AppLog.Warn("FolderName перегенерирован как небезопасный: " + found.FolderName);
            }

            return found;
        }
    }

    /// <summary>"USB_Метка_Serial" — безопасное для файловой системы имя подпапки.</summary>
    private static string BuildFolderName(string label, string serial)
    {
        var INVALID = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var chars = label.ToCharArray();
        var sb = new System.Text.StringBuilder("USB_");
        foreach (var ch in chars)
        {
            if (Array.IndexOf(INVALID, ch) >= 0 || char.IsControl(ch)) sb.Append('_');
            else sb.Append(ch);
        }
        sb.Append('_').Append(serial.Replace("-", "").Replace(":", ""));

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
