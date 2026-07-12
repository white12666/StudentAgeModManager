using System;
using System.Collections.Generic;
using System.Globalization;

namespace StudentAgeModManager.Core
{
    /// <summary>中央索引 mods.json 的数据模型（JavaScriptSerializer 反序列化目标）。</summary>
    public class ModIndex
    {
        public int schemaVersion { get; set; }
        public string updatedAt { get; set; }
        public List<string> mirrors { get; set; }
        public ManagerInfo manager { get; set; }
        public BepInExInfo bepinex { get; set; }
        public List<ModEntry> mods { get; set; }
    }

    public class ManagerInfo
    {
        public string version { get; set; }
        public string downloadUrl { get; set; }
    }

    public class BepInExInfo
    {
        public string version { get; set; }
        public string downloadUrl { get; set; }
    }

    public class ModEntry
    {
        public string id { get; set; }
        public string name { get; set; }
        public string description { get; set; }
        /// <summary>owner/repo，用于「主页」按钮。</summary>
        public string repo { get; set; }
        public string version { get; set; }
        public string downloadUrl { get; set; }
        /// <summary>"dll" 单文件放入 installDir；"zip" 解压到 installDir。</summary>
        public string assetType { get; set; }
        /// <summary>相对游戏根目录，如 BepInEx/plugins/StudentAgeEditorPlus。</summary>
        public string installDir { get; set; }
        public string workshopId { get; set; }
    }

    /// <summary>创意工坊条目的统一识别与安全 URL 构造。</summary>
    public static class WorkshopItem
    {
        private const int MaxReferenceLength = 2048;
        private const string SteamCommunityHost = "steamcommunity.com";

        // Empty/null keeps compatibility with legacy direct-download entries. Any
        // other value, including whitespace or malformed text, is a declared workshop
        // item and must never fall back to downloading executable content directly.
        public static bool IsDeclared(ModEntry entry)
        {
            return entry != null && !string.IsNullOrEmpty(entry.workshopId);
        }

        public static bool TryGetId(ModEntry entry, out string normalizedId)
        {
            normalizedId = null;
            if (entry == null) return false;
            string error;
            return TryNormalizeReference(entry.workshopId,
                out normalizedId, out error);
        }

        internal static bool TryNormalizeReference(string reference, out string normalizedId,
            out string error)
        {
            normalizedId = null;
            error = null;
            if (string.IsNullOrEmpty(reference))
            {
                error = "工坊 ID 或链接为空。";
                return false;
            }

            if (reference.Length > MaxReferenceLength)
            {
                error = "工坊 ID 或链接超过 2048 字符限制。";
                return false;
            }

            string value = reference.Trim();
            if (value.Length == 0)
            {
                error = "工坊 ID 或链接不能只包含空白。";
                return false;
            }

            if (ContainsOnlyAsciiDigits(value))
                return TryNormalizeNumericId(value, out normalizedId, out error);

            foreach (char c in value)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    error = "工坊链接不能包含未编码的空白或控制字符。";
                    return false;
                }
            }

            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                error = "既不是纯数字 ID，也不是有效的绝对链接。";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, SteamCommunityHost,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.IdnHost, SteamCommunityHost,
                    StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            {
                error = "链接必须使用官方 https://steamcommunity.com 地址。";
                return false;
            }

            if (uri.Fragment.Length != 0)
            {
                error = "工坊链接不能包含片段标识（#...）。";
                return false;
            }

            string rawPath;
            string rawQuery;
            if (!TryReadRawPathAndQuery(value, out rawPath, out rawQuery, out error))
                return false;

            bool validPath = string.Equals(rawPath, "/sharedfiles/filedetails",
                                 StringComparison.Ordinal) ||
                             string.Equals(rawPath, "/sharedfiles/filedetails/",
                                 StringComparison.Ordinal) ||
                             string.Equals(rawPath, "/workshop/filedetails",
                                 StringComparison.Ordinal) ||
                             string.Equals(rawPath, "/workshop/filedetails/",
                                 StringComparison.Ordinal);
            if (!validPath)
            {
                error = "链接路径必须是 Steam sharedfiles/workshop 的 filedetails 页面。";
                return false;
            }

