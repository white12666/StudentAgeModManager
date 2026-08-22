using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StudentAge.WorkshopBridge;

namespace StudentAgeModManager.Core
{
    /// <summary>安装 BepInEx 前置与创意工坊 DLL Bridge。</summary>
    public class ModInstaller
    {
        public const string BepInExVersion = "5.4.23";
        public const string WorkshopBridgeFileName = "StudentAge.WorkshopBridge.dll";
        public const string EmbeddedBepInExPackageSha256 =
            "D1C85CDC44F999883BF36587AD1C1DD03B149C7A9FB2700D651FFD6ED433B971";
        private const string BepInExPackageResourceName =
            "StudentAgeModManager.Resources.BepInEx-5.4.23-package.zip";
        private const string WorkshopBridgeResourceName =
            "StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll";
        private const string CompletionMarkerName = "bepinex-install.complete";
        private const string IncompleteMarkerName = "bepinex-install.incomplete";

        private readonly LocalState _state;
        // Deterministic fault boundary used by integration tests. Production leaves it null.
        private Action<string> _beforeAtomicCommit;

        public ModInstaller(LocalState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public static bool IsGameRunning()
        {
            return Process.GetProcessesByName("StudentAge").Length > 0;
        }

        private string CompletionMarkerPath => Path.Combine(_state.GameDir, "BepInEx",
            "ModManager", CompletionMarkerName);
        private string IncompleteMarkerPath => Path.Combine(_state.GameDir, "BepInEx",
            "ModManager", IncompleteMarkerName);

        public bool IsBepInExInstalled()
        {
            if (!HasLegacyBepInExFiles() || File.Exists(IncompleteMarkerPath)) return false;
            // Preserve legacy/third-party BepInEx detection. Once this manager has emitted a
            // completion record, however, that record is authoritative and must validate fully.
            return !File.Exists(CompletionMarkerPath) || IsEmbeddedBepInExPackageCurrent();
        }

        /// <summary>
        /// True only when this manager committed the exact current embedded package. A valid
        /// legacy or third-party BepInEx remains installed but is not claimed as manager-verified.
        /// </summary>
        public bool IsEmbeddedBepInExPackageCurrent()
        {
            if (!File.Exists(CompletionMarkerPath) || File.Exists(IncompleteMarkerPath))
                return false;
            try
            {
                IList<PackageFile> expected = ReadEmbeddedPackageManifest();
                string[] lines = File.ReadAllLines(CompletionMarkerPath);
                if (lines.Length != expected.Count + 3 ||
                    !string.Equals(lines[0], "StudentAgeModManager.BepInExComplete|1",
                        StringComparison.Ordinal) ||
                    !string.Equals(lines[1], "PackageSha256|" + EmbeddedBepInExPackageSha256,
                        StringComparison.Ordinal) ||
                    !string.Equals(lines[2], "FileCount|" + expected.Count,
                        StringComparison.Ordinal)) return false;

                for (int index = 0; index < expected.Count; index++)
                {
                    PackageFile file = expected[index];
                    if (!string.Equals(lines[index + 3], file.RelativePath + "|" + file.Hash,
                            StringComparison.Ordinal) ||
                        !File.Exists(ResolvePackageDestination(file.RelativePath)) ||
                        !string.Equals(GetFileHash(ResolvePackageDestination(file.RelativePath)),
                            file.Hash, StringComparison.Ordinal)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private bool HasLegacyBepInExFiles()
        {
            return File.Exists(Path.Combine(_state.GameDir, "winhttp.dll")) &&
                File.Exists(Path.Combine(_state.GameDir, "BepInEx", "core", "BepInEx.dll"));
        }

        public string WorkshopBridgePath => Path.Combine(_state.GameDir,
            "BepInEx", "patchers", WorkshopBridgeFileName);

        public bool IsWorkshopBridgeInstalled() => File.Exists(WorkshopBridgePath);

        public bool IsWorkshopBridgeCurrent()
        {
            if (!IsWorkshopBridgeInstalled()) return false;
            try { return GetFileHash(WorkshopBridgePath) == GetEmbeddedBridgeHash(); }
            catch { return false; }
        }

        public Task InstallBepInExAsync(Action<int, string> progress,
            CancellationToken ct = default(CancellationToken))
        {
            EnsureGameNotRunning();
            return Task.Run(() => InstallBepInExCore(progress, ct), ct);
        }

        private void InstallBepInExCore(Action<int, string> progress, CancellationToken ct)
        {
            EnsureGameNotRunning();
            ct.ThrowIfCancellationRequested();
            const string sourceLabel = "内置 BepInEx 5.4.23";
            progress?.Invoke(0, sourceLabel);

            string stageRoot = Path.Combine(Path.GetTempPath(),
                "StudentAgeModManager.BepInEx." + Guid.NewGuid().ToString("N"));
            try
            {
                List<StagedFile> files = StagePackage(stageRoot, progress, sourceLabel, ct);
                ValidateStage(files);
                using (WorkshopBridgeTransaction.Acquire(_state.GameDir))
                {
                    EnsureGameNotRunning();
                    ValidateStage(files);
                    ct.ThrowIfCancellationRequested();

                    // Commit an interruption record before invalidating the prior package record
                    // or touching live files. It prevents a partial tree falling back to legacy
                    // detection if the process exits during any subsequent atomic commit.
                    WriteIncompleteMarker();
                    TryDelete(CompletionMarkerPath);
                    int committed = 0;
                    foreach (StagedFile file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        AtomicCommitFile(file.StagePath,
                            ResolvePackageDestination(file.RelativePath), file.Hash);
                        committed++;
                        progress?.Invoke(Math.Min(94, 90 + committed * 4 / files.Count),
                            sourceLabel);
                    }
                    InstallWorkshopBridgeCore();
                    WriteCompletionMarker(files);
                    DeleteRequired(IncompleteMarkerPath,
                        "无法清除 BepInEx 安装中断标记，安装未完成。");
                    if (!IsEmbeddedBepInExPackageCurrent())
                        throw new IOException("BepInEx 完成标记写入后校验失败，安装未完成。");
                }
                progress?.Invoke(100, sourceLabel);
            }
            finally
            {
                try { Directory.Delete(stageRoot, true); } catch { }
            }
        }

        private List<StagedFile> StagePackage(string stageRoot, Action<int, string> progress,
            string sourceLabel, CancellationToken ct)
        {
            using (Stream hashStream = OpenEmbeddedBepInExPackage())
            {
                string actualHash = GetStreamHashHex(hashStream);
                if (!string.Equals(actualHash, EmbeddedBepInExPackageSha256,
                    StringComparison.Ordinal))
                    throw new InvalidDataException("内嵌 BepInEx 安装包校验失败。期望 " +
                        EmbeddedBepInExPackageSha256 + "，实际 " + actualHash + "。");
            }

            Directory.CreateDirectory(stageRoot);
            var files = new List<StagedFile>();
            using (Stream package = OpenEmbeddedBepInExPackage())
            using (var zip = new ZipArchive(package, ZipArchiveMode.Read, false))
            {
                string prefix = DetectZipRoot(zip);
                int totalFiles = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                    if (!string.IsNullOrEmpty(entry.Name)) totalFiles++;

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string relativePath = NormalizeEntryPath(entry, prefix);
                    if (relativePath == null) continue;
                    string stagePath = Path.Combine(stageRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagePath));
                    using (Stream source = entry.Open())
                    using (var output = new FileStream(stagePath, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(output);
                        output.Flush(true);
                    }
                    files.Add(new StagedFile(relativePath, stagePath, GetFileHash(stagePath)));
                    progress?.Invoke(totalFiles == 0 ? 90 :
                        Math.Min(90, files.Count * 90 / totalFiles), sourceLabel);
                }
            }
            return files;
        }

        private static void ValidateStage(IList<StagedFile> files)
        {
            bool hasProxy = false;
            bool hasCore = false;
            if (files.Count == 0) throw new InvalidDataException("内嵌 BepInEx 安装包为空。");
            foreach (StagedFile file in files)
            {
                if (!File.Exists(file.StagePath) ||
                    !string.Equals(GetFileHash(file.StagePath), file.Hash, StringComparison.Ordinal))
                    throw new InvalidDataException("BepInEx 暂存文件校验失败: " + file.RelativePath);
                if (string.Equals(file.RelativePath, "winhttp.dll", StringComparison.OrdinalIgnoreCase))
                    hasProxy = true;
                if (string.Equals(file.RelativePath, @"BepInEx\core\BepInEx.dll",
                    StringComparison.OrdinalIgnoreCase)) hasCore = true;
            }
            if (!hasProxy || !hasCore)
                throw new InvalidDataException("内嵌 BepInEx 安装包缺少完整运行时文件。");
        }

        public void InstallWorkshopBridge()
        {
            EnsureGameNotRunning();
            using (WorkshopBridgeTransaction.Acquire(_state.GameDir))
            {
                EnsureGameNotRunning();
                if (!IsBepInExInstalled())
                    throw new InvalidOperationException("请先安装 BepInEx，再安装创意工坊 DLL 支持。");
                InstallWorkshopBridgeCore();
            }
        }

        private void InstallWorkshopBridgeCore()
        {
            string expectedHash = GetEmbeddedBridgeHash();
            string directory = Path.GetDirectoryName(WorkshopBridgePath);
            Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, "." + WorkshopBridgeFileName + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (Stream source = OpenEmbeddedBridge())
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    source.CopyTo(output);
                    output.Flush(true);
                }
                AtomicCommitFile(temp, WorkshopBridgePath, expectedHash);
            }
            finally { TryDelete(temp); }

            if (!string.Equals(GetFileHash(WorkshopBridgePath), expectedHash,
                StringComparison.Ordinal))
                throw new IOException("创意工坊 DLL 桥接器写入后校验失败。");
        }

        private void WriteIncompleteMarker()
        {
            string directory = Path.GetDirectoryName(IncompleteMarkerPath);
            Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, "." + IncompleteMarkerName + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] content = new UTF8Encoding(false).GetBytes(
                    "StudentAgeModManager.BepInExIncomplete|1\r\nPackageSha256|" +
                    EmbeddedBepInExPackageSha256 + "\r\n");
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    output.Write(content, 0, content.Length);
                    output.Flush(true);
                }
                AtomicCommitFile(temp, IncompleteMarkerPath, GetFileHash(temp));
            }
            finally { TryDelete(temp); }
        }

