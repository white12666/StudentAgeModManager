using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StudentAge.WorkshopBridge
{
    public sealed class BridgeOptions
    {
        public const string DefaultMarkerFileName = "workshop-plugin.json";
        public const string WorkshopAppId = "1991040";

        public string GameRootPath { get; set; }
        public string WorkshopRootPath { get; set; }
        public string ActiveModListPath { get; set; }
        public string PluginRootPath { get; set; }
        public string MarkerFileName { get; set; } = DefaultMarkerFileName;

        public static BridgeOptions ForGame(string gameRootPath)
        {
            if (string.IsNullOrWhiteSpace(gameRootPath))
                throw new ArgumentException("游戏目录为空。", nameof(gameRootPath));

            gameRootPath = Path.GetFullPath(gameRootPath);
            return new BridgeOptions
            {
                GameRootPath = gameRootPath,
                WorkshopRootPath = SteamPathLocator.FindWorkshopRoot(gameRootPath),
                ActiveModListPath = SteamPathLocator.FindActiveModList(gameRootPath),
                PluginRootPath = Path.Combine(gameRootPath, "BepInEx", "plugins"),
            };
        }
    }

    public sealed class BridgeResult
    {
        private readonly List<string> _messages = new List<string>();

        public bool Synchronized { get; internal set; }
        public int EnabledIdCount { get; internal set; }
        public int LinkedCount { get; internal set; }
        public int RemovedLinkCount { get; internal set; }
        public int SkippedCount { get; internal set; }
        public int ErrorCount { get; internal set; }
        public IReadOnlyList<string> Messages => _messages;

        internal void Info(string message) => _messages.Add("[Info] " + message);
        internal void Warning(string message) => _messages.Add("[Warning] " + message);
        internal void Error(string message)
        {
            ErrorCount++;
            _messages.Add("[Error] " + message);
        }
    }

    [DataContract]
    internal sealed class WorkshopPluginManifest
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "pluginRoot")]
        public string PluginRoot { get; set; }
    }

    public static class WorkshopBridgeSynchronizer
    {
        public const string LinkDirectoryName = ".workshop";
        private const string WorkshopPluginRelativePath = @"BepInEx\plugins";
        private const string ManifestType = "bepinex-plugin";
        private const string ManifestPluginRoot = "BepInEx/plugins";
        private const int MaxManifestBytes = 64 * 1024;

        public static BridgeResult Synchronize(BridgeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var result = new BridgeResult();

            if (string.IsNullOrWhiteSpace(options.PluginRootPath))
            {
                result.Error("BepInEx 插件目录为空；无法安全停用或同步工坊链接。");
                return result;
            }

            string markerFileName;
            try
            {
                markerFileName = string.IsNullOrWhiteSpace(options.MarkerFileName)
                    ? BridgeOptions.DefaultMarkerFileName
                    : options.MarkerFileName;
                if (!string.Equals(markerFileName, Path.GetFileName(markerFileName),
                    StringComparison.Ordinal) || markerFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    result.Error("DLL 工坊声明文件名无效；不执行同步。");
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Error("DLL 工坊声明文件名无效；不执行同步: " + ex.Message);
                return result;
            }

            var linkRoot = Path.Combine(options.PluginRootPath, LinkDirectoryName);
            try
            {
                Directory.CreateDirectory(options.PluginRootPath);
                var existingLinkRoot = Directory.GetFileSystemEntries(options.PluginRootPath)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), LinkDirectoryName,
                        StringComparison.OrdinalIgnoreCase));
                if (existingLinkRoot != null)
                {
                    var attributes = File.GetAttributes(existingLinkRoot);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        result.Error("工坊桥接根目录不能是重解析点；不执行同步: " + linkRoot);
                        return result;
                    }
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        result.Error("工坊桥接根路径被普通文件占用；不执行同步: " + linkRoot);
                        return result;
                    }
                }
                else
                {
                    Directory.CreateDirectory(linkRoot);
                }
            }
            catch (Exception ex)
            {
                result.Error("无法创建工坊桥接目录: " + ex.Message);
                return result;
            }

            // Fail closed. Existing junctions can belong to a previously active Steam
            // user, so an unavailable/ambiguous _mod file must not leave DLLs enabled.
            if (string.IsNullOrWhiteSpace(options.ActiveModListPath) ||
                !File.Exists(options.ActiveModListPath))
            {
                RemoveExistingLinks(linkRoot, result);
                result.Warning("未找到游戏原生 Mod 启用列表；已清理可安全删除的旧联接，本次不加载 DLL 工坊项目。");
                return result;
            }

            HashSet<ulong> enabledIds;
            try
            {
                enabledIds = ReadEnabledIds(options.ActiveModListPath, result);
            }
            catch (Exception ex)
            {
                RemoveExistingLinks(linkRoot, result);
                result.Error("读取 Mod 启用列表失败；已清理可安全删除的旧联接: " + ex.Message);
                return result;
            }

            result.EnabledIdCount = enabledIds.Count;
            if (string.IsNullOrWhiteSpace(options.WorkshopRootPath) ||
                !Directory.Exists(options.WorkshopRootPath))
            {
                RemoveExistingLinks(linkRoot, result);
                result.Warning("未找到 Steam 创意工坊目录；已清理可安全删除的旧联接，本次不加载 DLL 工坊项目。");
                return result;
            }

            // Links are cheap. Recreate bridge-owned reparse points on every start so an
            // updated/moved Steam library can never leave an old target behind.
            RemoveExistingLinks(linkRoot, result);

            foreach (var workshopId in enabledIds.OrderBy(id => id))
            {
                var id = workshopId.ToString(CultureInfo.InvariantCulture);
                var itemRoot = Path.Combine(options.WorkshopRootPath, id);
                var markerPath = Path.Combine(itemRoot, markerFileName);
                var sourcePluginRoot = Path.Combine(itemRoot, WorkshopPluginRelativePath);
                var destination = Path.Combine(linkRoot, id);

                if (!Directory.Exists(itemRoot))
                {
                    result.SkippedCount++;
                    result.Warning("已启用的工坊项目尚未安装或已取消订阅: " + id);
                    continue;
                }

                if (!File.Exists(markerPath))
                {
                    // Normal JSON workshop mods intentionally land here.
                    result.SkippedCount++;
                    result.Info("跳过非 DLL 工坊项目（无声明文件）: " + id);
                    continue;
                }

                string manifestError;
                if (!TryValidateManifest(markerPath, out manifestError))
                {
                    result.SkippedCount++;
                    result.Error("DLL 工坊项目声明文件无效 " + id + ": " + manifestError);
                    continue;
                }

                if (!Directory.Exists(sourcePluginRoot))
                {
                    result.SkippedCount++;
                    result.Warning("DLL 工坊项目缺少 BepInEx/plugins 目录: " + id);
                    continue;
                }

                bool hasDll;
                try
                {
                    hasDll = Directory.GetFiles(sourcePluginRoot, "*.dll", SearchOption.AllDirectories).Length > 0;
                }
                catch (Exception ex)
                {
                    result.SkippedCount++;
                    result.Error("无法检查工坊项目 " + id + " 的 DLL: " + ex.Message);
                    continue;
                }

                if (!hasDll)
                {
                    result.SkippedCount++;
                    result.Warning("DLL 工坊项目的插件目录中没有 DLL: " + id);
                    continue;
                }

                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    result.SkippedCount++;
                    result.Error("桥接目标已被普通文件或目录占用，未覆盖: " + destination);
                    continue;
                }

                string error;
                if (JunctionManager.TryCreate(destination, sourcePluginRoot, out error))
                {
                    result.LinkedCount++;
                    result.Info("已桥接工坊 DLL: " + id + " -> " + sourcePluginRoot);
                }
                else
                {
                    result.Error("创建工坊链接失败 " + id + ": " + error);
                }
            }

            result.Synchronized = true;
            return result;
        }

        private static bool TryValidateManifest(string path, out string error)
        {
            error = null;
            try
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length <= 0)
                {
                    error = "文件为空。";
                    return false;
                }
                if (fileInfo.Length > MaxManifestBytes)
                {
                    error = "文件超过 64 KiB 限制。";
                    return false;
                }

                WorkshopPluginManifest manifest;
                var serializer = new DataContractJsonSerializer(typeof(WorkshopPluginManifest));
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    manifest = serializer.ReadObject(stream) as WorkshopPluginManifest;

                if (manifest == null)
                {
                    error = "无法读取声明对象。";
                    return false;
                }
                if (manifest.SchemaVersion != 1)
                {
                    error = "仅支持 schemaVersion=1。";
                    return false;
                }
                if (!string.Equals(manifest.Type, ManifestType, StringComparison.Ordinal))
                {
                    error = "type 必须为 bepinex-plugin。";
                    return false;
                }
                if (!string.Equals(manifest.PluginRoot, ManifestPluginRoot, StringComparison.Ordinal))
                {
                    error = "pluginRoot 必须为固定路径 BepInEx/plugins。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static HashSet<ulong> ReadEnabledIds(string path, BridgeResult result)
        {
            var ids = new HashSet<ulong>();
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0) continue;

                ulong id;
                if (ulong.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out id))
                    if (id != 0) ids.Add(id); else result.Warning("忽略无效的 Mod ID: 0");
                else
                    result.Warning("忽略无法解析的 Mod ID: " + line);
            }
            return ids;
        }

        private static void RemoveExistingLinks(string linkRoot, BridgeResult result)
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(linkRoot);
            }
            catch (Exception ex)
            {
                result.Error("无法枚举已有工坊链接: " + ex.Message);
                return;
            }

            foreach (var entry in entries)
            {
                try
                {
                    ulong bridgeId;
                    var entryName = Path.GetFileName(entry);
                    if (!ulong.TryParse(entryName, NumberStyles.None,
                        CultureInfo.InvariantCulture, out bridgeId) || bridgeId == 0 ||
                        !string.Equals(entryName, bridgeId.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal))
                    {
                        result.Warning("桥接目录中存在非标准 ID 项目，出于安全考虑保留: " + entry);
                        continue;
                    }

                    var attributes = File.GetAttributes(entry);
                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (!isDirectory || !isReparsePoint)
                    {
                        result.Warning("桥接目录中存在非链接项目，出于安全考虑保留: " + entry);
                        continue;
                    }

                    Directory.Delete(entry, false);
                    result.RemovedLinkCount++;
                }
                catch (Exception ex)
                {
                    result.Error("清理旧工坊链接失败 " + entry + ": " + ex.Message);
                }
            }
        }
    }

    internal static class JunctionManager
    {
        public static bool TryCreate(string junctionPath, string targetPath, out string error)
        {
            error = null;
            try
            {
                junctionPath = Path.GetFullPath(junctionPath);
                targetPath = Path.GetFullPath(targetPath);
                if (ContainsUnsafeCommandCharacter(junctionPath) || ContainsUnsafeCommandCharacter(targetPath))
                {
                    error = "路径包含目录联接命令不支持的字符。";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(junctionPath));
                var commandInterpreter = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

                var startInfo = new ProcessStartInfo
                {
                    FileName = commandInterpreter,
                    Arguments = "/D /V:OFF /C mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "无法启动系统目录联接命令。";
                        return false;
                    }

                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        error = "创建目录联接超时。";
                        return false;
                    }

                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    if (process.ExitCode != 0)
                    {
                        error = (stderr + " " + stdout).Trim();
                        if (error.Length == 0) error = "mklink 返回代码 " + process.ExitCode;
                        return false;
                    }
                }

                if (!Directory.Exists(junctionPath))
                {
                    error = "系统报告成功，但目录联接不存在。";
                    return false;
                }

                var attributes = File.GetAttributes(junctionPath);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    error = "创建出的目录不是重解析点，已拒绝使用。";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool ContainsUnsafeCommandCharacter(string path)
        {
            return path.IndexOfAny(new[]
            {
                '"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n'
            }) >= 0;
        }
    }

    internal static class SteamPathLocator
    {
        private const ulong SteamId64Base = 76561197960265728UL;

        public static string FindWorkshopRoot(string gameRootPath)
        {
            var steamAppsCandidates = new List<string>();
            try
            {
                AddCandidate(steamAppsCandidates, FindGameSteamAppsDirectory(gameRootPath));
                AddSteamInstallCandidate(steamAppsCandidates,
                    Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null));
                AddSteamInstallCandidate(steamAppsCandidates,
                    Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null));
                AddSteamInstallCandidate(steamAppsCandidates,
                    Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null));
            }
            catch
            {
                // The game library candidate is normally enough; continue with what was found.
            }

            // libraryfolders.vdf normally lives in the main Steam installation. Also
            // inspect a copy beside the game when present, which helps portable setups.
            foreach (var steamApps in steamAppsCandidates.ToArray())
            {
                try
                {
                    var libraryFolders = Path.Combine(steamApps, "libraryfolders.vdf");
                    if (!File.Exists(libraryFolders)) continue;
                    var text = File.ReadAllText(libraryFolders);
                    foreach (Match match in Regex.Matches(text,
                        @"""path""\s+""(?<path>[^""]+)""", RegexOptions.IgnoreCase))
                    {
                        var libraryRoot = DecodeVdfPath(match.Groups["path"].Value);
                        AddCandidate(steamAppsCandidates, Path.Combine(libraryRoot, "steamapps"));
                    }

                    // Compatibility with the old flat libraryfolders.vdf format.
                    foreach (Match match in Regex.Matches(text,
                        @"""[0-9]+""\s+""(?<path>[^""]+)""", RegexOptions.IgnoreCase))
                    {
                        var libraryRoot = DecodeVdfPath(match.Groups["path"].Value);
                        AddCandidate(steamAppsCandidates, Path.Combine(libraryRoot, "steamapps"));
                    }
                }
                catch
                {
                    // Ignore a malformed/unreadable VDF and continue with other libraries.
                }
            }

            string fallback = null;
            foreach (var steamApps in steamAppsCandidates)
            {
                try
                {
                    var workshopRoot = Path.Combine(steamApps, "workshop", "content",
                        BridgeOptions.WorkshopAppId);
                    if (fallback == null) fallback = workshopRoot;
                    if (Directory.Exists(workshopRoot)) return workshopRoot;
                }
                catch
                {
                    // Ignore this candidate.
                }
            }
            return fallback;
        }

        private static void AddSteamInstallCandidate(List<string> candidates, object registryValue)
        {
            var steamRoot = registryValue as string;
            if (string.IsNullOrWhiteSpace(steamRoot)) return;
            AddCandidate(candidates, Path.Combine(steamRoot.Replace('/', Path.DirectorySeparatorChar), "steamapps"));
        }

        private static void AddCandidate(List<string> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (!Path.IsPathRooted(path)) return;
                path = Path.GetFullPath(path.Trim());
                if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(path);
            }
            catch
            {
                // Ignore malformed candidate paths.
            }
        }

        private static string DecodeVdfPath(string path)
        {
            return (path ?? string.Empty).Replace(@"\\", @"\").Replace('/', Path.DirectorySeparatorChar);
        }

        private static string FindGameSteamAppsDirectory(string gameRootPath)
        {
            try
            {
                var commonDirectory = Directory.GetParent(Path.GetFullPath(gameRootPath));
                var steamAppsDirectory = commonDirectory == null ? null : commonDirectory.Parent;
                return steamAppsDirectory == null ? null : steamAppsDirectory.FullName;
            }
            catch
            {
                return null;
            }
        }

        public static string FindActiveModList(string gameRootPath)
        {
            var savesRoot = FindSavesRoot();
            if (string.IsNullOrEmpty(savesRoot)) return null;

            var steamId = FindActiveSteamId64(gameRootPath);
            if (!string.IsNullOrEmpty(steamId))
            {
                var exact = Path.Combine(savesRoot, steamId, "_mod");
                return File.Exists(exact) ? exact : null;
            }

            if (!Directory.Exists(savesRoot)) return null;
            try
            {
                // Without a trustworthy active Steam ID, only a single save profile is
                // unambiguous. Never guess between multiple users and load another
                // profile's enabled DLL set.
                var candidates = Directory.GetDirectories(savesRoot)
                    .Select(directory => Path.Combine(directory, "_mod"))
                    .Where(File.Exists)
                    .Take(2)
                    .ToArray();
                return candidates.Length == 1 ? candidates[0] : null;
            }
            catch
            {
                return null;
            }
        }

        private static string FindSavesRoot()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Directory.GetParent(local);
            if (appData == null) return null;
            return Path.Combine(appData.FullName, "LocalLow", "PakyiGame", "StudentAge", "Saves");
        }

        private static string FindActiveSteamId64(string gameRootPath)
        {
            try
            {
                var value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess",
                    "ActiveUser", null);
                if (value != null)
                {
                    uint accountId;
                    if (value is int)
                        accountId = unchecked((uint)(int)value);
                    else
                        accountId = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
                    if (accountId != 0)
                        return (SteamId64Base + accountId).ToString(CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // Fall through to appmanifest LastOwner.
            }

            try
            {
                var steamAppsDirectory = FindGameSteamAppsDirectory(gameRootPath);
                if (string.IsNullOrEmpty(steamAppsDirectory)) return null;
                var appManifest = Path.Combine(steamAppsDirectory,
                    "appmanifest_" + BridgeOptions.WorkshopAppId + ".acf");
                if (!File.Exists(appManifest)) return null;

                var match = Regex.Match(File.ReadAllText(appManifest),
                    "\\\"LastOwner\\\"\\s+\\\"(?<id>[0-9]+)\\\"",
                    RegexOptions.IgnoreCase);
                return match.Success ? match.Groups["id"].Value : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
