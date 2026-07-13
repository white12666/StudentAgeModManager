using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StudentAgeModManager.Core
{
    /// <summary>拉取并解析中央索引 mods.json；成功后把索引里的镜像列表回写给 Downloader。</summary>
    public class IndexClient
    {
        /// <summary>
        /// test 渠道固定读取 test 分支索引；合并并发布到稳定渠道时应与代码一起切回 main。
        /// </summary>
        public const string DefaultIndexUrl =
            "https://raw.githubusercontent.com/white12666/StudentAgeModManager/test/mods.json";

        private readonly Downloader _downloader;
        private readonly WorkshopMetadataService _workshopMetadata;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public IndexClient(Downloader downloader,
            WorkshopMetadataService workshopMetadata = null)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _workshopMetadata = workshopMetadata;
        }

        public async Task<ModIndex> FetchAsync(string indexUrl = null, CancellationToken ct = default(CancellationToken))
        {
            var url = indexUrl ?? OverrideIndexUrl() ?? DefaultIndexUrl;
            var text = await _downloader.DownloadStringAsync(url, ct);
            var index = ParseAndValidate(text);
            if (_workshopMetadata != null)
                await _workshopMetadata.EnrichMissingAsync(index, ct);
            if (index.mirrors != null && index.mirrors.Count > 0)
                _downloader.Mirrors = index.mirrors; // 用远端清单刷新镜像列表
            return index;
        }

        public static ModIndex ParseAndValidate(string text)
        {
            if (text == null) throw new InvalidDataException("索引内容为空。");
            text = text.TrimStart('\uFEFF'); // 容错：索引文件带 UTF-8 BOM 时去掉

            object rawIndex;
            try
            {
                rawIndex = Json.DeserializeObject(text);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("索引 JSON 无法解析: " + ex.Message, ex);
            }

            ValidateRawEntryTypes(rawIndex);

            ModIndex index;
            try
            {
                index = Json.ConvertToType<ModIndex>(rawIndex);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("索引 JSON 格式不正确: " + ex.Message, ex);
            }

            ValidateAndNormalize(index);
            return index;
        }

        private static void ValidateRawEntryTypes(object rawIndex)
        {
            var root = rawIndex as Dictionary<string, object>;
            if (root == null) return;

            object rawMods;
            if (!TryGetProperty(root, "mods", "索引中的 mods", out rawMods)) return;
            var mods = rawMods as object[];
            if (mods == null) return;

            for (int i = 0; i < mods.Length; i++)
            {
                var entry = mods[i] as Dictionary<string, object>;
                if (entry == null) continue;
                ValidateStringProperty(entry, "id", "mods[" + i + "] 的 id");
                ValidateStringProperty(entry, "name", "mods[" + i + "] 的 name");
                ValidateStringProperty(entry, "description", "mods[" + i + "] 的 description");
                ValidateStringProperty(entry, "workshopId",
                    "mods[" + i + "] 的 workshopId");
            }
        }

        private static void ValidateStringProperty(Dictionary<string, object> entry,
            string propertyName, string location)
        {
            object value;
            if (TryGetProperty(entry, propertyName, location, out value) && value != null &&
                !(value is string))
                throw new InvalidDataException(location + " 必须是 JSON 字符串或 null。");
        }

        private static bool TryGetProperty(Dictionary<string, object> values,
            string propertyName, string location, out object value)
        {
            value = null;
            bool found = false;
            foreach (KeyValuePair<string, object> pair in values)
            {
                if (!string.Equals(pair.Key, propertyName,
                    StringComparison.OrdinalIgnoreCase))
                    continue;
                if (found)
                    throw new InvalidDataException(location +
                        " 不能包含仅大小写不同的重复字段。");
                found = true;
                value = pair.Value;
            }
            return found;
        }

        private static void ValidateAndNormalize(ModIndex index)
        {
            if (index == null)
                throw new InvalidDataException("索引文件不是有效对象。");
            if (index.schemaVersion != 1)
                throw new InvalidDataException("索引 schemaVersion 必须为 1。");
            if (index.mods == null)
                throw new InvalidDataException("索引缺少 mods 数组。");

            var modIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var workshopIds = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < index.mods.Count; i++)
            {
                ModEntry entry = index.mods[i];
                string location = "mods[" + i + "]";
                if (entry == null)
                    throw new InvalidDataException(location + " 不能为 null。");

                if (string.IsNullOrWhiteSpace(entry.id))
                    throw new InvalidDataException(location + " 缺少非空 id。");
                if (entry.id.Length > 128)
                    throw new InvalidDataException(location + " 的 id 超过 128 字符限制。");
                if (!string.Equals(entry.id, entry.id.Trim(), StringComparison.Ordinal))
                    throw new InvalidDataException(location + " 的 id 不能包含首尾空白。");
                foreach (char c in entry.id)
                    if (char.IsControl(c))
                        throw new InvalidDataException(location + " 的 id 不能包含控制字符。");

                string firstLocation;
                if (modIds.TryGetValue(entry.id, out firstLocation))
                    throw new InvalidDataException(location + " 的 Mod ID “" + entry.id +
                        "”与 " + firstLocation + " 重复（忽略大小写）。");
                modIds.Add(entry.id, location);

                if (!WorkshopItem.IsDeclared(entry))
                    throw new InvalidDataException(location + "（" + entry.id +
                        "）缺少 workshopId；索引不再支持直接下载 DLL。");

                string normalizedWorkshopId;
                string workshopError;
                if (!WorkshopItem.TryNormalizeReference(entry.workshopId,
                    out normalizedWorkshopId, out workshopError))
                    throw new InvalidDataException(location + "（" + entry.id +
                        "）的 workshopId 无效: " + workshopError);

                string firstWorkshopEntry;
                if (workshopIds.TryGetValue(normalizedWorkshopId, out firstWorkshopEntry))
                    throw new InvalidDataException(location + "（" + entry.id +
                        "）与 " + firstWorkshopEntry + " 使用了相同的 Workshop ID " +
                        normalizedWorkshopId + "。");
                workshopIds.Add(normalizedWorkshopId, location + "（" + entry.id + "）");

                // Downstream code only handles canonical numeric IDs and always constructs
                // its own trusted Steam URL; the index-provided URL is never opened directly.
                entry.workshopId = normalizedWorkshopId;
            }
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
