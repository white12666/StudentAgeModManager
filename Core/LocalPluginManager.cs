using System;
using System.IO;

namespace StudentAgeModManager.Core
{
    /// <summary>
    /// 只负责启用/禁用本地未收录插件。操作粒度是 plugins 根目录中的一个直接子项，
    /// 从不删除文件，也不操作 Workshop Bridge 创建的目录联接。
    /// </summary>
    public sealed class LocalPluginManager
    {
        private readonly string _pluginRoot;
        private readonly string _disabledRoot;

        public LocalPluginManager(LocalState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            _pluginRoot = Path.GetFullPath(Path.Combine(state.GameDir, "BepInEx", "plugins"));
            _disabledRoot = Path.GetFullPath(state.DisabledDir);
        }

        public void Disable(LocalPluginUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            if (unit.IsDisabled) return;
            EnsureLocalUnit(unit);
            EnsureGameNotRunning();

            string source = ResolveImmediateChild(_pluginRoot, unit.RelativePath);
            string target = Path.Combine(_disabledRoot, Path.GetFileName(source));
            MoveWithoutOverwrite(source, target, unit.IsDirectory);
        }

        public void Enable(LocalPluginUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            if (!unit.IsDisabled) return;
            EnsureLocalUnit(unit);
            EnsureGameNotRunning();

            string source = ResolveImmediateChild(_disabledRoot, unit.RelativePath);
            string target = ResolveImmediateChild(_pluginRoot, unit.EnabledRelativePath);
            MoveWithoutOverwrite(source, target, unit.IsDirectory);
        }

        private static void EnsureLocalUnit(LocalPluginUnit unit)
        {
            if (unit.Source != LocalPluginSource.Local)
                throw new InvalidOperationException(
                    "Steam 工坊插件请在游戏“本地”页管理，管理器不会移动工坊联接。");
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

        private static void MoveWithoutOverwrite(string source, string target, bool isDirectory)
        {
            bool sourceExists = isDirectory ? Directory.Exists(source) : File.Exists(source);
            if (!sourceExists)
                throw new FileNotFoundException("插件不存在或已被移动。", source);
            if (IsReparsePoint(source))
                throw new InvalidDataException("拒绝移动重解析点；工坊联接必须由 Bridge 管理。");
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException("目标位置已存在同名插件，未执行覆盖: " + target);

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (isDirectory) Directory.Move(source, target);
            else File.Move(source, target);
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
