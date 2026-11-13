using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SmartUsbBackup.Interop;

namespace SmartUsbBackup.Core;

/// <summary>
/// Всё, что нужно знать о новом релизе на GitHub: версия, ссылка на скачивание,
/// размер ассета, хеш (если предоставлен GitHub).
/// </summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string AssetName, string? Sha256)
{
    /// <summary>Ссылка на страницу релиза на GitHub (html_url из API). Используется для ручного скачивания мажорных версий.</summary>
    public string? HtmlUrl { get; set; }
}

/// <summary>
/// Проверка обновлений раз в сутки + скачивание и запуск инсталлятора.
/// Источник обновлений — GitHub Releases репозитория COSinus205/SmartUsbBackup.
/// </summary>
public static class UpdateService
{
    public const string Repository = "COSinus205/SmartUsbBackup";

    private static readonly HttpClientHandler HttpHandler = new()
    {
        // SEC: A-03 — редиректы проверяем вручную по allowlist.
        AllowAutoRedirect = false,
    };

    private static readonly HttpClient Http = new(HttpHandler)
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    private static readonly string[] AllowedHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    // Ровно один asset-установщик на релиз: SmartUsbBackup-Setup-<semver>.exe
    private static readonly Regex ExpectedAssetRegex =
        new(@"^SmartUsbBackup-Setup-\d+\.\d+\.\d+\.exe$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"SmartUsbBackup/{AppVersion}");
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static Version AppVersion => ExtractVersion(
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    private static Version ExtractVersion(Version v) =>
        new(v.Major >= 0 ? v.Major : 0,
            v.Minor >= 0 ? v.Minor : 0,
            v.Build >= 0 ? v.Build : 0,
            v.Revision >= 0 ? v.Revision : 0);

    /// <summary>
    /// Приводит версию к Major.Minor.Build: «4.3.1» и «4.3.1.0» — одна и та же версия.
    /// Без этого сравнение тега релиза (4.3.1) с AssemblyVersion (4.3.1.0) даёт
    /// ложное «установлена более новая версия».
    /// </summary>
    public static Version Normalize(Version? version)
    {
        if (version is null) return new Version(0, 0, 0);
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }

    /// <summary>Сравнение версий с нормализацией: &lt;0, 0, &gt;0 — как у Version.CompareTo.</summary>
    public static int Compare(Version? left, Version? right)
        => Normalize(left).CompareTo(Normalize(right));

    /// <summary>Удалённая версия новее текущей.</summary>
    public static bool IsNewer(Version? current, Version? remote)
        => Compare(remote, current) > 0;

    /// <summary>Строковый вариант сравнения (теги вида "v4.3.1").</summary>
    public static bool IsNewer(string? current, string? remote)
    {
        if (!TryParseVersion(current, out var currentVersion)) return false;
        if (!TryParseVersion(remote, out var remoteVersion)) return false;
        return IsNewer(currentVersion, remoteVersion);
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        if (Version.TryParse((text ?? string.Empty).Trim().TrimStart('v', 'V'), out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>
    /// Возвращает true только для ожидаемого безопасного имени asset'а.
    /// SEC: A-01
    /// </summary>
    public static bool IsExpectedAssetName(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return false;

        // Базовое имя строго: никаких путей/разделителей/traversal.
        if (Path.GetFileName(assetName) != assetName)
            return false;
        if (assetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        if (assetName.Contains("..", StringComparison.Ordinal))
            return false;
        if (assetName.Contains(":", StringComparison.Ordinal))
            return false;

        // SEC: A-01 — жёсткий шаблон имени.
        return ExpectedAssetRegex.IsMatch(assetName);
    }

    /// <summary>
    /// Разрешает путь скачивания только внутри updatesDir. При любых сомнениях — null.
    /// SEC: A-01
    /// </summary>
    public static string? ResolveTargetPath(string? updatesDir, string? assetName)
    {
        if (string.IsNullOrWhiteSpace(updatesDir) || !IsExpectedAssetName(assetName))
            return null;

        var fullDir = Path.GetFullPath(updatesDir);
        var candidate = Path.Combine(fullDir, assetName!);
        var fullTarget = Path.GetFullPath(candidate);

        var prefix = fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        return fullTarget;
    }

    /// <summary>
    /// Проверяет download URL по allowlist.
    /// SEC: A-03
    /// </summary>
    public static bool IsAllowedDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            AppLog.Warn("SEC A-03: URL пуст/whitespace — отклонено");
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            AppLog.Warn($"SEC A-03: URL не является абсолютным URI — отклонено: {url}");
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn($"SEC A-03: недопустимая схема '{uri.Scheme}' (ожидается https) — отклонено: {url}");
            return false;
        }

        if (uri.IsDefaultPort == false && uri.Port != 443)
        {
            AppLog.Warn($"SEC A-03: недопустимый порт {uri.Port} (ожидается 443) — отклонено: {url}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            AppLog.Warn($"SEC A-03: пустой хост — отклонено: {url}");
            return false;
        }

        bool hostAllowed = false;
        foreach (var h in AllowedHosts)
        {
            if (string.Equals(uri.Host, h, StringComparison.OrdinalIgnoreCase))
            {
                hostAllowed = true;
                break;
            }
        }
        if (!hostAllowed)
        {
            AppLog.Warn($"SEC A-03: хост '{uri.Host}' не в allowlist [{string.Join(", ", AllowedHosts)}] — отклонено: {url}");
            return false;
        }

        if (uri.AbsolutePath.Contains("..", StringComparison.Ordinal))
        {
            AppLog.Warn($"SEC A-03: путь содержит '..' — отклонено: {url}");
            return false;
        }

        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            // Ожидаемый префикс для release-ассетов.
            if (!uri.AbsolutePath.StartsWith("/" + Repository + "/", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn($"SEC A-03: для github.com путь '{uri.AbsolutePath}' не начинается с '/{Repository}/' — отклонено: {url}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Вычисляет и сравнивает SHA-256 файла с ожидаемым hex.
    /// SEC: A-02
    /// </summary>
    public static async Task<bool> VerifySha256Async(string filePath, string? expectedHex, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
            return false;

        string hex = expectedHex.Trim();
        if (hex.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            hex = hex.Substring("sha256:".Length).Trim();

        // HEX без префикса, 64 символа.
        if (hex.Length != 64)
            return false;
        foreach (var c in hex)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }

        try
        {
            using var sha = SHA256.Create();
            await using var stream = File.OpenRead(filePath);
            var hash = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash).Equals(hex, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // SEC: A-02 fail-closed
            AppLog.Error(ex, "SEC A-02: не удалось вычислить SHA-256");
            return false;
        }
    }

    /// <summary>
    /// Возвращает true, если удалённая версия имеет больший мажорный номер.
    /// Invalid/неопознанные версии не блокируют стандартное обновление (false).
    /// </summary>
    public static bool IsMajorUpdate(Version? current, Version? remote)
    {
        if (current is null || remote is null)
            return false;
        return remote.Major > current.Major;
    }

    /// <summary>Строковая обёртка (для непроверенных тегов/CLI и тестов). При невалидном вводе — false, без исключений.</summary>
    public static bool IsMajorUpdate(string? current, string? remote)
    {
        if (!Version.TryParse((current ?? string.Empty).TrimStart('v', 'V'), out var currentVersion))
        {
            AppLog.Warn("SEC A-???: невалидная текущая версия при сравнении мажорных: " + current);
            return false;
        }
        if (!Version.TryParse((remote ?? string.Empty).TrimStart('v', 'V'), out var remoteVersion))
        {
            AppLog.Warn("SEC A-???: невалидная удалённая версия при сравнении мажорных: " + remote);
            return false;
        }
        return IsMajorUpdate(currentVersion, remoteVersion);
    }

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

            string? htmlUrl = null;
            if (root.TryGetProperty("html_url", out var htmlUrlProp))
                htmlUrl = htmlUrlProp.GetString();

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                return null;

            string? url = null;
            long size = 0;
            string assetName = "";
            string? sha256 = null;

            string? sidecarUrl = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (!IsExpectedAssetName(name))
                        continue;

                    // Если имя содержит версию — она должна совпадать с тегом релиза
                    // (сравниваем нормализованно: 4.3.1 == 4.3.1.0).
                    var m = Regex.Match(name, @"SmartUsbBackup-Setup-(\d+\.\d+\.\d+)\.exe", RegexOptions.CultureInvariant);
                    if (m.Success && Version.TryParse(m.Groups[1].Value, out var assetVersion) &&
                        Compare(assetVersion, version) != 0)
                        continue;

                    if (!a.TryGetProperty("browser_download_url", out var urlProp))
                        continue;
                    var candidateUrl = urlProp.GetString();
                    if (!IsAllowedDownloadUrl(candidateUrl))
                        continue;

                    url = candidateUrl;
                    size = a.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                    assetName = name;

                    if (a.TryGetProperty("digest", out var digestProp))
                    {
                        var digest = digestProp.GetString();
                        if (!string.IsNullOrWhiteSpace(digest))
                            sha256 = digest;
                    }
                    break;
                }

                // Попытка взять хеш из sidecar-ассета *.sha256 (если digest отсутствует).
                if (url is not null && sha256 is null && IsExpectedAssetName(assetName))
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.GetProperty("name").GetString() ?? "";
                        if (!name.Equals(assetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!a.TryGetProperty("browser_download_url", out var sideUrlProp))
                            continue;
                        var sideUrl = sideUrlProp.GetString();
                        if (!IsAllowedDownloadUrl(sideUrl))
                            continue;
                        sidecarUrl = sideUrl;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(assetName))
                return null;

            // Попытка скачать sidecar-хеш (best effort).
            if (sha256 is null && sidecarUrl is not null)
            {
                try
                {
                    var txt = await Http.GetStringAsync(sidecarUrl, ct);
                    var parsed = ParseSha256(txt);
                    if (parsed is not null)
                        sha256 = parsed;
                }
                catch (Exception)
                {
                    // SEC: A-02 — отсутствие хеша не скрываем; оставляем null (fail-closed в DownloadAsync).
                }
            }

            // SEC: A-02 — без хеша автообновление недоступно (UI сообщит пользователю).
            if (string.IsNullOrWhiteSpace(sha256))
            {
                AppLog.Warn("SEC A-02: в ответе API отсутствует SHA-256 для asset'а " + assetName + " — автообновление недоступно");
            }

            return new UpdateInfo(version, tag, url!, size, assetName, sha256) { HtmlUrl = htmlUrl };
        }
        catch (Exception)
        {
            // Нет сети / нет ключа JSON / DNS — не тревожим UI.
            return null;
        }
    }

    private static string? ParseSha256(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.Trim();
        // Строка вида "sha256:HEX" или "HEX  filename" или просто HEX.
        var parts = t.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var candidate = p;
            if (candidate.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                candidate = candidate.Substring("sha256:".Length);
            if (candidate.Length == 64 && candidate.All(c =>
                    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return candidate;
            }
        }
        return null;
    }

    public static async Task<string?> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!IsExpectedAssetName(info.AssetName))
        {
            AppLog.Warn("SEC A-01: отклонено имя asset'а: " + info.AssetName);
            return null;
        }
        if (!IsAllowedDownloadUrl(info.DownloadUrl))
        {
            AppLog.Warn("SEC A-03: отклонён URL загрузки: " + info.DownloadUrl);
            return null;
        }
        // SEC: A-02 — обновление без SHA-256 не принимаем: целостность
        // подтверждается только хешем, обхода «на свой риск» больше нет.
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            AppLog.Warn("SEC A-02: обновление без SHA-256 — отказ");
            return null;
        }

        var dir = Path.Combine(SettingsStore.Dir, "updates");
        try { Directory.CreateDirectory(dir); }
        catch { return null; }

        var target = ResolveTargetPath(dir, info.AssetName);
        if (target is null)
        {
            AppLog.Warn("SEC A-01: путь скачивания вне папки обновлений");
            return null;
        }

        // SEC: A-04 — больше не переиспользуем файл только по размеру.
        var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var resp = await SendWithRedirectsAsync(info.DownloadUrl, ct);
            if (resp is null || !resp.IsSuccessStatusCode)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }

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

            // SEC: A-02 — SHA-256 обязателен (проверен выше).
            var shaOk = await VerifySha256Async(tmp, info.Sha256!, ct);
            if (!shaOk)
            {
                AppLog.Error(null, "SEC A-02: SHA-256 не совпал — файл удалён");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return null;
            }

            File.Move(tmp, target, overwrite: true);
            return target;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return null;
        }
    }

    private static async Task<HttpResponseMessage?> SendWithRedirectsAsync(string startUrl, CancellationToken ct)
    {
        var current = startUrl;
        for (int i = 0; i < 3; i++)
        {
            if (!IsAllowedDownloadUrl(current))
            {
                AppLog.Warn("SEC A-03: редирект/URL не в allowlist: " + current);
                return null;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)resp.StatusCode;
            if (status >= 300 && status < 400)
            {
                var location = resp.Headers.Location;
                resp.Dispose();
                if (location is null)
                    return null;

                string next = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(current), location).ToString();

                if (!IsAllowedDownloadUrl(next))
                {
                    AppLog.Warn("SEC A-03: редирект на недопустимый URL: " + next);
                    return null;
                }
                current = next;
                continue;
            }
            return resp;
        }
        AppLog.Warn("SEC A-03: превышен лимит редиректов");
        return null;
    }

    public static bool VerifyAuthenticode(string filePath) => WinTrust.Verify(filePath);

    /// <summary>
    /// Внутренняя механика запуска установщика (через cmd с задержкой).
    /// Используется и в продакшен-пути (после WinTrust), и во временных тестах.
    /// <paramref name="restartHidden"/> — перезапустить приложение скрыто (как оно
    /// работало в автозагрузке), иначе покажется окно.
    /// </summary>
    public static bool ApplyUpdateCore(string installerPath, bool restartHidden = false)
    {
        try
        {
            // После silent-установки Inno не запускает приложение ([Run] skipifsilent),
            // поэтому перезапуск делаем сами — уже после завершения установщика.
            string restart = string.Empty;
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    restart = " & start \"\" \"" + exe + "\"" + (restartHidden ? " -hidden" : string.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Не удалось определить путь к приложению для перезапуска: " + ex.Message);
            }

            // Установщик запускается отдельным cmd с задержкой ~2 с, чтобы дать
            // приложению завершить штатную очистку и освободить файлы до копирования.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout 2 >nul & \"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART{restart}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Не удалось запустить установщик");
            return false;
        }
    }

    /// <summary>
    /// Запускает скачанный установщик в detached-процессе (через cmd с задержкой),
    /// чтобы инсталлятор не зависел от времени жизни текущего процесса.
    /// Возвращает true, если процесс установщика удалось запустить.
    /// </summary>
    public static bool ApplyUpdate(string installerPath, bool restartHidden = false)
    {
        // SEC: A-02 — проверка Authenticode перед запуском.
        if (!WinTrust.Verify(installerPath))
        {
            AppLog.Warn("SEC A-02: ApplyUpdate отменён — подпись недействительна: " + installerPath);
            return false;
        }

        return ApplyUpdateCore(installerPath, restartHidden);
    }
}