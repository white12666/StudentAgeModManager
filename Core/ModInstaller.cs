using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace StudentAgeModManager.Core
{
    /// <summary>安装 BepInEx 前置与创意工坊 DLL Bridge。</summary>
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

        private static void EnsureGameNotRunning()
        {
            if (IsGameRunning())
                throw new InvalidOperationException("检测到游戏正在运行，DLL 被占用。请先关闭游戏再操作。");
        }
    }
}
