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

    /// <summary>Когда флешка впервые была видна приложению.</summary>
    [JsonPropertyName("firstSeen")]
    public DateTime FirstSeen { get; set; } = DateTime.Now;

    /// <summary>Полный путь к папке бэкапа (внутри базовой папки).</summary>
    [JsonIgnore]
    public string BackupPath => Path.Combine(SettingsStore.Current.BaseBackupPath, FolderName);

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
    [JsonPropertyName("skipHiddenSystem")]
    public bool SkipHiddenSystem { get; set; } = true;

    /// <summary>
    /// Мгновенная синхронизация изменений (FileSystemWatcher): если true —
    /// добавление/удаление/изменение файла на флешке сразу запускает копирование.
    /// По умолчанию ВЫКЛЮЧЕНО: полный прогон только при подключении флешки или
    /// по кнопке «Синхронизировать» (пользователь просит предсказуемость).
    /// </summary>
    [JsonPropertyName("liveWatch")]
    public bool LiveWatch { get; set; } = false;

    /// <summary>Список известных флешек.</summary>
    [JsonPropertyName("drives")]
    public List<DriveProfile> Drives { get; set; } = new();

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
                if (s is not null) return s;
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
                var tmp = FilePath + ".tmp";
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
        return sb.ToString();
    }
}
