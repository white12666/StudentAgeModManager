using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StudentAgeModManager.Core
{
    /// <summary>
    /// 游戏目录定位，三级策略：
    /// 1. exe 自身所在目录（或其上级）就是游戏目录
    /// 2. Steam 注册表 + libraryfolders.vdf 扫描
    /// 3. 返回 null，由 UI 弹目录选择框
    /// </summary>
    public static class GameLocator
    {
        public const string GameName = "StudentAge";
        public const string GameExe = "StudentAge.exe";

        public static string Locate()
        {
            // 1) 自身目录
            var selfDir = AppDomain.CurrentDomain.BaseDirectory;
            var probe = selfDir;
            for (int i = 0; i < 3 && probe != null; i++)
            {
                if (File.Exists(Path.Combine(probe, GameExe))) return probe;
                probe = Path.GetDirectoryName(probe.TrimEnd('\\', '/'));
            }

            // 2) Steam 扫描
            var steam = GetSteamPath();
            if (steam != null)
            {
                var dir = FindGameDirInLibraries(steam);
                if (dir != null) return dir;
            }
            return null;
        }

        public static bool IsValidGameDir(string dir)
        {
            return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, GameExe));
        }

        private static string GetSteamPath()
        {
            var regKeys = new[]
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
                @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam",
            };
            foreach (var key in regKeys)
            {
                try
                {
                    var val = Registry.GetValue(key, "InstallPath", null) as string;
                    if (!string.IsNullOrEmpty(val) && Directory.Exists(val)) return val;
                }
                catch { }
            }
            foreach (var d in new[]
            {
                @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam", @"D:\Steam", @"E:\Steam",
            })
            {
                if (File.Exists(Path.Combine(d, "steam.exe"))) return d;
            }
            return null;
        }

        private static string FindGameDirInLibraries(string steamPath)
        {
            var libraries = new List<string> { steamPath };
            var vdf = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    var content = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        var path = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase) && Directory.Exists(path))
                            libraries.Add(path);
                    }
                }
                catch { }
            }
            foreach (var lib in libraries)
            {
                var gameDir = Path.Combine(lib, @"steamapps\common", GameName);
                if (File.Exists(Path.Combine(gameDir, GameExe))) return gameDir;
            }
            return null;
        }
    }
}
