using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartUsbBackup.Core;

/// <summary>
/// Всё, что нужно знать о новом релизе на GitHub: версия, ссылка на скачивание,
/// размер ассета. Данные приходят из GitHub Releases API.
/// </summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string AssetName);

/// <summary>
/// Проверка обновлений раз в сутки + скачивание и запуск инсталлятора.
/// Источник обновлений — GitHub Releases репозитория COSinus205/SmartUsbBackup,
/// куда при релизе загружен SmartUsbBackup-Setup-X.Y.Z.exe.
/// </summary>
public static class UpdateService
{
    public const string Repository = "COSinus205/SmartUsbBackup";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"SmartUsbBackup/{AppVersion}");
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Текущая версия приложения (берётся из сборки).</summary>
    public static Version AppVersion => ExtractVersion(
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    private static Version ExtractVersion(Version v) =>
        new(v.Major >= 0 ? v.Major : 0,
            v.Minor >= 0 ? v.Minor : 0,
            v.Build >= 0 ? v.Build : 0,
            v.Revision >= 0 ? v.Revision : 0);

    /// <summary>
    /// Посмотреть последний релиз. Возвращает null, если сеть недоступна или у grey-white
    /// версий nothing newer (или если release нет ассетов).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repository}/releases/latest");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                return null;

            // Ищем установщик-ассет: "SmartUsbBackup-Setup-X.Y.Z.exe".
            string? url = null;
            long size = 0;
            string assetName = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith("SmartUsbBackup-Setup-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    url = a.GetProperty("browser_download_url").GetString();
                    size = a.GetProperty("size").GetInt64();
                    assetName = name;
                    break;
                }
            }
            if (string.IsNullOrEmpty(url)) return null;

            return new UpdateInfo(version, tag, url!, size, assetName);
        }
        catch (Exception)
        {
            // Нет сети / нет ключа JSON / DNS — не тревожим UI.
            return null;
        }
    }

    /// <summary>
    /// Скачать файл установщика во временное место. Возвращает локальный путь.
    /// Если файл уже скачан раньше и совпадает по размеру — повторно не качаем.
    /// </summary>
    public static async Task<string?> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(SettingsStore.Dir, "updates");
        try { Directory.CreateDirectory(dir); }
        catch { return null; }

        var target = Path.Combine(dir, info.AssetName);
        var tmp = target + ".tmp";

        // Если нужный файл уже лежит с правильным размером — переиспользуем.
        if (File.Exists(target))
        {
            try
            {
                var fi = new FileInfo(target);
                if (fi.Length == info.Size) return target;
                File.Delete(target);
            }
            catch { }
        }

        try
        {
            using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);

            var buffer = new byte[64 * 1024];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
            await dst.FlushAsync(ct);
            dst.Close();

            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            return target;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Запустить инсталлятор (тихо) и выйти из приложения, чтобы он смог
    /// перелинковаться с {app}-папкой. Инсталлятор уже умеет закрывать
    /// запущенное приложение (CloseApplications=yes в .iss).
    /// </summary>
    public static void ApplyUpdate(string installerPath)
    {
        try
        {
            var psi = new ProcessStartInfo(installerPath)
            {
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
                Verb = "open",
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Не удалось запустить установщик: {ex}");
            return;
        }

        // Даём инсталлятору увидеть мутекс приложения и освободить файлы.
        Task.Delay(1500).ContinueWith(_ => Environment.Exit(0));
    }
}
