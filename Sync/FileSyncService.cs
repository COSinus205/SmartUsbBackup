using System;
using System.Buffers;
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
    Scanning,   // строим список файлов
    Copying,    // копируем
    Pruning,    // «зеркало»: удаляем файлы, исчезнувшие на флешке
    Wipe,       // удаление бэкапа флешки
    Moving,     // перенос бэкапа флешки в другую папку
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

/// <summary>Ключ сравнения файлов: «Размер + Дата изменения в UTC».</summary>
internal readonly record struct FileKey(long Size, DateTime LastWriteUtc);

/// <summary>
/// «Двигатель» зеркального резервного копирования одной флешки.
/// Всегда зеркало: то, чего нет на флешке, удаляется из бэкапа.
/// Сравнение — только по размеру и дате (содержимое не читается).
/// </summary>
public sealed class FileSyncService
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Метаданные всех файлов (рекурсивно). Не открывает содержимое файлов.</summary>
    /// <param name="inaccessibleDirs">
    /// Каталоги, которые не удалось перечислить (нет доступа). Их файлы отсутствуют
    /// в снимке не потому, что удалены, а потому что не прочитаны. Зеркальная чистка
    /// обязана их не трогать, иначе бэкап теряет данные.
    /// </param>
    private List<FileMeta> SnapshotFiles(
        string root, IProgress<SyncProgress>? progress, out HashSet<string> inaccessibleDirs)
    {
        inaccessibleDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var opts = new EnumerationOptions
        {
            // Обход ручной: нужно знать, какие каталоги не открылись.
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var result = new List<FileMeta>();
        int count = 0;
        var sw = Stopwatch.StartNew();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
                {
                    if (entry is DirectoryInfo sub)
                    {
                        pending.Push(sub.FullName);
                        continue;
                    }

                    var path = entry.FullName;
                    try
                    {
                        var fi = (FileInfo)entry;
                        result.Add(new FileMeta(path, fi.Length, fi.LastWriteTimeUtc));
                    }
                    catch
                    {
                        // Файл исчез/недоступен во время обхода: размер вычислится при копировании.
                        result.Add(new FileMeta(path, 0, DateTime.MinValue));
                    }

                    count++;
                    if (progress is not null && (count % 1024 == 0 || sw.ElapsedMilliseconds >= 250))
                    {
                        progress.Report(new SyncProgress('\0', count, 0, 0, 0, 0, 0, path, SyncPhase.Scanning));
                        sw.Restart();
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Корень не читается вовсе — это не «пустая флешка»: чистка по пустому
                // снимку снесла бы весь бэкап. Пробрасываем наверх.
                if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                    throw;

                inaccessibleDirs.Add(dir);
                AppLog.Warn($"Каталог недоступен при сканировании, его файлы не будут удалены из бэкапа: {dir}");
            }
        }

        // Флешку вынули во время обхода — пустой снимок смертелен для зеркала.
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Источник недоступен во время сканирования: " + root);

        if (progress is not null && count > 0)
            progress.Report(new SyncProgress('\0', count, count, 0, 0, 0, 0, root, SyncPhase.Scanning));

        return result;
    }

    /// <summary>Зеркалирует <paramref name="sourceDir"/> (флешка) → <paramref name="targetDir"/> (бэкап).</summary>
    public async Task<SyncResult> SyncAllAsync(
        string sourceDir, string targetDir,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDir))
            return new SyncResult(0, 0, 0, 0, "Флешка отключена");

        List<FileMeta> sourceFiles;
        HashSet<string> inaccessibleDirs;
        long totalBytes = 0;
        try
        {
            sourceFiles = SnapshotFiles(sourceDir, progress, out inaccessibleDirs);
            foreach (var f in sourceFiles) totalBytes += f.Size;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"Не удалось просканировать {sourceDir}");
            return new SyncResult(0, 0, 0, 0, $"Ошибка сканирования: {ex.Message}");
        }

        var sw = Stopwatch.StartNew();
        int copied = 0, skipped = 0;
        long bytesCopied = 0;

        var targetIndex = BuildTargetIndex(targetDir);
        progress?.Report(new SyncProgress('\0', 0, sourceFiles.Count, 0, 0, 0, totalBytes, "", SyncPhase.Scanning));

        // Предварительное создание целевых каталогов.
        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fm in sourceFiles)
        {
            var dstDir = Path.GetDirectoryName(Path.Combine(targetDir, Path.GetRelativePath(sourceDir, fm.Path)))!;
            if (createdDirs.Contains(dstDir)) continue;
            try { Directory.CreateDirectory(dstDir); createdDirs.Add(dstDir); }
            catch { /* будет повторная попытка перед конкретным файлом */ }
        }

        var progressSw = Stopwatch.StartNew();
        int lastPercent = 0;

        for (int i = 0; i < sourceFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Флешку вынули прямо во время копирования: не перебираем оставшиеся файлы.
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

                bool needsCopy = !targetIndex.TryGetValue(rel, out var dstKey) || dstKey != srcKey;
                if (!needsCopy)
                {
                    skipped++;
                }
                else
                {
                    var dstDir = Path.GetDirectoryName(dstPath)!;
                    if (!createdDirs.Contains(dstDir))
                    {
                        Directory.CreateDirectory(dstDir);
                        createdDirs.Add(dstDir);
                    }

                    await CopyChunkedAsync(srcPath, dstPath, ct);

                    // .tmp → Move сохраняет время записи .tmp: возвращаем дату источника,
                    // иначе следующий прогон снова сочтёт файл новым.
                    System.IO.File.SetLastWriteTimeUtc(dstPath, srcMeta.LastWriteUtc);

                    copied++;
                    bytesCopied += srcMeta.Size;
                    targetIndex[rel] = srcKey;
                }

                int percent = totalBytes > 0
                    ? (int)(bytesCopied * 100L / totalBytes)
                    : (i == sourceFiles.Count - 1 ? 100 : 0);

                if (progress is not null &&
                    (percent != lastPercent || progressSw.ElapsedMilliseconds >= 100))
                {
                    progress.Report(new SyncProgress('\0', i + 1, sourceFiles.Count,
                        copied, skipped, bytesCopied, totalBytes, srcPath, SyncPhase.Copying));
                    lastPercent = percent;
                    progressSw.Restart();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Error(ex, $"Не удалось скопировать {srcPath}");
                if (!Directory.Exists(sourceDir))
                {
                    AppLog.Warn("Флешка отключена во время синхронизации — копирование остановлено");
                    return new SyncResult(copied, 0, skipped, bytesCopied, "Флешка отключена");
                }
            }
        }

        int deleted = MirrorPrune(sourceDir, targetDir, sourceFiles, inaccessibleDirs, targetIndex, copied, skipped, progress, ct);

        progress?.Report(new SyncProgress('\0', sourceFiles.Count, sourceFiles.Count,
            copied, skipped, bytesCopied, totalBytes, "", SyncPhase.Completed));

        AppLog.Info(
            $"SyncAll {sourceDir} → {targetDir}: копий {copied}, пропущено {skipped}, " +
            $"удалено {deleted}, {bytesCopied / 1048576.0:F1} МБ, {sw.Elapsed.TotalSeconds:F1} c");

        return new SyncResult(copied, deleted, skipped, bytesCopied, null);
    }

    /// <summary>Удаляет из бэкапа файлы, которых нет на флешке («зеркало»).</summary>
    private static int MirrorPrune(
        string sourceDir, string targetDir, List<FileMeta> sourceFiles, HashSet<string> inaccessibleDirs,
        Dictionary<string, FileKey> targetIndex, int copied, int skipped,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var fullTargetDir = Path.GetFullPath(targetDir);
        var root = Path.GetPathRoot(fullTargetDir);
        if (string.IsNullOrWhiteSpace(root) ||
            string.Equals(fullTargetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Error(null, "SEC B-04: mirror pruning отменён — targetDir является корнем");
            return 0;
        }

        var expectedOnSource = new HashSet<string>(
            sourceFiles.Select(s => Path.GetRelativePath(sourceDir, s.Path)),
            StringComparer.OrdinalIgnoreCase);

        // Файлы под каталогами, недоступными при сканировании, отсутствуют в снимке
        // не из-за удаления — из бэкапа их удалять нельзя.
        var protectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in inaccessibleDirs)
        {
            try { protectedDirs.Add(Path.GetRelativePath(sourceDir, d)); }
            catch { /* вне sourceDir — не наш случай */ }
        }

        var sep = Path.DirectorySeparatorChar;
        var prefix = fullTargetDir.TrimEnd(sep) + sep;
        var toDelete = new List<string>();
        int protectedSkipped = 0;

        foreach (var rel in targetIndex.Keys)
        {
            if (expectedOnSource.Contains(rel)) continue;
            if (IsUnderAnyDir(rel, protectedDirs)) { protectedSkipped++; continue; }

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

        if (protectedSkipped > 0)
            AppLog.Warn($"[MIRROR] Не удалено файлов: {protectedSkipped} — их каталог был недоступен при сканировании");

        int totalToPrune = toDelete.Count;
        progress?.Report(new SyncProgress('\0', 0, totalToPrune, copied, skipped, 0, totalToPrune, "", SyncPhase.Pruning));

        int pruned = 0;
        var pruneSw = Stopwatch.StartNew();
        var examples = new List<string>(3);

        foreach (var rel in toDelete)
        {
            ct.ThrowIfCancellationRequested();
            string dst = Path.GetFullPath(Path.Combine(fullTargetDir, rel));

            try
            {
                System.IO.File.Delete(dst);
                pruned++;
                targetIndex.Remove(rel);
                if (examples.Count < 3) examples.Add(dst);

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
            var suffix = examples.Count > 0 ? "; например: " + string.Join("; ", examples) : "";
            AppLog.Info($"[MIRROR] Удалено файлов: {pruned}{suffix}");
        }

        if (progress is not null && pruned > 0 && pruned != totalToPrune)
            progress.Report(new SyncProgress('\0', pruned, totalToPrune, copied, skipped, pruned, totalToPrune, "", SyncPhase.Pruning));

        CleanupEmptyDirs(fullTargetDir);
        return pruned;
    }

    /// <summary>Относительный путь лежит внутри одного из каталогов (или совпадает).</summary>
    private static bool IsUnderAnyDir(string relPath, HashSet<string> dirs)
    {
        if (dirs.Count == 0) return false;
        foreach (var d in dirs)
        {
            if (relPath.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
            if (relPath.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
        foreach (var fi in new DirectoryInfo(targetDir).EnumerateFiles("*", opts))
        {
            try { idx[Path.GetRelativePath(targetDir, fi.FullName)] = new FileKey(fi.Length, fi.LastWriteTimeUtc); }
            catch { /* файл мог исчезнуть во время обхода */ }
        }
        return idx;
    }

    /// <summary>Чанковое копирование с ArrayPool; запись во временный .tmp + атомарный Move.</summary>
    private static async Task CopyChunkedAsync(string src, string dst, CancellationToken ct)
    {
        var tmp = dst + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var srcStream = new FileStream(
                src, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                // Буфер не больше самого файла: не арендуем мегабайт под файл в 10 КБ.
                int chunk = (int)Math.Clamp(
                    Math.Min(BufferSize, Math.Max(64 * 1024, srcStream.Length)),
                    64 * 1024, BufferSize);

                await using var dstStream = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    chunk, FileOptions.Asynchronous);

                var buffer = ArrayPool<byte>.Shared.Rent(chunk);
                try
                {
                    int read;
                    while ((read = await srcStream.ReadAsync(buffer.AsMemory(0, chunk), ct)) > 0)
                        await dstStream.WriteAsync(buffer.AsMemory(0, read), ct);

                    await dstStream.FlushAsync(ct);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }

            System.IO.File.Move(tmp, dst, overwrite: true);
        }
        catch
        {
            // Отмена/ошибка/вынутая флешка не должны оставлять «хвост» .tmp.
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Удаление опустевших папок после зеркальной чистки (с глубоких к корню).</summary>
    private static void CleanupEmptyDirs(string root)
    {
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var dir in Directory.EnumerateDirectories(root, "*", opts)
                                           .OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    try { Directory.Delete(dir); } catch { /* занято — оставим до следующего раза */ }
                }
            }
        }
        catch { /* не критично */ }
    }

    private readonly record struct FileMeta(string Path, long Size, DateTime LastWriteUtc);
}
