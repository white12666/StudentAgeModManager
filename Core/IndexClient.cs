using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StudentAgeModManager.Core
{
    /// <summary>拉取并解析中央索引 mods.json；成功后把索引里的镜像列表回写给 Downloader。</summary>
    public class IndexClient
    {
        /// <summary>中央索引 raw 地址（发布前改成实际仓库）。</summary>
        public const string DefaultIndexUrl =
            "https://raw.githubusercontent.com/white12666/StudentAgeModManager/main/mods.json";

        private readonly Downloader _downloader;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public IndexClient(Downloader downloader)
        {
            _downloader = downloader;
        }

        public async Task<ModIndex> FetchAsync(string indexUrl = null, CancellationToken ct = default(CancellationToken))
        {
            var url = indexUrl ?? OverrideIndexUrl() ?? DefaultIndexUrl;
            var text = await _downloader.DownloadStringAsync(url, ct);
            text = text.TrimStart('\uFEFF'); // 容错：索引文件带 UTF-8 BOM 时去掉
            var index = Json.Deserialize<ModIndex>(text);
            if (index == null || index.mods == null)
                throw new InvalidDataException("索引文件格式不正确");
            if (index.mirrors != null && index.mirrors.Count > 0)
                _downloader.Mirrors = index.mirrors; // 用远端清单刷新镜像列表
            return index;
        }

        /// <summary>
        /// 本地调试：exe 同目录放 index_url.txt 可覆盖索引地址（支持 file:/// 或本地路径）。
        /// </summary>
        private static string OverrideIndexUrl()
        {
            try
            {
                var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index_url.txt");
                if (File.Exists(p))
                {
                    var line = File.ReadAllText(p).Trim();
                    if (line.Length > 0)
                    {
                        if (File.Exists(line)) return new Uri(Path.GetFullPath(line)).AbsoluteUri;
                        return line;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
