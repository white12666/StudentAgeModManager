using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace StudentAgeModManager.Core
{
    /// <summary>
    /// 本地安装状态（installed.json），存于游戏目录 BepInEx/ModManager/ 下。
    /// 另存少量工具设置（settings.json）：手选的游戏目录、镜像模式。
    /// </summary>
    public class LocalState
    {
        public string GameDir { get; }
        public string StateDir => Path.Combine(GameDir, "BepInEx", "ModManager");
        public string DisabledDir => Path.Combine(StateDir, "disabled");
        private string InstalledJsonPath => Path.Combine(StateDir, "installed.json");

        public Dictionary<string, InstalledMod> Installed { get; private set; }
            = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public LocalState(string gameDir)
        {
            GameDir = gameDir;
            Load();
        }

        public void Load()
        {
            Installed = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(InstalledJsonPath))
                {
                    var text = File.ReadAllText(InstalledJsonPath);
                    var data = Json.Deserialize<Dictionary<string, InstalledMod>>(text);
                    if (data != null)
                        Installed = new Dictionary<string, InstalledMod>(data, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { /* 损坏时按空状态处理，安装时会重建 */ }
        }

        public void Save()
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(InstalledJsonPath, Json.Serialize(Installed));
        }

        public InstalledMod Get(string modId)
        {
            InstalledMod m;
            return Installed.TryGetValue(modId, out m) ? m : null;
        }

        public void Set(string modId, InstalledMod info)
        {
            Installed[modId] = info;
            Save();
        }

        public void Remove(string modId)
        {
            if (Installed.Remove(modId)) Save();
        }

        /// <summary>
        /// 综合判断某 mod 的状态（含存量用户收编：目录里有文件但无记录）。
        /// </summary>
        public ModStatus GetStatus(ModEntry entry)
        {
            var rec = Get(entry.id);
            if (rec != null)
            {
                if (!rec.enabled) return ModStatus.Disabled;
                if (string.IsNullOrEmpty(rec.version)) return ModStatus.InstalledUnknown;
                return VersionCompare(rec.version, entry.version) < 0
                    ? ModStatus.UpdateAvailable
                    : ModStatus.UpToDate;
            }
            // 无记录：探测安装目录里是否已有文件（存量用户）
            var relativeInstallDir = (entry.installDir ?? string.Empty).Replace('/', '\\').Trim('\\');
            if (relativeInstallDir.Length == 0) return ModStatus.NotInstalled;
            var dir = Path.Combine(GameDir, relativeInstallDir);
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0)
                return ModStatus.InstalledUnknown;
            return ModStatus.NotInstalled;
        }

        /// <summary>宽松版本比较：去掉 v 前缀，逐段数字比较。返回 -1/0/1。</summary>
        public static int VersionCompare(string a, string b)
        {
            a = (a ?? "").TrimStart('v', 'V');
            b = (b ?? "").TrimStart('v', 'V');
            var pa = a.Split('.', '-');
            var pb = b.Split('.', '-');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = 0, vb = 0;
                if (i < pa.Length) int.TryParse(pa[i], out va);
                if (i < pb.Length) int.TryParse(pb[i], out vb);
                if (va != vb) return va < vb ? -1 : 1;
            }
            return 0;
        }
    }
}
