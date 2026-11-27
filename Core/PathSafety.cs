using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartUsbBackup.Core;

/// <summary>
/// Централизованные проверки безопасности путей.
/// SEC: B-01, B-02, B-03, B-04, D-01, C-01
/// </summary>
public static class PathSafety
{
    public static bool IsSafeSubPath(string basePath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(candidatePath))
            return false;

        string fullBase;
        string fullCandidate;
        try
        {
            fullBase = Path.GetFullPath(basePath);
            fullCandidate = Path.GetFullPath(candidatePath);
        }
        catch
        {
            // SEC: fail-closed
            return false;
        }

        if (!Path.IsPathRooted(fullBase) || !Path.IsPathRooted(fullCandidate))
            return false;

        var prefix = fullBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

        // Кандидат обязан быть строго внутри base; равенство base запрещено.
        return fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Совпадает с каталогом или находится внутри него.</summary>
    public static bool IsInsideOrEqual(string basePath, string candidatePath)
    {
        if (IsSafeSubPath(basePath, candidatePath)) return true;
        try
        {
            return string.Equals(
                Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Пути находятся на одном томе. При ошибке возвращает true (fail-closed):
    /// для проверки «бэкап не должен лежать на флешке» это безопасный ответ.
    /// </summary>
    public static bool IsSameVolume(string pathA, string pathB)
    {
        try
        {
            var rootA = Path.GetPathRoot(Path.GetFullPath(pathA));
            var rootB = Path.GetPathRoot(Path.GetFullPath(pathB));
            if (string.IsNullOrEmpty(rootA) || string.IsNullOrEmpty(rootB)) return true;
            return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    public static bool ContainsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true; // SEC: fail-closed

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return true; }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return true;

        string current = root;
        var relative = full.Substring(root.Length);
        foreach (var part in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    var attr = File.GetAttributes(current);
                    if ((attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        return true;
                }
            }
            catch
            {
                // SEC: fail-closed — недоступный компонент трактуем как небезопасный
                return true;
            }
        }
        return false;
    }

    public static bool IsReservedWindowsName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true; // пустое имя небезопасно

        // Концевые пробелы/точки небезопасны (Windows обрезает).
        if (name != name.Trim())
            return true;
        if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
            return true;

        var withoutExt = Path.GetFileNameWithoutExtension(name).Trim();
        var reserved = new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
        };
        foreach (var r in reserved)
        {
            if (string.Equals(withoutExt, r, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string? CanonicalizeBase(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return null;

        // Устройство/verbatim-путь (`\\?\`, `\\.\`) не должен попадать в настройки:
        // он обходит сравнение с системными каталогами.
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return null;
        }

        string full;
        try { full = Path.GetFullPath(trimmed); }
        catch { return null; }

        // 8.3-алиасы (`PROGRA~1`) раскрываем, иначе они обходят проверки ниже.
        full = ExpandShortPath(full);

        if (!Path.IsPathRooted(full))
            return null;

        // Корень диска запрещён.
        var root = Path.GetPathRoot(full);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var forbiddenExact = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        foreach (var f in forbiddenExact)
        {
            if (!string.IsNullOrWhiteSpace(f) &&
                string.Equals(full, Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        // Внутри системных каталогов хранить бэкапы нельзя (не только ровно в них).
        // LocalAppData/AppData/UserProfile сюда не входят: там подпапки разрешены
        // (путь по умолчанию — %LocalAppData%\SmartUsbBackup\Backups).
        var forbiddenTrees = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (var f in forbiddenTrees)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;

            string fullF;
            try { fullF = Path.GetFullPath(f); }
            catch { continue; }

            if (IsInsideOrEqual(fullF, full))
                return null;
        }

        // Любой прямой подкаталог Users\<имя> без своей подпапки запрещён.
        var parentDir = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(parentDir) &&
            string.Equals(Path.GetFileName(parentDir), "Users", StringComparison.OrdinalIgnoreCase) &&
            Path.IsPathRooted(parentDir))
        {
            return null;
        }

        return full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(
        string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    /// <summary>
    /// Раскрывает 8.3-алиасы существующей части пути. Для обычных путей
    /// (без `~`) ничего не делает и не трогает диск.
    /// </summary>
    private static string ExpandShortPath(string full)
    {
        if (!full.Contains('~'))
            return full;

        try
        {
            string existing = full;
            var tail = new Stack<string>();

            while (!string.IsNullOrEmpty(existing) &&
                   !Directory.Exists(existing) && !File.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, StringComparison.OrdinalIgnoreCase))
                    break;

                tail.Push(Path.GetFileName(existing));
                existing = parent;
            }

            if (string.IsNullOrEmpty(existing) || (!Directory.Exists(existing) && !File.Exists(existing)))
                return full;

            var sb = new StringBuilder(existing.Length + 260);
            uint n = GetLongPathNameW(existing, sb, (uint)sb.Capacity);
            if (n == 0 || n >= sb.Capacity)
                return full;

            string expanded = sb.ToString(0, (int)n);
            while (tail.Count > 0)
                expanded = Path.Combine(expanded, tail.Pop());

            return expanded;
        }
        catch
        {
            return full;
        }
    }
}