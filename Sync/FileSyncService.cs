using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SmartUsbBackup.Sync;

/// <summary>Фаза синхронизации — для отображения в прогресс-баре.</summary>
public enum SyncPhase
{
    Scanning,   // строим список файлов (быстро: ничего не читаем напрямую)
    Copying,    // копируем глубину
    Pruning,    // «зеркало»: удаляем файлы, исчезнувшие на флешке
    Done,
    Error,
}

/// <summary>Сообщение о прогрессе: (текущий_файл / всего_файлов, размер), путь к файлу.</summary>
public sealed record SyncProgress(
    int CurrentFile,
    int TotalFiles,
    long BytesDone,
    long TotalBytes,
    string CurrentPath,
    SyncPhase Phase);

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
    private const int CHUNK = 256 * 1024;

    private readonly bool _mirror;
    private readonly bool _verifyByCrc;
    private readonly bool _skipHiddenSystem;

    public FileSyncService(bool mirror, bool verifyByCrc, bool skipHiddenSystem)
    {
        _mirror = mirror;
        _verifyByCrc = verifyByCrc;
        _skipHiddenSystem = skipHiddenSystem;
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

        List<string> sourceFiles;
        long totalBytes;
        try
        {
            sourceFiles = SnapshotFiles(sourceDir);
            totalBytes = 0;
            foreach (var f in sourceFiles)
            {
                try { totalBytes += new FileInfo(f).Length; } catch { /*гонка с FS*/ }
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

        progress?.Report(new SyncProgress(0, sourceFiles.Count, 0, totalBytes, "", SyncPhase.Scanning));

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

            var srcPath = sourceFiles[i];
            try
            {
                var rel = Path.GetRelativePath(sourceDir, srcPath);
                var dstPath = Path.Combine(targetDir, rel);

                var srcFi = new FileInfo(srcPath);
                var srcKey = new FileKey(srcFi.Length, srcFi.LastWriteTimeUtc);

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
                    Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                    await CopyChunkedAsync(srcPath, dstPath, ct);
                    copied++;
                    bytesCopied += srcFi.Length;
                    targetIndex[rel] = srcKey; // копия теперь идентична источнику
                }

                progress?.Report(new SyncProgress(i + 1, sourceFiles.Count,
                    bytesCopied, totalBytes, srcPath, SyncPhase.Copying));

                // Микропауза между файлами, чтобы контроллер флешки и диск не работали на 100%.
                // Дозируется лишь в «массовой» фазе — это заметно снижает CPU + I/O ожидания.
                if (copied % 8 == 0) await Task.Yield();
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

        // 2) Режим «ЗЕРКАЛО»: удалить из бэкапа файлы, которых больше нет на флешке.
        int deleted = 0;
        if (_mirror)
        {
            progress?.Report(new SyncProgress(sourceFiles.Count, sourceFiles.Count,
                bytesCopied, totalBytes, "", SyncPhase.Pruning));

            foreach (var rel in targetIndex.Keys.ToList())
            {
                ct.ThrowIfCancellationRequested();
                var dst = Path.Combine(targetDir, rel);
                try
                {
                    System.IO.File.Delete(dst);
                    deleted++;
                    AppLog.Info($"[MIRROR] Удалён из бэкапа: {dst}");
                }
                catch (Exception ex) { AppLog.Error(ex, $"Не удалось удалить {dst}"); }
            }
            CleanupEmptyDirs(targetDir);
        }

        progress?.Report(new SyncProgress(sourceFiles.Count, sourceFiles.Count,
            bytesCopied, totalBytes, "", SyncPhase.Done));

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
    private List<string> SnapshotFiles(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = _skipHiddenSystem
                ? FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                : FileAttributes.ReparsePoint,
        };
        return Directory.EnumerateFiles(root, "*", opts).ToList();
    }

    /// <summary>Индекс приёмной папки: относительный путь → (размер, дата UTC).</summary>
    private static Dictionary<string, FileKey> BuildTargetIndex(string targetDir)
    {
        var idx = new Dictionary<string, FileKey>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(targetDir)) return idx;

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var f in Directory.EnumerateFiles(targetDir, "*", opts))
        {
            try
            {
                var fi = new FileInfo(f);
                idx[Path.GetRelativePath(targetDir, f)] = new FileKey(fi.Length, fi.LastWriteTimeUtc);
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
    private static async Task CopyChunkedAsync(string src, string dst, CancellationToken ct)
    {
        var tmp = dst + ".tmp";

        try
        {
            await using (var srcStream = new FileStream(
                src, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete, // не блокируем открытие пользователем
                CHUNK, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dstStream = new FileStream(
                tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                CHUNK, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CHUNK);
                try
                {
                    int read;
                    while ((read = await srcStream.ReadAsync(buffer.AsMemory(0, CHUNK), ct)) > 0)
                    {
                        await dstStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    await dstStream.FlushAsync(ct);
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
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
