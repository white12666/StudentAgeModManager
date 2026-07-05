using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StudentAgeModManager.Core
{
    public enum MirrorMode
    {
        Auto,        // 先直连，失败走镜像
        MirrorOnly,  // 强制镜像
        DirectOnly,  // 强制直连
    }

    /// <summary>
    /// 带镜像回退的下载器。镜像用法：镜像前缀 + 原始完整 URL，
    /// 如 https://ghproxy.net/https://github.com/owner/repo/releases/download/...
    /// </summary>
    public class Downloader
    {
        /// <summary>内置种子镜像（首次拉索引前可用；索引拉到后用索引里的列表覆盖）。</summary>
        public static readonly string[] SeedMirrors =
        {
            "https://ghproxy.net/",
            "https://gh-proxy.com/",
            "https://ghfast.top/",
        };

        public List<string> Mirrors { get; set; } = new List<string>(SeedMirrors);
        public MirrorMode Mode { get; set; } = MirrorMode.Auto;
        public int TimeoutSeconds { get; set; } = 15;

        static Downloader()
        {
            // 老系统 GitHub TLS 兼容
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private IEnumerable<string> Candidates(string url)
        {
            if (Mode != MirrorMode.MirrorOnly) yield return url;
            if (Mode != MirrorMode.DirectOnly)
                foreach (var m in Mirrors)
                    yield return m.TrimEnd('/') + "/" + url;
        }

        /// <summary>下载文本（用于 mods.json）。全部候选失败抛出最后一个异常。</summary>
        public async Task<string> DownloadStringAsync(string url, CancellationToken ct = default(CancellationToken))
        {
            Exception last = null;
            foreach (var candidate in Candidates(url))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var wc = CreateClient())
                    using (ct.Register(wc.CancelAsync))
                    {
                        var task = wc.DownloadStringTaskAsync(candidate);
                        if (await Task.WhenAny(task, Task.Delay(TimeoutSeconds * 1000, ct)) != task)
                        {
                            wc.CancelAsync();
                            throw new TimeoutException("请求超时: " + candidate);
                        }
                        return await task;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    last = ex;
                }
            }
            throw last ?? new Exception("无可用下载源");
        }

        /// <summary>下载文件到临时路径，返回临时文件路径。progress: (0~100, 当前源)。</summary>
        public async Task<string> DownloadFileAsync(string url, Action<int, string> progress,
            CancellationToken ct = default(CancellationToken))
        {
            Exception last = null;
            foreach (var candidate in Candidates(url))
            {
                ct.ThrowIfCancellationRequested();
                var temp = Path.Combine(Path.GetTempPath(),
                    "SAMM_" + Guid.NewGuid().ToString("N") + Path.GetExtension(new Uri(url).AbsolutePath));
                try
                {
                    using (var wc = CreateClient())
                    using (ct.Register(wc.CancelAsync))
                    {
                        string sourceLabel = ShortHost(candidate);
                        if (progress != null)
                            wc.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage, sourceLabel);

                        var task = wc.DownloadFileTaskAsync(candidate, temp);
                        // 大文件下载：只要有进度就不算超时 → 用首字节超时策略简化为整体 10 分钟上限
                        if (await Task.WhenAny(task, Task.Delay(10 * 60 * 1000, ct)) != task)
                        {
                            wc.CancelAsync();
                            throw new TimeoutException("下载超时: " + candidate);
                        }
                        await task;
                    }
                    var fi = new FileInfo(temp);
                    if (!fi.Exists || fi.Length == 0) throw new IOException("下载内容为空");
                    return temp;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    last = ex;
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            throw last ?? new Exception("无可用下载源");
        }

        private static WebClient CreateClient()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "StudentAgeModManager/1.0";
            wc.Encoding = System.Text.Encoding.UTF8; // 默认是 ANSI(GBK)，会把 UTF-8 的 mods.json 解成乱码
            return wc;
        }

        private static string ShortHost(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }
    }
}
