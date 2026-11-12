using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartUsbBackup.Core;

namespace SmartUsbBackup.Sync;

/// <summary>Фаза синхронизации — для отображения в прогресс-баре.</summary>
public enum SyncPhase
{
    Idle,       // ничего не происходит
    Connecting, // подключение/чтение метаданных флешки
    Scanning,   // строим список файлов (быстро: ничего не читаем напрямую)
    Copying,    // копируем глубину
    Pruning,    // «зеркало»: удаляем файлы, исчезнувшие на флешке
    Wipe,       // удаление бэкапа флешки
    Moving,     // перенос бэкапа флешки в другую папку
    LiveWatch,  // ожидание изменений в фоне (live watch)
    Testing,    // однократный тест настроек при первом подключении (только чтение)
    Cancelled,  // пользователь/система отменили операцию
    Completed,  // синхронизация завершена успешно
    Error,      // произошла ошибка
}

/// <summary>Сообщение о прогрессе одной операции синхронизации.</summary>
public sealed record SyncProgress(
    char DriveLetter,
    int ProcessedCount,
    int TotalFiles,
    int Copied,
    int Skipped,
    long BytesDone,
    long TotalBytes,
    string CurrentFile,
    SyncPhase Phase)
{
    /// <summary>Отображаемое имя флешки («Метка [серийник]»).</summary>
    public string DriveName { get; init; } = "";

    /// <summary>Процент выполнения по байтам (0..100).</summary>
    public int Percent => TotalBytes > 0
        ? (int)Math.Clamp(BytesDone * 100.0 / TotalBytes, 0, 100)
        : (TotalFiles > 0 && ProcessedCount >= TotalFiles ? 100 : 0);
}

/// <summary>Итог одного прогона синхронизации.</summary>
public sealed record SyncResult(int Copied, int Deleted, int Skipped, long BytesCopied, string? Error);

/// <summary>
/// Ключ сравнения файлов: «Размер + Дата изменения в UTC».
/// Дешёвое сравнение без чтения содержимого (конкретная стратегия инкрементальности).
/// CRC32 подключается только как вторая проверка при verifyByCrc = true.
/// </summary>
internal readonly record struct FileKey(long Size, DateTime LastWriteUtc);

/// <summary>
/// «Двигатель» зеркального резервного копирования одной флешки.
///
/// Стратегия низкого потребления ресурсов:
///   • Запускается только ПО СОБЫТИЮ (подключение флешки; изменение файла от FileSystemWatcher).
///   • Дерево читается EnumerationOptions (Metadata-операции, без открытия содержимого).
///   • Инкрементальность: сравнение Size+LastWriteUtc, CRC32 — только для «подозрительных» пар.
///   • Чтение/запись чанками по 256 КБ с ArrayPool (нулевые аллокации в цикле копирования).
///   • Бэкап пишется во временный файл .tmp и атомарно переименовывается —
///     копия никогда не остаётся в «недокопированном» состоянии.
/// </summary>
public sealed class FileSyncService
{
    /// <summary>Размер буфера копирования, байт. 256 КБ — «золотая середина» для USB-накопителей.</summary>
    private const int CHUNK = 4 * 1024 * 1024;  // 4 MБ — быстрее на USB-накопителях

    private readonly bool _mirror;
    private readonly bool _verifyByCrc;
    private readonly bool _skipHiddenSystem;
    private readonly int _bufferSize;
    private readonly bool _flushAfterFile;
    private readonly bool _fastSmallFileCopy;
    private readonly bool _useTargetIndexCache;

    /// <summary>Кэш метаданных файла (избегаем повторного FileInfo в цикле копирования).</summary>
    private readonly record struct FileMeta(string Path, long Size, DateTime LastWriteUtc);

