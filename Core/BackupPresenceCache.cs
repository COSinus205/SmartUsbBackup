using System;
using System.Collections.Generic;
using System.IO;

namespace SmartUsbBackup.Core;

/// <summary>
/// Кэш ответа на вопрос «в папке бэкапа есть хотя бы один файл/подпапка» и
/// «папка вообще существует».
///
/// Зачем: <c>RefreshList</c> вызывается на каждое событие состояния (старт/стоп
/// синхронизации, подключение/отключение флешки, прогресс) и раньше на каждый
/// вызов делал по 3–4 обращения к диску на каждый профиль — на UI-потоке.
/// Для сетевой папки это могло давать заметные подтормаживания интерфейса.
///
/// Теперь результат живёт <see cref="DefaultMaxAge"/> и переиспользуется, а там,
/// где мы точно знаем, что содержимое изменилось (завершение синхронизации,
/// удаление бэкапа, смена папки хранения), кэш инвалидируется явно — то есть
/// задержка обновления не «магическая», а нулевая.
/// </summary>
internal sealed class BackupPresenceCache
{
    /// <summary>Сколько живёт результат проверки, если его не инвалидировали явно.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(2);

    /// <summary>Защита от неограниченного роста (профилей обычно единицы).</summary>
    private const int MaxEntries = 64;

    private readonly record struct Entry(bool Exists, bool HasContent, long CheckedAtTicks);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeSpan _maxAge;
    private readonly Func<long> _clock;

    public BackupPresenceCache(TimeSpan? maxAge = null, Func<long>? clock = null)
    {
        _maxAge = maxAge ?? DefaultMaxAge;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>true, если папка существует и в ней есть хотя бы одна запись.</summary>
    public bool HasContent(string path) => Get(path).HasContent;

    /// <summary>true, если папка существует (даже пустая).</summary>
    public bool Exists(string path) => Get(path).Exists;

    /// <summary>Забыть про конкретную папку (после удаления/переноса бэкапа).</summary>
    public void Invalidate(string path)
    {
        lock (_gate) _entries.Remove(path);
    }

    /// <summary>Забыть всё (например, сменилась базовая папка хранения).</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    private Entry Get(string path)
    {
        long now = _clock();

        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var cached) && now - cached.CheckedAtTicks < _maxAge.TotalMilliseconds)
                return cached;

            var fresh = Probe(path, now);

            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(path))
                EvictOldestLocked();

            _entries[path] = fresh;
            return fresh;
        }
    }

    private static Entry Probe(string path, long now)
    {
        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                RecurseSubdirectories = false,
            };

            // Один обход каталога вместо Directory.Exists + Enumerate...Any():
            // если запись нашлась — папка существует, второй syscall не нужен.
            using var entries = Directory.EnumerateFileSystemEntries(path, "*", opts).GetEnumerator();
            if (entries.MoveNext())
                return new Entry(true, true, now);

            // Пустая папка: отличаем «пусто» от «папки нет».
            return new Entry(Directory.Exists(path), false, now);
        }
        catch
        {
            return new Entry(false, false, now);
        }
    }

    private void EvictOldestLocked()
    {
        string? oldestKey = null;
        long oldestStamp = long.MaxValue;

        foreach (var pair in _entries)
        {
            if (pair.Value.CheckedAtTicks >= oldestStamp) continue;
            oldestStamp = pair.Value.CheckedAtTicks;
            oldestKey = pair.Key;
        }

        if (oldestKey is not null)
            _entries.Remove(oldestKey);
    }
}