        private void WriteCompletionMarker(IList<StagedFile> files)
        {
            string directory = Path.GetDirectoryName(CompletionMarkerPath);
            Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, "." + CompletionMarkerName + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
                {
                    writer.WriteLine("StudentAgeModManager.BepInExComplete|1");
                    writer.WriteLine("PackageSha256|" + EmbeddedBepInExPackageSha256);
                    writer.WriteLine("FileCount|" + files.Count);
                    foreach (StagedFile file in files)
                        writer.WriteLine(file.RelativePath + "|" + file.Hash);
                    writer.Flush();
                    output.Flush(true);
                }
                AtomicCommitFile(temp, CompletionMarkerPath, GetFileHash(temp));
            }
            finally { TryDelete(temp); }
        }

        private void AtomicCommitFile(string stagedPath, string destination, string expectedHash)
        {
            if (!string.Equals(GetFileHash(stagedPath), expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("提交前文件校验失败: " + destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temp = Path.Combine(Path.GetDirectoryName(destination), "." +
                Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (Stream source = File.OpenRead(stagedPath))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    source.CopyTo(output);
                    output.Flush(true);
                }
                if (!string.Equals(GetFileHash(temp), expectedHash, StringComparison.Ordinal))
                    throw new IOException("原子提交临时文件校验失败: " + destination);
                _beforeAtomicCommit?.Invoke(destination);
                if (File.Exists(destination)) File.Replace(temp, destination, null, true);
                else File.Move(temp, destination);
            }
            finally { TryDelete(temp); }
            if (!string.Equals(GetFileHash(destination), expectedHash, StringComparison.Ordinal))
                throw new IOException("原子提交后文件校验失败: " + destination);
        }

        private static Stream OpenEmbeddedBridge()
        {
            Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(WorkshopBridgeResourceName);
            if (stream == null)
                throw new InvalidDataException("管理器内未找到创意工坊 DLL 桥接器资源。");
            return stream;
        }

        private static Stream OpenEmbeddedBepInExPackage()
        {
            Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(BepInExPackageResourceName);
            if (stream == null)
                throw new InvalidDataException("管理器内未找到 BepInEx 安装包资源。");
            return stream;
        }

        private static IList<PackageFile> ReadEmbeddedPackageManifest()
        {
            using (Stream hashStream = OpenEmbeddedBepInExPackage())
                if (!string.Equals(GetStreamHashHex(hashStream),
                    EmbeddedBepInExPackageSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("内嵌 BepInEx 安装包身份校验失败。");

            var files = new List<PackageFile>();
            using (Stream package = OpenEmbeddedBepInExPackage())
            using (var zip = new ZipArchive(package, ZipArchiveMode.Read, false))
            {
                string prefix = DetectZipRoot(zip);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string relativePath = NormalizeEntryPath(entry, prefix);
                    if (relativePath == null) continue;
                    using (Stream source = entry.Open())
                    using (SHA256 sha256 = SHA256.Create())
                        files.Add(new PackageFile(relativePath,
                            Convert.ToBase64String(sha256.ComputeHash(source))));
                }
            }
            return files;
        }

        private static string NormalizeEntryPath(ZipArchiveEntry entry, string prefix)
        {
            string relativePath = entry.FullName.Replace('/', '\\');
            if (prefix.Length > 0)
            {
                if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return null;
                relativePath = relativePath.Substring(prefix.Length);
            }
            ValidateRelativePath(relativePath);
            return relativePath;
        }

        private static string GetEmbeddedBridgeHash()
        {
            using (Stream stream = OpenEmbeddedBridge())
            using (SHA256 sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(stream));
        }

        private static string GetFileHash(string path)
        {
            using (Stream stream = File.OpenRead(path))
            using (SHA256 sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(stream));
        }

        private static string GetStreamHashHex(Stream stream)
        {
            using (SHA256 sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
        }

        private string ResolvePackageDestination(string relativePath)
        {
            ValidateRelativePath(relativePath);
            string gameRoot = Path.GetFullPath(_state.GameDir);
            string rootPrefix = gameRoot.EndsWith(Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal) ? gameRoot : gameRoot + Path.DirectorySeparatorChar;
            string destination = Path.GetFullPath(Path.Combine(gameRoot, relativePath));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("内嵌 BepInEx 安装包路径越界: " + relativePath);
            return destination;
        }

        private static void ValidateRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("内嵌 BepInEx 安装包包含无效路径: " + relativePath);
            foreach (string segment in relativePath.Split('\\'))
                if (segment.Length == 0 || segment == "." || segment == ".." ||
                    segment.IndexOf(':') >= 0)
                    throw new InvalidDataException("内嵌 BepInEx 安装包包含无效路径: " +
                        relativePath);
        }

        private static string DetectZipRoot(ZipArchive zip)
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.Name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase))
                {
                    string full = entry.FullName.Replace('/', '\\');
                    return full.Substring(0, full.Length - entry.Name.Length);
                }
            }
            throw new InvalidDataException("内嵌 BepInEx 安装包缺少 winhttp.dll。");
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path)) File.Delete(path); } catch { }
        }
        private static void DeleteRequired(string path, string errorMessage)
        {
            try { File.Delete(path); }
            catch (Exception ex) { throw new IOException(errorMessage, ex); }
            if (File.Exists(path)) throw new IOException(errorMessage);
        }

        private static void EnsureGameNotRunning()
        {
            if (IsGameRunning())
                throw new InvalidOperationException(
                    "检测到游戏正在运行，DLL 被占用。请先关闭游戏再操作。");
        }

        private sealed class PackageFile
        {
            public string RelativePath { get; }
            public string Hash { get; }

            public PackageFile(string relativePath, string hash)
            {
                RelativePath = relativePath;
                Hash = hash;
            }
        }

        private sealed class StagedFile
        {
            public string RelativePath { get; }
            public string StagePath { get; }
            public string Hash { get; }

            public StagedFile(string relativePath, string stagePath, string hash)
            {
                RelativePath = relativePath;
                StagePath = stagePath;
                Hash = hash;
            }
        }
    }
}
