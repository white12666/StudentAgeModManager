using System;
using System.Globalization;
using System.IO;
using StudentAge.WorkshopBridge;

namespace StudentAgeModManager.Core
{
    /// <summary>
    /// Moves one direct local plugin unit between the live and disabled roots. Every topology
    /// decision is revalidated while holding the same game transaction as Workshop mutation.
    /// </summary>
    public sealed class LocalPluginManager
    {
        private readonly string _gameDir;
        private readonly string _pluginRoot;
        private readonly string _disabledRoot;
        private readonly string _conflictBackupRoot;
        // Deterministic fault boundary used by integration tests. Production leaves it null.
        private Action<string> _beforeSourceMove;

        public LocalPluginManager(LocalState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            _gameDir = state.GameDir;
            _pluginRoot = Path.GetFullPath(Path.Combine(state.GameDir, "BepInEx", "plugins"));
            _disabledRoot = Path.GetFullPath(state.DisabledDir);
            _conflictBackupRoot = Path.GetFullPath(state.ConflictBackupDir);
        }

        public string Disable(LocalPluginUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            if (unit.IsDisabled) return null;
            EnsureLocalUnit(unit);
            string source = ResolveImmediateChild(_pluginRoot, unit.RelativePath);
            string target = Path.Combine(_disabledRoot, Path.GetFileName(source));
            return MoveUnderTransaction(source, target, unit.IsDirectory);
        }

        public string Enable(LocalPluginUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            if (!unit.IsDisabled) return null;
            EnsureLocalUnit(unit);
            string source = ResolveImmediateChild(_disabledRoot, unit.RelativePath);
            string target = ResolveImmediateChild(_pluginRoot, unit.EnabledRelativePath);
            return MoveUnderTransaction(source, target, unit.IsDirectory);
        }

        private string MoveUnderTransaction(string source, string target, bool isDirectory)
        {
            using (WorkshopBridgeTransaction.Acquire(_gameDir))
            {
                EnsureGameNotRunning();
                ValidateSource(source, isDirectory);

                string archived = null;
                if (Directory.Exists(target) || File.Exists(target))
                    archived = ArchiveConflictTarget(target);

                try
                {
                    // Revalidate after archiving: a stale card or external actor must never turn
                    // this into a copy/delete operation or overwrite newly appeared content.
                    ValidateSource(source, isDirectory);
                    if (Directory.Exists(target) || File.Exists(target))
                        throw new IOException("目标位置在操作期间出现同名插件，未执行覆盖: " + target);
                    _beforeSourceMove?.Invoke(source);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (isDirectory) Directory.Move(source, target);
                    else File.Move(source, target);
                    return archived;
                }
                catch (Exception moveError)
                {
                    if (archived == null) throw;
                    try
                    {
                        // Roll back only into the still-empty original slot. Never overwrite a
                        // later actor; report both locations if restoration is no longer safe.
                        if (Directory.Exists(target) || File.Exists(target))
                            throw new IOException("原目标位置已被其他操作占用。");
                        if (Directory.Exists(archived)) Directory.Move(archived, target);
                        else if (File.Exists(archived)) File.Move(archived, target);
                        else throw new FileNotFoundException("归档副本已不存在。", archived);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new IOException("插件移动失败，且无法安全恢复已归档副本。源位置: " +
                            source + "；归档位置: " + archived + "；原目标位置: " + target +
                            "。请保留这些文件并手动处理。", new AggregateException(
                                moveError, rollbackError));
                    }
                    throw new IOException("插件移动失败；冲突副本已恢复到原位置，未丢失任何副本。",
                        moveError);
                }
            }
        }

        private string ArchiveConflictTarget(string target)
        {
            bool isDirectory = Directory.Exists(target);
            bool isFile = File.Exists(target);
            if (!isDirectory && !isFile) return null;
            if (IsReparsePoint(target))
                throw new InvalidDataException(
                    "冲突位置是目录联接，可能由工坊同步管理，管理器不会移动它：" + target);

            Directory.CreateDirectory(_conflictBackupRoot);
            string baseName = Path.GetFileName(target.TrimEnd('\\', '/')) + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N");
            string destination = Path.Combine(_conflictBackupRoot, baseName);
            if (isDirectory) Directory.Move(target, destination);
            else File.Move(target, destination);
            return destination;
        }

        private static void EnsureLocalUnit(LocalPluginUnit unit)
        {
            if (unit.Source != LocalPluginSource.Local)
                throw new InvalidOperationException(
                    "Steam 工坊 Mod 请使用工坊开关或游戏“本地”页管理，管理器不会移动其文件。");
        }

        private static string ResolveImmediateChild(string expectedRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidDataException("插件路径为空。");
            string full = Path.GetFullPath(Path.Combine(expectedRoot,
                Path.GetFileName(relativePath.TrimEnd('\\', '/'))));
            string parent = Path.GetDirectoryName(full.TrimEnd('\\', '/'));
            if (!string.Equals(parent, expectedRoot.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件路径越界。");
            return full;
        }

        private static void ValidateSource(string source, bool expectedDirectory)
        {
            bool directory = Directory.Exists(source);
            bool file = File.Exists(source);
            if (!directory && !file)
                throw new FileNotFoundException("插件不存在或已被移动；请刷新列表后重试。", source);
            if (directory != expectedDirectory || file == expectedDirectory)
                throw new IOException("插件类型已发生变化；请刷新列表后重试: " + source);
            if (IsReparsePoint(source))
                throw new InvalidDataException("该目录由工坊同步管理，管理器不会移动它。");
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static void EnsureGameNotRunning()
        {
            if (ModInstaller.IsGameRunning())
                throw new InvalidOperationException(
                    "检测到游戏正在运行，DLL 可能被占用。请先关闭游戏再操作。");
        }
    }
}
