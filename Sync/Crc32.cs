using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SmartUsbBackup.Sync;

/// <summary>
/// Потокобезопасный CRC32 (полином 0xEDB88320, стандарт IEEE, совместим с zip/bzip2).
/// Используется для «глубокой» проверки идентичности файлов при VerifyByCrc = true.
/// Таблица 256 значений = 1 КБ в памяти, подсчёт ~200 МБ/с на чанковых чтениях.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint POLY = 0xEDB88320u; // отражённый полином IEEE
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ POLY : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>CRC32 потока.</summary>
    public static uint Compute(Stream stream, CancellationToken ct = default)
    {
        uint crc = 0xFFFFFFFFu;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc = Update(crc, buffer, read);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        return ~crc;
    }

    public static uint Update(uint crc, byte[] buffer, int count)
    {
        uint c = crc;
        for (int i = 0; i < count; i++)
            c = Table[(c ^ buffer[i]) & 0xFF] ^ (c >> 8);
        return c;
    }

    /// <summary>
    /// Сравнить два файла по CRC32. Читает их чанками по 64 КБ (без аллокаций)
    /// и выходит СРАЗУ при первой несовпадающей странице — очень экономно.
    /// </summary>
    public static async Task<bool> AreEqualAsync(string src, string dst, CancellationToken ct = default)
    {
        try
        {
            var srcFi = new FileInfo(src);
            var dstFi = new FileInfo(dst);
            if (!dstFi.Exists || srcFi.Length != dstFi.Length) return false;
            if (srcFi.Length == 0) return true;   // оба пустые

            // Сравнение чанков «на лету» — не вычисляем полные суммы, а ищем первое различие.
            await using var a = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var b = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var bufA = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            var bufB = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = await a.ReadAsync(bufA.AsMemory(0, bufA.Length), ct)) > 0)
                {
                    int readB = await b.ReadAsync(bufB.AsMemory(0, read), ct);
                    if (readB != read) return false;

                    for (int i = 0; i < read; i++)
                        if (bufA[i] != bufB[i]) return false;
                }
                return b.Read(bufB, 0, 1) == 0; // в конце второй файл должен закончиться тоже
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(bufA);
                System.Buffers.ArrayPool<byte>.Shared.Return(bufB);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"CRC-сравнение не удалось ({ex.Message}); считаю файлы РАЗНЫМИ (безопасная сторона)");
            return false; // перестраховка: перескопировать
        }
    }
}
