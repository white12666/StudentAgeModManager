using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace StudentAge.WorkshopBridge
{
    /// <summary>
    /// BepInEx 5 preloader patcher entry point. It does not patch a managed assembly;
    /// Initialize only prepares workshop directory junctions before Chainloader scans plugins.
    /// </summary>
    public static class WorkshopBridgePatcher
    {
        public static IEnumerable<string> TargetDLLs
        {
            get { return Array.Empty<string>(); }
        }

        public static void Initialize()
        {
            string gameRoot = null;
            try
            {
                gameRoot = LocateGameRoot();
                var options = BridgeOptions.ForGame(gameRoot);
                var result = WorkshopBridgeSynchronizer.Synchronize(options);
                WriteLog(gameRoot, result);
            }
            catch (Exception ex)
            {
                // A bridge failure must never prevent the game itself from starting.
                try
                {
                    WriteFatalLog(gameRoot, ex);
                }
                catch
                {
                    // ignored deliberately
                }
            }
        }

        // Required by the BepInEx 5 patcher discovery convention.
        public static void Patch(ref AssemblyDefinition assembly)
        {
        }

        public static void Finish()
        {
        }

        private static string LocateGameRoot()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            var patchersDirectory = Path.GetDirectoryName(location);
            var bepinExDirectory = Directory.GetParent(patchersDirectory ?? string.Empty);
            var gameDirectory = bepinExDirectory == null ? null : bepinExDirectory.Parent;
            if (gameDirectory == null)
                throw new DirectoryNotFoundException("无法从补丁位置确定游戏目录: " + location);
            return gameDirectory.FullName;
        }

        private static void WriteLog(string gameRoot, BridgeResult result)
        {
            var logPath = GetLogPath(gameRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            using (var writer = new StreamWriter(logPath, false))
            {
                writer.WriteLine("StudentAge Workshop Bridge 0.2.0");
                writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                writer.WriteLine("Synchronized: " + result.Synchronized);
                writer.WriteLine("Enabled IDs: " + result.EnabledIdCount);
                writer.WriteLine("Baseline IDs: " + result.BaselineIdCount);
                writer.WriteLine("Auto-enabled IDs: " + result.AutoEnabledIdCount);
                writer.WriteLine("Linked: " + result.LinkedCount);
                writer.WriteLine("Removed stale links: " + result.RemovedLinkCount);
                writer.WriteLine("Skipped: " + result.SkippedCount);
                writer.WriteLine("Errors: " + result.ErrorCount);
                writer.WriteLine();
                foreach (var message in result.Messages)
                    writer.WriteLine(message);
            }
        }

        private static void WriteFatalLog(string gameRoot, Exception ex)
        {
            var logPath = GetLogPath(gameRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            File.WriteAllText(logPath,
                "StudentAge Workshop Bridge failed before synchronization.\r\n" +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n\r\n" + ex);
        }

        private static string GetLogPath(string gameRoot)
        {
            var root = string.IsNullOrEmpty(gameRoot) ? AppDomain.CurrentDomain.BaseDirectory : gameRoot;
            return Path.Combine(root, "BepInEx", "WorkshopBridge.log");
        }
    }
}
