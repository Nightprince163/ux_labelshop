using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace UXLabelshop.Services;

/// <summary>一次可用升级：目录里版本号最高的那个 exe。</summary>
public sealed record UpdateRelease(Version Version, string FileName, string Url);

/// <summary>
/// 客户端自升级：服务端只放带版本号的 exe + nginx 目录列表，没有清单文件。
/// 目录列表（HTML 或 autoindex JSON）当纯文本正则扫文件名，取版本号最大的。
/// 只在启动时检查一次；未配置地址或网络不通时全部静默，绝不阻塞开机。
/// </summary>
public class UpdateService
{
    /// <summary>发布文件命名规范：UXLabelshop_v主.次.修订.exe，三段纯数字。</summary>
    public const string FileNamePattern = @"UXLabelshop_v(\d+)\.(\d+)\.(\d+)\.exe";

    private static readonly Regex FileRx = new(
        FileNamePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;

    /// <summary>可注入 HttpClient 供自检用（假 handler，不发真实请求）。</summary>
    public UpdateService(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>当前程序版本，取 csproj &lt;Version&gt; 生成的信息版本；不依赖 exe 文件名。</summary>
    public static Version CurrentVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly();
            var text = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "";
            var plus = text.IndexOf('+');
            if (plus > 0) text = text[..plus];
            return Version.TryParse(text, out var v)
                ? v
                : asm?.GetName().Version ?? new Version(0, 0, 0);
        }
    }

    /// <summary>从 nginx autoindex 输出（HTML / JSON 都行）里挑版本号最高的文件。</summary>
    public static UpdateRelease? ParseLatest(string? listing, string baseUrl)
    {
        if (string.IsNullOrEmpty(listing)) return null;
        var url = baseUrl.Trim();
        if (url.Length == 0) return null;
        if (!url.EndsWith('/')) url += "/";

        UpdateRelease? best = null;
        foreach (Match m in FileRx.Matches(listing))
        {
            var v = new Version(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value));
            if (best != null && v <= best.Version) continue;
            best = new UpdateRelease(v, m.Value, url + m.Value);
        }
        return best;
    }

    /// <summary>检查是否有比本地更新的版本；3 秒超时，异常由调用方吞掉。</summary>
    public async Task<UpdateRelease?> CheckAsync(string baseUrl, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var listing = await _http.GetStringAsync(baseUrl, timeout.Token).ConfigureAwait(false);
        var latest = ParseLatest(listing, baseUrl);
        return latest != null && latest.Version > CurrentVersion ? latest : null;
    }

    /// <summary>
    /// 下载到「当前 exe 同目录 / 当前 exe 文件名 + .new」。
    /// 没有清单文件，用 Content-Length + PE 头(MZ) 校验完整性。
    /// </summary>
    public async Task<string> DownloadAsync(UpdateRelease release,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(App.AppDir, "UXLabelshop.exe");
        var staged = exe + ".new";

        using var resp = await _http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var expected = resp.Content.Headers.ContentLength;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(staged, FileMode.Create, FileAccess.Write,
                         FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                total += n;
                if (expected is > 0) progress?.Report(Math.Min(1, (double)total / expected.Value));
            }
        }

        var ok = (!expected.HasValue || new FileInfo(staged).Length == expected.Value) && HasPeHeader(staged);
        if (!ok)
        {
            try { File.Delete(staged); } catch { /* 清理失败不影响报错 */ }
            throw new IOException("下载内容不完整或不是有效的 exe，已丢弃，请重试");
        }
        return staged;
    }

    private static bool HasPeHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    /// <summary>
    /// 交接换包：运行中的 exe 不直接覆盖。生成一个临时 PowerShell 脚本，
    /// 等旧进程退出后把旧 exe 改名 .old、新 exe 改成正式名、再启动新进程。
    /// exe 在桌面/文档/D 盘等用户可写目录，全程不需要管理员，也不需要第二个 updater 程序。
    /// .old 会一直保留，出问题时改回文件名即可回滚。
    /// </summary>
    public static void ApplyAndRestart(string stagedFile)
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("取不到当前程序路径，无法应用更新");
        var script = Path.Combine(App.AppDir, "_apply_update.ps1");
        File.WriteAllText(script, ApplyScript, new UTF8Encoding(false));

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                     "-File", script,
                     "-ProcId", Environment.ProcessId.ToString(),
                     "-ExePath", exe,
                     "-NewPath", stagedFile,
                 })
            psi.ArgumentList.Add(a);

        Process.Start(psi);
        Application.Current.Shutdown();
    }

    private const string ApplyScript = """
        param([int]$ProcId, [string]$ExePath, [string]$NewPath)
        Wait-Process -Id $ProcId -Timeout 60 -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        $old = "$($ExePath).old"
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
        Move-Item -LiteralPath $ExePath -Destination $old -Force
        Move-Item -LiteralPath $NewPath -Destination $ExePath -Force
        Start-Process -FilePath $ExePath -ArgumentList '--after-update'
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}