            string rawId;
            if (!TryReadSingleQueryId(rawQuery, out rawId, out error))
                return false;
            return TryNormalizeNumericId(rawId, out normalizedId, out error);
        }

        private static bool TryReadRawPathAndQuery(string value, out string rawPath,
            out string rawQuery, out string error)
        {
            rawPath = null;
            rawQuery = null;
            error = null;

            int schemeSeparator = value.IndexOf(':');
            if (schemeSeparator <= 0 || schemeSeparator + 2 >= value.Length ||
                value[schemeSeparator + 1] != '/' || value[schemeSeparator + 2] != '/')
            {
                error = "工坊链接必须使用标准的 https:// 形式。";
                return false;
            }

            int authorityStart = schemeSeparator + 3;
            int queryStart = value.IndexOf('?', authorityStart);
            int fragmentStart = value.IndexOf('#', authorityStart);
            int pathEnd = value.Length;
            if (queryStart >= 0 && queryStart < pathEnd) pathEnd = queryStart;
            if (fragmentStart >= 0 && fragmentStart < pathEnd) pathEnd = fragmentStart;

            int slashStart = value.IndexOfAny(new[] { '/', '\\' }, authorityStart);
            rawPath = slashStart >= 0 && slashStart < pathEnd
                ? value.Substring(slashStart, pathEnd - slashStart)
                : string.Empty;

            if (queryStart < 0 || (fragmentStart >= 0 && queryStart > fragmentStart))
            {
                rawQuery = string.Empty;
                return true;
            }

            int queryEnd = fragmentStart >= 0 ? fragmentStart : value.Length;
            rawQuery = value.Substring(queryStart + 1, queryEnd - queryStart - 1);
            return true;
        }

        private static bool TryReadSingleQueryId(string query, out string rawId,
            out string error)
        {
            rawId = null;
            error = null;
            int idCount = 0;
            foreach (string pair in (query ?? string.Empty).Split('&'))
            {
                if (pair.Length == 0) continue;
                int separator = pair.IndexOf('=');
                string encodedName = separator < 0 ? pair : pair.Substring(0, separator);
                string encodedValue = separator < 0 ? string.Empty : pair.Substring(separator + 1);
                string name;
                string value;
                if (!HasValidPercentEscapes(encodedName) ||
                    !HasValidPercentEscapes(encodedValue))
                {
                    error = "工坊链接查询参数包含无效转义。";
                    return false;
                }
                try
                {
                    name = Uri.UnescapeDataString(encodedName);
                    value = Uri.UnescapeDataString(encodedValue);
                }
                catch (Exception)
                {
                    error = "工坊链接查询参数包含无效转义。";
                    return false;
                }

                if (!string.Equals(name, "id", StringComparison.Ordinal))
                {
                    if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "工坊链接的 id 参数名必须使用小写。";
                        return false;
                    }
                    continue;
                }
                idCount++;
                rawId = value;
            }

            if (idCount == 0)
            {
                error = "工坊链接缺少 id 参数。";
                return false;
            }
            if (idCount != 1)
            {
                error = "工坊链接必须且只能包含一个 id 参数。";
                return false;
            }
            return true;
        }

        private static bool HasValidPercentEscapes(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '%') continue;
                if (i + 2 >= value.Length || !IsAsciiHex(value[i + 1]) ||
                    !IsAsciiHex(value[i + 2]))
                    return false;
                i += 2;
            }
            return true;
        }

        private static bool IsAsciiHex(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'a' && c <= 'f') ||
                   (c >= 'A' && c <= 'F');
        }

        private static bool TryNormalizeNumericId(string value, out string normalizedId,
            out string error)
        {
            normalizedId = null;
            error = null;
            if (string.IsNullOrEmpty(value) || !ContainsOnlyAsciiDigits(value))
            {
                error = "Workshop ID 必须是纯数字。";
                return false;
            }

            ulong id;
            if (!ulong.TryParse(value, NumberStyles.None,
                CultureInfo.InvariantCulture, out id))
            {
                error = "Workshop ID 超出支持的数字范围。";
                return false;
            }
            if (id == 0)
            {
                error = "Workshop ID 不能为 0。";
                return false;
            }

            normalizedId = id.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool ContainsOnlyAsciiDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
                if (c < '0' || c > '9') return false;
            return true;
        }

        public static string PageUrl(string normalizedId)
        {
            string canonicalId;
            string error;
            if (!TryNormalizeNumericId(normalizedId, out canonicalId, out error) ||
                !string.Equals(normalizedId, canonicalId, StringComparison.Ordinal))
                throw new ArgumentException("Workshop ID 必须是规范的非零纯数字。",
                    nameof(normalizedId));
            return "https://steamcommunity.com/sharedfiles/filedetails/?id=" + normalizedId;
        }
    }

    /// <summary>本地 installed.json：modId -> 安装状态。</summary>
    public class InstalledMod
    {
        public string version { get; set; }
        public List<string> files { get; set; }
        public bool enabled { get; set; } = true;
    }

    /// <summary>mod 在界面上的综合状态。</summary>
    public enum ModStatus
    {
        NotInstalled,      // 未安装
        UpToDate,          // 已装且最新
        UpdateAvailable,   // 已装但有新版
        InstalledUnknown,  // 目录里有文件但无记录（存量用户）
        Disabled,          // 已禁用
    }
}
