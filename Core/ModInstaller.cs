using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace StudentAgeModManager.Core
{
    /// <summary>安装 / 更新 / 卸载 / 启用 / 禁用。</summary>
    public class ModInstaller
    {
        public const string WorkshopBridgeFileName = "StudentAge.WorkshopBridge.dll";
        private const string WorkshopBridgeResourceName =
            "StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll";

        private readonly LocalState _state;
        private readonly Downloader _downloader;

        public ModInstaller(LocalState state, Downloader downloader)
        {
            _state = state;
            _downloader = downloader;
        }

        public static bool IsGameRunning()
        {
            return Process.GetProcessesByName("StudentAge").Length > 0;
        }

        public bool IsBepInExInstalled()
        {
            return File.Exists(Path.Combine(_state.GameDir, "winhttp.dll"))
                && Directory.Exists(Path.Combine(_state.GameDir, "BepInEx", "core"));
        }

        public string WorkshopBridgePath => Path.Combine(_state.GameDir,
            "BepInEx", "patchers", WorkshopBridgeFileName);

        public bool IsWorkshopBridgeInstalled()
        {
            return File.Exists(WorkshopBridgePath);
        }

        public bool IsWorkshopBridgeCurrent()
        {
            if (!IsWorkshopBridgeInstalled()) return false;
            try { return GetFileHash(WorkshopBridgePath) == GetEmbeddedBridgeHash(); }
            catch { return false; }
        }

        private string ResolveInstallDir(ModEntry entry)
        {
            var rel = (entry.installDir ?? "").Replace('/', '\\').Trim('\\');
            if (rel.Length == 0 || rel.Contains("..")) throw new InvalidDataException("非法安装路径: " + entry.installDir);
            return Path.Combine(_state.GameDir, rel);
        }

        /// <summary>安装或覆盖更新。返回写入的文件清单（相对游戏目录）。</summary>
        public async Task InstallAsync(ModEntry entry, Action<int, string> progress,
            CancellationToken ct = default(CancellationToken))
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (WorkshopItem.IsDeclared(entry))
                throw new InvalidOperationException(
                    "创意工坊条目只能由 Steam 安装，已拒绝直接下载 DLL。");
            EnsureGameNotRunning();
            var installDir = ResolveInstallDir(entry);
            var temp = await _downloader.DownloadFileAsync(entry.downloadUrl, progress, ct);
            try
            {
                var files = new List<string>();
                Directory.CreateDirectory(installDir);

                if (string.Equals(entry.assetType, "zip", StringComparison.OrdinalIgnoreCase))
                {
                    using (var zip = ZipFile.OpenRead(temp)) // 同时起到校验 zip 有效的作用
                    {
                        foreach (var e in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(e.Name)) continue; // 目录项
                            var relPath = e.FullName.Replace('/', '\\');
                            if (relPath.Contains("..")) continue;      // zip slip 防护
                            var dest = Path.Combine(installDir, relPath);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            e.ExtractToFile(dest, true);
                            files.Add(RelativeToGame(dest));
                        }
                    }
                }
                else // dll：单文件
                {
                    var fileName = Path.GetFileName(new Uri(entry.downloadUrl).AbsolutePath);
                    if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        fileName = entry.id + ".dll";
                    var dest = Path.Combine(installDir, fileName);
                    File.Copy(temp, dest, true);
                    files.Add(RelativeToGame(dest));
                }

                // 收编旧记录中多余的文件（更新后不再存在的文件不主动删，避免误删用户配置）
                _state.Set(entry.id, new InstalledMod
                {
                    version = entry.version,
                    files = files,
                    enabled = true,
                });
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        public void Uninstall(ModEntry entry)
        {
            EnsureGameNotRunning();
            var rec = _state.Get(entry.id);
            var installDir = ResolveInstallDir(entry);

            if (rec != null && rec.files != null && rec.files.Count > 0)
            {
                foreach (var rel in rec.files)
                {
                    try
                    {
                        var p = Path.Combine(_state.GameDir, rel);
                        if (File.Exists(p)) File.Delete(p);
                    }
                    catch { }
                }
            }
            // 目录空了就删目录；存量用户（无记录）直接删整个安装目录
            try
            {
                if (Directory.Exists(installDir) &&
                    (rec == null || !Directory.EnumerateFileSystemEntries(installDir).Any()))
                    Directory.Delete(installDir, rec == null);
            }
            catch { }
            // 同时清理禁用区
            try
            {
                var disabled = Path.Combine(_state.DisabledDir, entry.id);
                if (Directory.Exists(disabled)) Directory.Delete(disabled, true);
            }
            catch { }
            _state.Remove(entry.id);
        }

        /// <summary>禁用：把安装目录整体移动到 disabled/&lt;id&gt;。</summary>
        public void Disable(ModEntry entry)
        {
            EnsureGameNotRunning();
            var rec = _state.Get(entry.id);
            if (rec == null || !rec.enabled) return;
            var installDir = ResolveInstallDir(entry);
            var target = Path.Combine(_state.DisabledDir, entry.id);
            if (!Directory.Exists(installDir)) throw new DirectoryNotFoundException("安装目录不存在: " + installDir);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(_state.DisabledDir);
            Directory.Move(installDir, target);
            rec.enabled = false;
            _state.Set(entry.id, rec);
        }

        /// <summary>启用：从 disabled/&lt;id&gt; 移回安装目录。</summary>
        public void Enable(ModEntry entry)
        {
            EnsureGameNotRunning();
            var rec = _state.Get(entry.id);
            if (rec == null || rec.enabled) return;
            var installDir = ResolveInstallDir(entry);
            var source = Path.Combine(_state.DisabledDir, entry.id);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("禁用备份不存在: " + source);
            if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(installDir));
            Directory.Move(source, installDir);
            rec.enabled = true;
            _state.Set(entry.id, rec);
        }

        /// <summary>安装 BepInEx 前置：下载 zip，解压到游戏根目录。</summary>
        public async Task InstallBepInExAsync(BepInExInfo info, Action<int, string> progress,
            CancellationToken ct = default(CancellationToken))
        {
            EnsureGameNotRunning();
            var temp = await _downloader.DownloadFileAsync(info.downloadUrl, progress, ct);
            try
            {
                using (var zip = ZipFile.OpenRead(temp))
                {
                    // 兼容「包里套了一层目录」的情况：找到 winhttp.dll 所在层作为根
                    string prefix = DetectZipRoot(zip);
                    foreach (var e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;
                        var full = e.FullName.Replace('/', '\\');
                        if (prefix.Length > 0)
                        {
                            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                            full = full.Substring(prefix.Length);
                        }
                        if (full.Length == 0 || full.Contains("..")) continue;
                        var dest = Path.Combine(_state.GameDir, full);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                }

                // Always deploy the bridge embedded in this manager, even if the
                // downloaded BepInEx archive contains an older copy.
                InstallWorkshopBridgeCore();
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        /// <summary>
        /// Installs or repairs only the workshop bridge on top of an existing BepInEx.
        /// </summary>
        public void InstallWorkshopBridge()
        {
            EnsureGameNotRunning();
            if (!IsBepInExInstalled())
                throw new InvalidOperationException("请先安装 BepInEx，再安装创意工坊 DLL 支持。");
            InstallWorkshopBridgeCore();
        }

        private void InstallWorkshopBridgeCore()
        {
            var destination = WorkshopBridgePath;
            var directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);

            var temp = destination + ".tmp";
            try
            {
                using (var source = OpenEmbeddedBridge())
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    source.CopyTo(output);
                File.Copy(temp, destination, true);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }

            if (!IsWorkshopBridgeCurrent())
                throw new IOException("创意工坊 DLL 桥接器写入后校验失败。");
        }

        private static Stream OpenEmbeddedBridge()
        {
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(WorkshopBridgeResourceName);
            if (stream == null)
                throw new InvalidDataException("管理器内未找到创意工坊 DLL 桥接器资源。");
            return stream;
        }

        private static string GetEmbeddedBridgeHash()
        {
            using (var stream = OpenEmbeddedBridge())
            using (var sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(stream));
        }

        private static string GetFileHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(stream));
        }

        /// <summary>找 winhttp.dll 在 zip 中的目录前缀（"" 表示就在根）。</summary>
        private static string DetectZipRoot(ZipArchive zip)
        {
            foreach (var e in zip.Entries)
            {
                if (e.Name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var full = e.FullName.Replace('/', '\\');
                    return full.Substring(0, full.Length - e.Name.Length);
                }
            }
            return "";
        }

        private string RelativeToGame(string fullPath)
        {
            var root = _state.GameDir.TrimEnd('\\') + "\\";
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(root.Length)
                : fullPath;
        }

        private static void EnsureGameNotRunning()
        {
            if (IsGameRunning())
                throw new InvalidOperationException("检测到游戏正在运行，DLL 被占用。请先关闭游戏再操作。");
        }
    }
}
