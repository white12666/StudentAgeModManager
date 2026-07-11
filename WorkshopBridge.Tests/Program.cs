using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using StudentAge.WorkshopBridge;

namespace StudentAge.WorkshopBridge.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(),
                "StudentAge.WorkshopBridge.Tests." + Guid.NewGuid().ToString("N"));
            try
            {
                Run(tempRoot);
                Console.WriteLine("All WorkshopBridge tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private static void Run(string tempRoot)
        {
            TestWorkshopLibraryDiscovery(tempRoot);
            var gameRoot = Path.Combine(tempRoot, "steamapps", "common", "StudentAge");
            var workshopRoot = Path.Combine(tempRoot, "steamapps", "workshop", "content", "1991040");
            var pluginRoot = Path.Combine(gameRoot, "BepInEx", "plugins");
            var activeList = Path.Combine(tempRoot, "userdata", "_mod");
            Directory.CreateDirectory(Path.GetDirectoryName(activeList));
            Directory.CreateDirectory(pluginRoot);

            CreateWorkshopDllMod(workshopRoot, "100", withMarker: true);
            CreateWorkshopDllMod(workshopRoot, "200", withMarker: false);
            CreateWorkshopDllMod(workshopRoot, "300", withMarker: true);
            File.WriteAllText(Path.Combine(workshopRoot, "300", BridgeOptions.DefaultMarkerFileName),
                "{\"schemaVersion\":1,\"type\":\"bepinex-plugin\",\"pluginRoot\":\"../outside\"}");
            File.WriteAllText(activeList, "100\r\n200\r\n300\r\ninvalid\r\n0\r\n100\r\n");

            var options = new BridgeOptions
            {
                GameRootPath = gameRoot,
                WorkshopRootPath = workshopRoot,
                ActiveModListPath = activeList,
                PluginRootPath = pluginRoot,
            };

            var first = WorkshopBridgeSynchronizer.Synchronize(options);
            Assert(first.Synchronized, "first synchronization should complete");
            Assert(first.EnabledIdCount == 3, "duplicate/invalid/zero IDs should be filtered");
            Assert(first.LinkedCount == 1, "only marked DLL workshop item should be linked");
            Assert(first.SkippedCount == 2, "unmarked or invalid-manifest items should be skipped");
            Assert(first.ErrorCount == 1, "unsafe pluginRoot should be rejected");
            Assert(!Directory.Exists(Path.Combine(pluginRoot, WorkshopBridgeSynchronizer.LinkDirectoryName, "300")),
                "invalid manifest must never be linked");

            var link = Path.Combine(pluginRoot, WorkshopBridgeSynchronizer.LinkDirectoryName, "100");
            Assert(Directory.Exists(link), "junction should exist");
            Assert((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
                "bridge path should be a reparse point");
            Assert(Directory.GetFiles(pluginRoot, "Probe.dll", SearchOption.AllDirectories).Any(),
                "BepInEx recursive scan should see DLL through the junction");

            // Missing/ambiguous native state must fail closed. A stale junction may
            // belong to a different Steam user and therefore cannot remain enabled.
            File.Delete(activeList);
            var missingList = WorkshopBridgeSynchronizer.Synchronize(options);
            Assert(!missingList.Synchronized, "missing active list should not report a full synchronization");
            Assert(!Directory.Exists(link), "missing active list must disable existing workshop links");
            Assert(missingList.RemovedLinkCount == 1, "fail-closed cleanup should report the removed link");

            File.WriteAllText(activeList, "100\r\n");
            var relinked = WorkshopBridgeSynchronizer.Synchronize(options);
            Assert(relinked.LinkedCount == 1 && Directory.Exists(link),
                "valid native state should recreate the workshop link");
            options.WorkshopRootPath = Path.Combine(tempRoot, "missing-workshop-root");
            var missingWorkshop = WorkshopBridgeSynchronizer.Synchronize(options);
            Assert(!missingWorkshop.Synchronized && !Directory.Exists(link),
                "an unavailable workshop root must also fail closed");
            options.WorkshopRootPath = workshopRoot;

            var nonCanonicalLink = Path.Combine(pluginRoot,
                WorkshopBridgeSynchronizer.LinkDirectoryName, "0100");
            CreateJunction(nonCanonicalLink,
                Path.Combine(workshopRoot, "100", "BepInEx", "plugins"));

            // An existing but empty list is authoritative and disables all workshop DLLs.
            File.WriteAllText(activeList, string.Empty);
            var disabled = WorkshopBridgeSynchronizer.Synchronize(options);
            Assert(disabled.Synchronized, "empty active list should synchronize");
            Assert(!Directory.Exists(link), "disabled workshop DLL link should be removed");
            Assert(Directory.Exists(nonCanonicalLink),
                "a non-canonical junction name not created by Bridge must be preserved");
            Assert(File.Exists(Path.Combine(workshopRoot, "100", "BepInEx", "plugins", "ProbeMod", "Probe.dll")),
                "removing a junction must never remove Steam source files");
            Directory.Delete(nonCanonicalLink, false);

            // Never overwrite/delete a normal directory in the bridge-owned parent.
            var conflict = Path.Combine(pluginRoot, WorkshopBridgeSynchronizer.LinkDirectoryName, "100");
            Directory.CreateDirectory(conflict);
            File.WriteAllText(Path.Combine(conflict, "keep.txt"), "do not delete");
            File.WriteAllText(activeList, "100\r\n");
            var conflictResult = WorkshopBridgeSynchronizer.Synchronize(options);
            Assert(conflictResult.ErrorCount > 0, "normal-directory conflict should be reported");
            Assert(File.Exists(Path.Combine(conflict, "keep.txt")), "normal directory must be preserved");

            // Paths passed through cmd.exe must reject shell metacharacters rather
            // than relying on quoting rules that can change across command contexts.
            var unsafeCommandPluginRoot = Path.Combine(gameRoot, "BepInEx", "plugins&unsafe");
            Directory.CreateDirectory(unsafeCommandPluginRoot);
            var unsafeCommandOptions = new BridgeOptions
            {
                GameRootPath = gameRoot,
                WorkshopRootPath = workshopRoot,
                ActiveModListPath = activeList,
                PluginRootPath = unsafeCommandPluginRoot,
            };
            var unsafeCommandResult = WorkshopBridgeSynchronizer.Synchronize(unsafeCommandOptions);
            Assert(unsafeCommandResult.Synchronized,
                "unsafe command path should be handled as an item error, not crash synchronization");
            Assert(unsafeCommandResult.ErrorCount > 0 && unsafeCommandResult.LinkedCount == 0,
                "unsafe command characters must prevent junction creation");
            Assert(!Directory.Exists(Path.Combine(unsafeCommandPluginRoot,
                    WorkshopBridgeSynchronizer.LinkDirectoryName, "100")),
                "unsafe command path must not create a bridge entry");

            // The bridge-owned root itself must be a normal directory. Otherwise cleanup
            // could mutate the target of a user-created junction outside BepInEx/plugins.
            var unsafePluginRoot = Path.Combine(gameRoot, "BepInEx", "unsafe-plugins");
            var unsafeLinkRoot = Path.Combine(unsafePluginRoot, WorkshopBridgeSynchronizer.LinkDirectoryName);
            var externalTarget = Path.Combine(tempRoot, "external-link-target");
            Directory.CreateDirectory(unsafePluginRoot);
            Directory.CreateDirectory(externalTarget);
            File.WriteAllText(Path.Combine(externalTarget, "keep.txt"), "do not touch");
            CreateJunction(unsafeLinkRoot, externalTarget);
            try
            {
                var unsafeOptions = new BridgeOptions
                {
                    GameRootPath = gameRoot,
                    WorkshopRootPath = workshopRoot,
                    ActiveModListPath = activeList,
                    PluginRootPath = unsafePluginRoot,
                };
                var unsafeResult = WorkshopBridgeSynchronizer.Synchronize(unsafeOptions);
                Assert(!unsafeResult.Synchronized, "reparse-point bridge root must abort synchronization");
                Assert(unsafeResult.ErrorCount > 0, "reparse-point bridge root should be reported");
                Assert(File.Exists(Path.Combine(externalTarget, "keep.txt")),
                    "bridge root validation must not mutate the junction target");
            }
            finally
            {
                if (Directory.Exists(unsafeLinkRoot)) Directory.Delete(unsafeLinkRoot, false);
            }
        }

        private static void CreateWorkshopDllMod(string workshopRoot, string id, bool withMarker)
        {
            var itemRoot = Path.Combine(workshopRoot, id);
            var dllDirectory = Path.Combine(itemRoot, "BepInEx", "plugins", "ProbeMod");
            Directory.CreateDirectory(dllDirectory);
            File.WriteAllText(Path.Combine(dllDirectory, "Probe.dll"), "test assembly placeholder");
            if (withMarker)
                File.WriteAllText(Path.Combine(itemRoot, BridgeOptions.DefaultMarkerFileName),
                    "{\"schemaVersion\":1,\"type\":\"bepinex-plugin\",\"pluginRoot\":\"BepInEx/plugins\"}");
        }

        private static void TestWorkshopLibraryDiscovery(string tempRoot)
        {
            var primaryLibrary = Path.Combine(tempRoot, "locator-primary");
            var secondaryLibrary = Path.Combine(tempRoot, "locator-secondary");
            var gameRoot = Path.Combine(primaryLibrary, "steamapps", "common", "StudentAge");
            var expectedWorkshopRoot = Path.Combine(secondaryLibrary, "steamapps", "workshop",
                "content", BridgeOptions.WorkshopAppId);
            Directory.CreateDirectory(gameRoot);
            Directory.CreateDirectory(expectedWorkshopRoot);

            var escapedLibraryPath = secondaryLibrary.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(primaryLibrary, "steamapps", "libraryfolders.vdf"),
                "\"libraryfolders\"\r\n{\r\n  \"1\"\r\n  {\r\n    \"path\" \"" +
                escapedLibraryPath + "\"\r\n  }\r\n}\r\n");

            var located = BridgeOptions.ForGame(gameRoot).WorkshopRootPath;
            Assert(string.Equals(Path.GetFullPath(expectedWorkshopRoot), Path.GetFullPath(located),
                StringComparison.OrdinalIgnoreCase),
                "libraryfolders.vdf should locate workshop content in another Steam library");
        }


        private static void CreateJunction(string junctionPath, string targetPath)
        {
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandInterpreter))
                commandInterpreter = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = commandInterpreter,
                Arguments = "/D /C mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(startInfo))
            {
                if (process == null) throw new InvalidOperationException("Could not start mklink.");
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("mklink failed: " + stderr + " " + stdout);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }
    }
}
