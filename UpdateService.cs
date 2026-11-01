using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ExplorerDocks
{
    internal static class UpdateService
    {
        private const long MaxDownloadBytes = 512L * 1024 * 1024;
        private static readonly string Repository = typeof(UpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepository")?.Value?.Trim() ?? "";

        public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 3);

        public static bool IsConfigured => Regex.IsMatch(Repository,
            @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

        internal sealed record Release(Version Version, string Name, Uri DownloadUrl, string Sha256);
        internal sealed record Prepared(string JobDirectory, string Mode, string Payload, string Executable);

        private static string? InstallMode
        {
            get
            {
                var dir = AppContext.BaseDirectory;
                if (File.Exists(Path.Combine(dir, "explorerdocks-installed.marker"))) return "Installer";
                if (File.Exists(Path.Combine(dir, "explorerdocks-portable.marker"))) return "Portable";
                return null; // dotnet run や開発フォルダーを更新しない
            }
        }

        public static async Task<Release?> FindNewReleaseAsync()
        {
            if (!IsConfigured) return null;
            using var client = CreateClient(TimeSpan.FromSeconds(25));
            var api = $"https://api.github.com/repos/{Repository}/releases/latest";
            using var response = await client.GetAsync(api);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var release = json.RootElement;
            var tag = release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest))
                throw new InvalidDataException("GitHub Release のバージョンを解析できません。");
            var current = CurrentVersion;
            if (latest <= current) return null;

            string suffix = InstallMode == "Installer" ? "_win-x64_Setup.exe" : "_win-x64_Portable.zip";
            string required = $"ExplorerDocks_v{latest.ToString(3)}{suffix}";
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), required,
                    StringComparison.OrdinalIgnoreCase)) continue;
                string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$"))
                    throw new InvalidDataException("更新ファイルのSHA-256がGitHubにありません。");
                string link = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                    !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("GitHub以外のダウンロード先は使用できません。");
                return new Release(latest, required, uri, digest[7..].ToLowerInvariant());
            }
            throw new InvalidDataException($"更新用ファイル {required} がReleaseにありません。");
        }

        public static async Task<Prepared?> PrepareAsync(Release release)
        {
            string? mode = InstallMode;
            if (mode is null) return null;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExplorerDocks", "updates");
            string job = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            try
            {
                string archivePath = Path.Combine(job, release.Name);
                // 100MB級の配布物でも回線速度によって失敗しないよう、更新確認より長い制限時間を使う。
                using (var client = CreateClient(TimeSpan.FromMinutes(10)))
                using (var response = await client.GetAsync(release.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                        throw new InvalidDataException("更新ファイルが大きすぎます。");
                    await using var input = await response.Content.ReadAsStreamAsync();
                    await using var output = new FileStream(archivePath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 65536, useAsync: true);
                    byte[] buffer = new byte[65536];
                    long count = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) != 0)
                    {
                        count += read;
                        if (count > MaxDownloadBytes) throw new InvalidDataException("更新ファイルが大きすぎます。");
                        await output.WriteAsync(buffer.AsMemory(0, read));
                    }
                }
                await using (var verify = File.OpenRead(archivePath))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify));
                    if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新ファイルのSHA-256が一致しません。");
                }

                string payload = archivePath;
                if (mode == "Portable")
                {
                    payload = Path.Combine(job, "ExplorerDocks.exe");
                    using var zip = ZipFile.OpenRead(archivePath);
                    var entry = zip.GetEntry("ExplorerDocks.exe");
                    if (entry is null || entry.Length < 1024 || entry.Length > MaxDownloadBytes)
                        throw new InvalidDataException("Portable ZIPに実行ファイルがありません。");
                    entry.ExtractToFile(payload);
                    using var file = File.OpenRead(payload);
                    if (file.ReadByte() != 'M' || file.ReadByte() != 'Z')
                        throw new InvalidDataException("Portable ZIP内の実行ファイルが不正です。");
                }

                using var resource = typeof(UpdateService).Assembly
                    .GetManifestResourceStream("ExplorerDocks.ApplyUpdate")
                    ?? throw new InvalidDataException("更新スクリプトがありません。");
                using (var dest = File.Create(Path.Combine(job, "apply_update.ps1")))
                    resource.CopyTo(dest);
                return new Prepared(job, mode, payload, Environment.ProcessPath ??
                    Path.Combine(AppContext.BaseDirectory, "ExplorerDocks.exe"));
            }
            catch
            {
                try { Directory.Delete(job, recursive: true); } catch { }
                throw;
            }
        }

        public static bool LaunchApply(Prepared prepared)
        {
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", Path.Combine(prepared.JobDirectory, "apply_update.ps1"),
                "-ParentPid", Environment.ProcessId.ToString(),
                "-Mode", prepared.Mode,
                "-Payload", prepared.Payload,
                "-TargetExe", prepared.Executable,
                "-JobDir", prepared.JobDirectory
            }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            return process is not null;
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ExplorerDocks/{CurrentVersion.ToString(3)}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }
    }
}