    public FileSyncService(bool mirror, bool verifyByCrc, bool skipHiddenSystem, int bufferSizeKb = 4096, bool flushAfterFile = true, bool fastSmallFileCopy = false, bool useTargetIndexCache = true)
    {
        _mirror = mirror;
        _verifyByCrc = verifyByCrc;
        _skipHiddenSystem = skipHiddenSystem;
        _bufferSize = Math.Clamp(bufferSizeKb * 1024, 64 * 1024, 16 * 1024 * 1024);
        _flushAfterFile = flushAfterFile;
        _fastSmallFileCopy = fastSmallFileCopy;
        _useTargetIndexCache = useTargetIndexCache;
    }

    // =====================================================================
    //  ПОЛНАЯ синхронизация (при подключении флешки / кнопке «Синхронизировать»)
    // =====================================================================

    /// <summary>
    /// Зеркалирует <paramref name="sourceDir"/> (флешка) → <paramref name="targetDir"/> (бэкап).
    /// Работает на фоне (async/await), прогресс — через IProgress&lt;SyncProgress&gt; (маршалинг в UI).
    /// </summary>
    public async Task<SyncResult> SyncAllAsync(
        string sourceDir, string targetDir,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDir))
            return new SyncResult(0, 0, 0, 0, "Флешка отключена");

        List<FileMeta> sourceFiles;
        long totalBytes;
        try
        {
            sourceFiles = SnapshotFiles(sourceDir, progress);
            totalBytes = 0;
            foreach (var f in sourceFiles)
            {
                totalBytes += f.Size;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"Не удалось просканировать {sourceDir}");
            return new SyncResult(0, 0, 0, 0, $"Ошибка сканирования: {ex.Message}");
        }

        var sw = Stopwatch.StartNew();
        int copied = 0, skipped = 0;
        long bytesCopied = 0;

        // Индекс приёмной стороны для O(1)-сравнения по относительному пути.
        var targetIndex = BuildTargetIndex(targetDir);
        if (_useTargetIndexCache) SaveTargetIndexCache(targetDir, targetIndex);

        progress?.Report(new SyncProgress('\0', 0, sourceFiles.Count, 0, 0, 0, totalBytes, "", SyncPhase.Scanning));

        // Предварительное создание всех целевых каталогов + учёт уже созданных:
        // CreateDirectory — это syscall, не нужно повторять его на каждый файл.
        var targetDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fm in sourceFiles)
        {
            targetDirs.Add(Path.GetDirectoryName(Path.Combine(targetDir, Path.GetRelativePath(sourceDir, fm.Path)))!);
        }
        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in targetDirs)
        {
            try
            {
                Directory.CreateDirectory(d);
                createdDirs.Add(d);
            }
            catch { /* будет повторная попытка перед конкретным файлом */ }
        }

        // Троттлинг UI-прогресса: лимит 1 отчёт на 100 мс или при сдвиге на 1%.
        var progressStopwatch = Stopwatch.StartNew();
        int lastReportedPercent = 0;

        for (int i = 0; i < sourceFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Флешку вынули прямо во время копирования: дальше перебирать 14 000
            // файлов и логировать ошибки бессмысленно — мгновенно выходим.
            if ((i & 0x7F) == 0 && !Directory.Exists(sourceDir))
            {
                AppLog.Warn("Флешка отключена во время синхронизации — копирование остановлено");
                return new SyncResult(copied, 0, skipped, bytesCopied, "Флешка отключена");
            }

            var srcMeta = sourceFiles[i];
            var srcPath = srcMeta.Path;
            try
            {
                var rel = Path.GetRelativePath(sourceDir, srcPath);
                var dstPath = Path.Combine(targetDir, rel);

                var srcKey = new FileKey(srcMeta.Size, srcMeta.LastWriteUtc);

                var dstKey = targetIndex.TryGetValue(rel, out var k) ? (FileKey?)k : null;

                bool needsCopy =
                    dstKey is null ||                                            // новый файл
                    dstKey.Value != srcKey ||                                    // размер/дата изменились
                    (_verifyByCrc && !await Crc32.AreEqualAsync(srcPath, dstPath, ct)); // глубокая проверка

                if (!needsCopy)
                {
                    skipped++;
                }
                else
                {
                    var dstDir = Path.GetDirectoryName(dstPath)!;
                    if (!createdDirs.Contains(dstDir))
                    {
                        // Папка ещё не создана (или предварительное создание не удалось):
                        // повторяем попытку, но не чаще одного раза на каталог.
                        Directory.CreateDirectory(dstDir);
                        createdDirs.Add(dstDir);
                    }

                    // Мелкие файлы выгоднее копировать силами ядра ОС (CopyFile): меньше
                    // managed-накладных расходов, а mtime/ctime переносятся автоматически.
                    bool copiedByOs = _fastSmallFileCopy && srcMeta.Size > 0 && srcMeta.Size <= 4 * 1024 * 1024;
                    if (copiedByOs)
                    {
                        System.IO.File.Copy(srcPath, dstPath, overwrite: true);
                    }
                    else
                    {
                        await CopyChunkedAsync(srcPath, dstPath, ct);

                        // .tmp → Move сохраняет время записи самого .tmp, поэтому возвращаем
                        // дату источника: иначе следующий прогон снова сочтёт файл новым.
                        System.IO.File.SetLastWriteTimeUtc(dstPath, srcMeta.LastWriteUtc);
                    }

                    copied++;
                    bytesCopied += srcMeta.Size;
                    targetIndex[rel] = srcKey; // копия теперь идентична источнику
                }

                // Троттлинг прогресса: не чаще 1 раза в 100 мс, либо при изменении
                // процента завершения хотя бы на 1% (что наступит раньше).
                int currentPercent = totalBytes > 0
                    ? (int)(bytesCopied * 100L / totalBytes)
                    : (i == sourceFiles.Count - 1 ? 100 : 0);

                if (progress is not null)
                {
                    bool shouldReport = currentPercent != lastReportedPercent ||
                                       progressStopwatch.ElapsedMilliseconds >= 100;
                    if (shouldReport)
                    {
                        progress.Report(new SyncProgress('\0', i + 1, sourceFiles.Count,
                            copied, skipped, bytesCopied, totalBytes, srcPath, SyncPhase.Copying));
                        lastReportedPercent = currentPercent;
                        progressStopwatch.Restart();
                    }
                }

            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Error(ex, $"Не удалось скопировать {srcPath}");

                // Причина ошибки — носитель исчез: не продолжаем «в пустоту».
                if (!Directory.Exists(sourceDir))
                {
                    AppLog.Warn("Флешка отключена во время синхронизации — копирование остановлено");
                    return new SyncResult(copied, 0, skipped, bytesCopied, "Флешка отключена");
                }
            }
        }

        int deleted = 0;
        if (_mirror)
        {
            // SEC: B-04 — удалять только внутри targetDir, без reparse points.
            var fullTargetDir = Path.GetFullPath(targetDir);
            // Защита от удаления корня диска/системной папки.
            var root = Path.GetPathRoot(fullTargetDir);
            if (string.IsNullOrWhiteSpace(root) ||
                string.Equals(fullTargetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                             StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Error(null, "SEC B-04: mirror pruning отменён — targetDir является корнем");
            }
            else
            {
                // Множество файлов, присутствующих на флешке (по относительному пути).
                var expectedOnSource = new HashSet<string>(
                    sourceFiles.Select(s => Path.GetRelativePath(sourceDir, s.Path)),
                    StringComparer.OrdinalIgnoreCase);

                var sep = Path.DirectorySeparatorChar;
                var prefix = fullTargetDir.TrimEnd(sep) + sep;

                var toDelete = new List<string>();
                foreach (var rel in targetIndex.Keys)
                {
                    if (expectedOnSource.Contains(rel)) continue;

                    string dst;
                    try { dst = Path.GetFullPath(Path.Combine(fullTargetDir, rel)); }
                    catch { AppLog.Error(null, "SEC B-04: некорректный путь: " + rel); continue; }

                    if (!dst.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Error(null, "SEC B-04: отказ удаления вне targetDir: " + dst);
                        continue;
                    }
                    if (PathSafety.ContainsReparsePoint(dst))
                    {
                        AppLog.Error(null, "SEC B-04: отказ удаления — reparse point: " + dst);
                        continue;
                    }

                    toDelete.Add(rel);
                }

                int totalToPrune = toDelete.Count;
                progress?.Report(new SyncProgress('\0', 0, totalToPrune, copied, skipped, 0, totalToPrune, "", SyncPhase.Pruning));

                int pruned = 0;
                var pruneSw = Stopwatch.StartNew();

                // В лог пишем не каждое удаление (при зеркалировании их бывают тысячи),
                // а одну сводную строку с парой примеров.
                var mirrorExamples = new List<string>(3);

                foreach (var rel in toDelete)
                {
                    ct.ThrowIfCancellationRequested();
                    string dst = Path.GetFullPath(Path.Combine(fullTargetDir, rel));

                    try
                    {
                        System.IO.File.Delete(dst);
                        deleted++;
                        pruned++;
                        if (_useTargetIndexCache) targetIndex.Remove(rel);
                        if (mirrorExamples.Count < 3) mirrorExamples.Add(dst);

                        // Троттлинг: тысячи удалений не должны «затапливать» UI-поток
                        // отдельным отчётом на каждый файл.
                        if (progress is not null && (pruned % 128 == 0 || pruneSw.ElapsedMilliseconds >= 150))
                        {
                            progress.Report(new SyncProgress('\0', pruned, totalToPrune, copied, skipped, pruned, totalToPrune, dst, SyncPhase.Pruning));
                            pruneSw.Restart();
                        }
                    }
                    catch (Exception ex) { AppLog.Error(ex, $"Не удалось удалить {dst}"); }
                }

                if (pruned > 0)
                {
                    var examples = mirrorExamples.Count > 0
                        ? "; например: " + string.Join("; ", mirrorExamples)
                        : "";
                    AppLog.Info($"[MIRROR] Удалено файлов: {pruned}{examples}");
                }

                if (progress is not null && pruned > 0 && pruned != totalToPrune)
                    progress.Report(new SyncProgress('\0', pruned, totalToPrune, copied, skipped, pruned, totalToPrune, "", SyncPhase.Pruning));

                CleanupEmptyDirs(fullTargetDir);
            }
        }

        if (_useTargetIndexCache) SaveTargetIndexCache(targetDir, targetIndex);

        progress?.Report(new SyncProgress('\0', sourceFiles.Count, sourceFiles.Count,
            copied, skipped, bytesCopied, totalBytes, "", SyncPhase.Completed));

        AppLog.Info(
            $"SyncAll {sourceDir} → {targetDir}: копий {copied}, пропущено {skipped}, " +
            $"удалено {deleted}, {bytesCopied / 1048576.0:F1} МБ, {sw.Elapsed.TotalSeconds:F1} c");

        return new SyncResult(copied, deleted, skipped, bytesCopied, null);
    }

    // =====================================================================
    //  ИНКРЕМЕНТАЛЬНАЯ синхронизация ОДНОГО файла (событие FileSystemWatcher)
    // =====================================================================

    /// <summary>Файл изменился на флешке → скопировать только его в бэкап.</summary>
    public async Task<bool> SyncSingleFileAsync(string srcFullPath, string sourceDir, string targetDir)
    {
        try
        {
            if (!System.IO.File.Exists(srcFullPath)) return false;

            var rel = Path.GetRelativePath(sourceDir, srcFullPath);
            var dst = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            await CopyChunkedAsync(srcFullPath, dst, CancellationToken.None);
            var srcInfo = new FileInfo(srcFullPath);
            System.IO.File.SetLastWriteTimeUtc(dst, srcInfo.LastWriteTimeUtc);
            AppLog.Info($"[WATCH] Обновлён в бэкапе: {dst}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"[WATCH] Ошибка копирования {srcFullPath}");
            return false;
        }
    }

    /// <summary>Файл удалён на флешке → удалить его в бэкапе (режим «зеркало»).</summary>
    public void DeleteFromBackup(string srcFullPath, string sourceDir, string targetDir)
    {
        if (!_mirror) return;
        try
        {
            var rel = Path.GetRelativePath(sourceDir, srcFullPath);
            var dst = Path.Combine(targetDir, rel);
            if (System.IO.File.Exists(dst))
            {
                System.IO.File.Delete(dst);
                AppLog.Info($"[WATCH][MIRROR] Удалён с флешки → удалён в бэкапе: {dst}");
            }
        }
        catch (Exception ex) { AppLog.Error(ex, $"[WATCH] Не удалось удалить {srcFullPath}"); }
    }

    // =====================================================================
    //  ВСПОМОГАТЕЛЬНОЕ
    // =====================================================================

    /// <summary>Метаданные всех файлов (рекурсивно). Не открывает содержимое файлов.</summary>
    private List<FileMeta> SnapshotFiles(string root, IProgress<SyncProgress>? progress = null)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = _skipHiddenSystem
                ? FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                : FileAttributes.ReparsePoint,
        };

        var result = new List<FileMeta>();
        int count = 0;
        var sw = Stopwatch.StartNew();
        var rootDir = new DirectoryInfo(root);
        foreach (var fi in rootDir.EnumerateFiles("*", opts))
        {
            var path = fi.FullName;
            try
            {
                result.Add(new FileMeta(path, fi.Length, fi.LastWriteTimeUtc));
            }
            catch
            {
                // Файл мог исчезнуть/быть недоступен во время обхода — сохраняем путь,
                // размер впоследствии будет вычислен при попытке копирования.
                result.Add(new FileMeta(path, 0, DateTime.MinValue));
            }

            count++;
            if (progress is not null && (count % 1024 == 0 || sw.ElapsedMilliseconds >= 250))
            {
                progress.Report(new SyncProgress('\0', count, 0, 0, 0, 0, 0, path, SyncPhase.Scanning));
                sw.Restart();
            }
        }

        if (progress is not null && count > 0)
        {
            progress.Report(new SyncProgress('\0', count, count, 0, 0, 0, 0, root, SyncPhase.Scanning));
        }
        return result;
    }

    /// <summary>Индекс приёмной папки: относительный путь → (размер, дата UTC).</summary>
    private Dictionary<string, FileKey> BuildTargetIndex(string targetDir)
    {
        var idx = new Dictionary<string, FileKey>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(targetDir)) return idx;

        if (_useTargetIndexCache)
        {
            var cached = TryReadTargetIndexCache(targetDir);
            if (cached is not null)
            {
                foreach (var e in cached.Entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Path)) continue;
                    idx[e.Path] = new FileKey(e.Size, new DateTime(e.LastWriteUtcTicks, DateTimeKind.Utc));
                }
                return idx;
            }
        }

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var fi in new DirectoryInfo(targetDir).EnumerateFiles("*", opts))
        {
            try
            {
                idx[Path.GetRelativePath(targetDir, fi.FullName)] = new FileKey(fi.Length, fi.LastWriteTimeUtc);
            }
            catch { /* файл мог исчезнуть во время обхода */ }
        }
        return idx;
    }

    /// <summary>
    /// Чанковое 256-КБ копирование с ArrayPool:
    ///   • GC почти не аллокирует (буфер переиспользуется);
    ///   • SequentialScan помогает планировщику ОС читать вперёд;
    ///   • запись во временный .tmp + атомарный Move = бэкап всегда согласован.
    /// Reserved temp-файлы «недокопированного» вида невозможны.
    /// </summary>
    private sealed class TargetIndexCache
    {
        public int Version { get; set; } = 1;
        public long RootLastWriteUtcTicks { get; set; }
        public List<TargetIndexEntry> Entries { get; set; } = new();
    }

    private sealed class TargetIndexEntry
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
    }

    private static string GetTargetIndexCachePath(string targetDir)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(targetDir)));
        return Path.Combine(SettingsStore.Dir, "target-index", hash.Substring(0, 24) + ".json");
    }

    private static TargetIndexCache? TryReadTargetIndexCache(string targetDir)
    {
        var path = GetTargetIndexCachePath(targetDir);
        if (!File.Exists(path) || !Directory.Exists(targetDir)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var cache = System.Text.Json.JsonSerializer.Deserialize<TargetIndexCache>(
                json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cache is null || cache.Version != 1) return null;
            var currentRoot = Directory.GetLastWriteTimeUtc(targetDir).Ticks;
            if (cache.RootLastWriteUtcTicks != currentRoot) return null;
            return cache;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveTargetIndexCache(string targetDir, Dictionary<string, FileKey> index)
    {
        if (!Directory.Exists(targetDir)) return;
        try
        {
            var cache = new TargetIndexCache
            {
                RootLastWriteUtcTicks = Directory.GetLastWriteTimeUtc(targetDir).Ticks,
                Entries = index.Select(kv => new TargetIndexEntry
                {
                    Path = kv.Key,
                    Size = kv.Value.Size,
                    LastWriteUtcTicks = kv.Value.LastWriteUtc.Ticks,
                }).ToList(),
            };

            var dir = Path.GetDirectoryName(GetTargetIndexCachePath(targetDir))!;
            Directory.CreateDirectory(dir);
            var final = GetTargetIndexCachePath(targetDir);
            var tmp = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(cache, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
            if (File.Exists(final)) File.Delete(final);
            File.Move(tmp, final);
        }
        catch
        {
            // Кэш не критичен: при неудаче просто будет полное сканирование.
        }
    }

    private async Task CopyChunkedAsync(string src, string dst, CancellationToken ct)
    {
        var tmp = dst + ".tmp";

        try
        {
            await using (var srcStream = new FileStream(
                src, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete, // не блокируем открытие пользователем
                _bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                // Буфер не больше самого файла: не арендуем 4 МБ под файл в 10 КБ.
                int chunk = (int)Math.Clamp(
                    Math.Min(_bufferSize, Math.Max(64 * 1024, srcStream.Length)),
                    64 * 1024, _bufferSize);

                await using var dstStream = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    chunk, FileOptions.Asynchronous);
                {
                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(chunk);
                    try
                    {
                        int read;
                        while ((read = await srcStream.ReadAsync(buffer.AsMemory(0, chunk), ct)) > 0)
                        {
                            await dstStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        }

                        // Flush(true) сбрасывает и managed-буфер, поэтому при включённом
                        // flushAfterFile отдельный FlushAsync не нужен (раньше была двойная работа).
                        if (_flushAfterFile) dstStream.Flush(true);
                        else await dstStream.FlushAsync(ct);
                    }
                    finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
                }
            }

            // Атомарная замена цели (overwrite: true) — без окна «файла нет».
            System.IO.File.Move(tmp, dst, overwrite: true);
        }
        catch
        {
            // Отмена/ошибка/вынутая флешка не должны оставлять «хвост» .tmp.
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Однопроходная чистка опустевших папок (глубина обхода задана циклом).</summary>
    private static void CleanupEmptyDirs(string root)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var dir in Directory.EnumerateDirectories(root, "*", opts)
                                           .OrderByDescending(d => d.Length)) // с глубоких к корню
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    try { Directory.Delete(dir); } catch { /* занято — оставим до следующего раза */ }
                }
            }
        }
        catch { /* не критично */ }
    }
}
