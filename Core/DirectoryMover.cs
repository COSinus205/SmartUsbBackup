using System;
using System.IO;
using System.Linq;

namespace SmartUsbBackup.Core;

/// <summary>
/// Перенос папки бэкапа на другой путь. Сначала пробует атомарный
/// <see cref="Directory.Move(string, string)"/> (тот же том), при
/// <see cref="IOException"/> — копирование с удалением исходника.
/// </summary>
internal static class DirectoryMover
{
    /// <summary>
    /// Переносит дерево каталогов. Бросает <see cref="InvalidOperationException"/>,
    /// если назначение совпадает с источником или лежит внутри него: иначе
    /// <c>Directory.Move</c> падает, а fallback скопировал бы данные во вложенную
    /// папку и удалил исходник вместе с копией.
    /// </summary>
    public static void Move(string source, string destination)
    {
        if (PathSafety.IsInsideOrEqual(source, destination))
            throw new InvalidOperationException(
                "Нельзя перенести папку бэкапа внутрь неё самой: " + destination);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (!Directory.Exists(destination))
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException)
            {
                // Кросс-томовый перенос: копируем + удаляем исходное.
            }
        }

        if (!Directory.Exists(destination))
            Directory.CreateDirectory(destination);

        // Не идём сквозь junction/symlink: иначе можно вытащить данные из-за
        // пределов бэкапа.
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var file in Directory.EnumerateFiles(source, "*", opts))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                 })
                 .OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
        try { Directory.Delete(source, recursive: true); } catch { /* уже очищено */ }
    }
}
