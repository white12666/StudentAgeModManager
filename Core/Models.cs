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
            if (!IsDeclared(entry)) return false;

            ulong id;
            if (!ulong.TryParse(entry.workshopId.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out id) || id == 0)
                return false;

            normalizedId = id.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        public static string PageUrl(string normalizedId)
        {
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
