using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using StudentAge.WorkshopBridge;
using StudentAgeModManager;
using StudentAgeModManager.Core;

namespace StudentAgeModManager.Tests
{
    internal static class Program
    {
        private const string BridgeResourceName =
            "StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll";
        private const string BepInExResourceName =
            "StudentAgeModManager.Resources.BepInEx-5.4.23-package.zip";

        [STAThread]
        private static int Main(string[] args)
        {
            string tempRoot = null;
            try
            {
                if (args != null && args.Length > 0)
                    return RunCommandWithHandling(args, null, true);

                tempRoot = Path.Combine(Path.GetTempPath(),
                    "StudentAgeModManager.Tests." + Guid.NewGuid().ToString("N"));
                Run(tempRoot);
                Console.WriteLine("All ModManager integration tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                if (tempRoot != null)
                    try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private static int RunCommandWithHandling(string[] args,
            IWorkshopMetadataProvider workshopProvider, bool reportErrors)
        {
            try
            {
                return RunCommand(args, workshopProvider);
            }
            catch (Exception ex)
            {
                if (reportErrors) Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static int RunCommand(string[] args,
            IWorkshopMetadataProvider workshopProvider)
        {
            bool verifyWorkshop = args.Length == 3 &&
                string.Equals(args[2], "--verify-workshop", StringComparison.Ordinal);
            if ((args.Length != 2 && !verifyWorkshop) ||
                !string.Equals(args[0], "--validate-index", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "Usage: StudentAgeModManager.Tests --validate-index <path> [--verify-workshop]");
                return 2;
            }

            string path = Path.GetFullPath(args[1]);
            ModIndex index = IndexClient.ParseAndValidate(File.ReadAllText(path));
            Console.WriteLine("Index validation passed: " + path + " (" +
                index.mods.Count + " mods).");
            if (verifyWorkshop)
            {
                var service = new WorkshopMetadataService(
                    workshopProvider ?? (IWorkshopMetadataProvider)
                        new SteamWorkshopMetadataProvider());
                int verified = service.VerifyIndexAsync(index).GetAwaiter().GetResult();
                Console.WriteLine("Steam Workshop verification passed: " + verified +
                    " workshop items.");
            }
            return 0;
        }

        private static void Run(string tempRoot)
        {
            var managerVersion = FileVersionInfo.GetVersionInfo(typeof(MainForm).Assembly.Location);
            Assert(managerVersion.ProductVersion == "1.3.4" &&
                   !managerVersion.ProductVersion.Contains("+"),
                "release manager metadata should expose the exact public version without a stale Git suffix");

            // Run blocking async tests before WinForms installs its synchronization context.
            RunWorkshopRelaunchTests(Path.Combine(tempRoot, "workshop-relaunch"));
            RunInstallerTransactionTests(Path.Combine(tempRoot, "installer-transactions"));

            RunWorkshopReferenceTests();
            RunWorkshopPageLauncherTests();
            RunIndexValidationTests();
            RunWorkshopMetadataTests(Path.Combine(tempRoot, "workshop-metadata"));
            RunMainFormUiTests(Path.Combine(tempRoot, "main-form-ui"));
            RunWheelFlowLayoutPanelTests();
            RunModCardUiTests();
            RunLocalPluginScannerTests(Path.Combine(tempRoot, "local-plugins"));


            var offlineRoot = Path.Combine(tempRoot, "offline-install");
            Directory.CreateDirectory(offlineRoot);
            var offlineInstaller = new ModInstaller(new LocalState(offlineRoot));
            var installProgress = new List<int>();
            offlineInstaller.InstallBepInExAsync((percent, source) =>
            {
                installProgress.Add(percent);
                Assert(source.Contains("内置 BepInEx"),
                    "offline installation progress must identify the embedded source");
            }).GetAwaiter().GetResult();
            Assert(offlineInstaller.IsBepInExInstalled() &&
                   File.Exists(Path.Combine(offlineRoot, "BepInEx", "core", "BepInEx.dll")),
                "an empty game directory should receive the complete embedded BepInEx package");
            Assert(offlineInstaller.IsWorkshopBridgeCurrent(),
                "offline complete installation should also deploy the current embedded Bridge");
            Assert(installProgress.Count > 2 && installProgress[0] == 0 &&
                   installProgress[installProgress.Count - 1] == 100,
                "offline installation should report bounded progress from zero to completion");
            Assert(HashEmbeddedBepInExPackage() ==
                   ModInstaller.EmbeddedBepInExPackageSha256,
                "the embedded BepInEx archive must match the source-pinned SHA-256");
            Assert(typeof(ModInstaller).GetFields(BindingFlags.Instance |
                       BindingFlags.NonPublic | BindingFlags.Public)
                       .All(field => field.FieldType != typeof(Downloader)),
                "the prerequisite installer must not retain a network downloader");
            Assert(typeof(Downloader).GetMethod("DownloadFileAsync") == null,
                "the index text downloader must not expose a mirrored binary download path");

            var installer = offlineInstaller;
            Assert(installer.IsBepInExInstalled(), "completed embedded install should be detected");

            Assert(installer.IsWorkshopBridgeInstalled(), "bridge should be installed with the package");
            var bridgeVersion = FileVersionInfo.GetVersionInfo(installer.WorkshopBridgePath);
            Assert(bridgeVersion.ProductVersion == "0.4.0" &&
                   !bridgeVersion.ProductVersion.Contains("+"),
                "embedded Bridge product version must not include a Git revision; unrelated " +
                "manager or index commits must not change its exact-hash identity");
            Assert(installer.IsWorkshopBridgeCurrent(), "freshly extracted bridge should be current");
            Assert(HashEmbeddedBridge() == HashFile(installer.WorkshopBridgePath),
                "extracted bridge must exactly match the embedded resource");

            File.AppendAllText(installer.WorkshopBridgePath, "corrupt");
            Assert(!installer.IsWorkshopBridgeCurrent(), "modified bridge should be reported as stale");

            installer.InstallWorkshopBridge();
            Assert(installer.IsWorkshopBridgeCurrent(), "repair should restore the embedded bridge");
            Assert(Directory.GetFiles(Path.GetDirectoryName(installer.WorkshopBridgePath),
                       "*.tmp").Length == 0,
                "unique Bridge commit temporaries should be cleaned up");
        }
        private static void RunInstallerTransactionTests(string root)
        {
            Directory.CreateDirectory(root);
            string legacyRoot = Path.Combine(root, "legacy-third-party");
            string legacyCore = Path.Combine(legacyRoot, "BepInEx", "core", "BepInEx.dll");
            string legacyProxy = Path.Combine(legacyRoot, "winhttp.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyCore));
            byte[] legacyCoreBytes = { 1, 9, 8, 4 };
            byte[] legacyProxyBytes = { 2, 7, 3 };
            File.WriteAllBytes(legacyCore, legacyCoreBytes);
            File.WriteAllBytes(legacyProxy, legacyProxyBytes);
            var legacyInstaller = new ModInstaller(new LocalState(legacyRoot));
            Assert(legacyInstaller.IsBepInExInstalled() &&
                   !legacyInstaller.IsEmbeddedBepInExPackageCurrent(),
                "valid unmarked legacy or third-party BepInEx must remain installed but unverified");
            legacyInstaller.InstallWorkshopBridge();
            Assert(legacyInstaller.IsWorkshopBridgeCurrent() &&
                   File.ReadAllBytes(legacyCore).SequenceEqual(legacyCoreBytes) &&
                   File.ReadAllBytes(legacyProxy).SequenceEqual(legacyProxyBytes),
                "legacy upgrade must take the Bridge-only path without replacing BepInEx bytes");

            string repairRoot = Path.Combine(root, "marker-repair");
            var repairInstaller = new ModInstaller(new LocalState(repairRoot));
            repairInstaller.InstallBepInExAsync(null).GetAwaiter().GetResult();
            string marker = Path.Combine(repairRoot, "BepInEx", "ModManager",
                "bepinex-install.complete");
            File.Delete(marker);
            Assert(repairInstaller.IsBepInExInstalled() &&
                   !repairInstaller.IsEmbeddedBepInExPackageCurrent(),
                "an unmarked but structurally valid tree must remain usable as a legacy install " +
                "without being claimed as manager-verified");
            repairInstaller.InstallBepInExAsync(null).GetAwaiter().GetResult();
            Assert(repairInstaller.IsBepInExInstalled() &&
                   repairInstaller.IsEmbeddedBepInExPackageCurrent(),
                "rerunning installation must deterministically restore the exact package marker");
            string[] validMarker = File.ReadAllLines(marker);
            Assert(validMarker.Length > 3 &&
                   validMarker[0] == "StudentAgeModManager.BepInExComplete|1" &&
                   validMarker[1] == "PackageSha256|" +
                       ModInstaller.EmbeddedBepInExPackageSha256 &&
                   validMarker[2] == "FileCount|" + (validMarker.Length - 3),
                "completion marker must identify the package and exact manifest count");
            File.WriteAllLines(marker, validMarker.Take(validMarker.Length - 1).ToArray());
            Assert(!repairInstaller.IsBepInExInstalled() &&
                   !repairInstaller.IsEmbeddedBepInExPackageCurrent(),
                "a truncated marker subset must never validate as a complete install");
            File.WriteAllLines(marker, validMarker);
            Assert(repairInstaller.IsEmbeddedBepInExPackageCurrent(),
                "restoring the exact marker entry set must restore verified state");
            string leaseRoot = Path.Combine(root, "lease-contention");
            var leaseInstaller = new ModInstaller(new LocalState(leaseRoot));
            Exception leaseError = null;
            var installStarted = new ManualResetEvent(false);
            var installFinished = new ManualResetEvent(false);
            var installThread = new Thread(() =>
            {
                try
                {
                    installStarted.Set();
                    leaseInstaller.InstallBepInExAsync(null).GetAwaiter().GetResult();
                }
                catch (Exception ex) { leaseError = ex; }
                finally { installFinished.Set(); }
            }) { IsBackground = true };
            using (WorkshopBridgeTransaction.Acquire(leaseRoot))
            {
                installThread.Start();
                Assert(installStarted.WaitOne(TimeSpan.FromSeconds(5)) &&
                       !installFinished.WaitOne(TimeSpan.FromMilliseconds(250)),
                    "BepInEx live commit must contend on the shared game transaction");
            }
            Assert(installFinished.WaitOne(TimeSpan.FromSeconds(20)) &&
                   installThread.Join(TimeSpan.FromSeconds(1)) && leaseError == null &&
                   leaseInstaller.IsBepInExInstalled(),
                "BepInEx install must resume and commit after the game transaction is released");
            string failureRoot = Path.Combine(root, "precommit-failure");
            Directory.CreateDirectory(failureRoot);
            string oldProxy = Path.Combine(failureRoot, "winhttp.dll");
            byte[] oldBytes = { 7, 6, 5, 4 };
            File.WriteAllBytes(oldProxy, oldBytes);
            var failureInstaller = new ModInstaller(new LocalState(failureRoot));
            SetPrivateField(failureInstaller, "_beforeAtomicCommit", (Action<string>)(path =>
            {
                if (string.Equals(path, oldProxy, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("injected before replacement");
            }));
            AssertThrows<IOException>(() => failureInstaller.InstallBepInExAsync(null)
                    .GetAwaiter().GetResult(),
                "failure immediately before atomic replacement must surface");
            string incompleteMarker = Path.Combine(failureRoot, "BepInEx", "ModManager",
                "bepinex-install.incomplete");
            Assert(File.ReadAllBytes(oldProxy).SequenceEqual(oldBytes) &&
                   File.Exists(incompleteMarker) && !failureInstaller.IsBepInExInstalled(),
                "pre-commit failure must preserve old bytes and leave an interruption record");
            string markerDeleteRoot = Path.Combine(root, "marker-delete-failure");
            var markerDeleteInstaller = new ModInstaller(new LocalState(markerDeleteRoot));
            string markerDeleteIncomplete = Path.Combine(markerDeleteRoot, "BepInEx", "ModManager",
                "bepinex-install.incomplete");
            string markerDeleteComplete = Path.Combine(markerDeleteRoot, "BepInEx", "ModManager",
                "bepinex-install.complete");
            FileStream lockedIncomplete = null;
            var deletionProgress = new List<int>();
            SetPrivateField(markerDeleteInstaller, "_beforeAtomicCommit", (Action<string>)(path =>
            {
                if (string.Equals(path, markerDeleteComplete, StringComparison.OrdinalIgnoreCase))
                    lockedIncomplete = new FileStream(markerDeleteIncomplete, FileMode.Open,
                        FileAccess.Read, FileShare.None);
            }));
            try
            {
                AssertThrows<IOException>(() => markerDeleteInstaller.InstallBepInExAsync(
                        (percent, source) => deletionProgress.Add(percent)).GetAwaiter().GetResult(),
                    "failure to remove the interruption marker must make installation fail");
            }
            finally
            {
                if (lockedIncomplete != null) lockedIncomplete.Dispose();
            }
            Assert(!markerDeleteInstaller.IsBepInExInstalled() &&
                   deletionProgress.All(percent => percent != 100),
                "an uncleared interruption marker must never report installed or 100 percent");

            string concurrentRoot = Path.Combine(root, "concurrent-bridge");
            var initial = new ModInstaller(new LocalState(concurrentRoot));
            initial.InstallBepInExAsync(null).GetAwaiter().GetResult();
            File.AppendAllText(initial.WorkshopBridgePath, "stale");
            Exception firstError = null;
            Exception secondError = null;
            var firstHeld = new ManualResetEvent(false);
            var releaseFirst = new ManualResetEvent(false);
            var secondCommit = new ManualResetEvent(false);
            var firstInstaller = new ModInstaller(new LocalState(concurrentRoot));
            var secondInstaller = new ModInstaller(new LocalState(concurrentRoot));
            SetPrivateField(firstInstaller, "_beforeAtomicCommit", (Action<string>)(path =>
            {
                if (!string.Equals(path, firstInstaller.WorkshopBridgePath,
                    StringComparison.OrdinalIgnoreCase)) return;
                firstHeld.Set();
                if (!releaseFirst.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("test did not release first Bridge commit");
            }));
            SetPrivateField(secondInstaller, "_beforeAtomicCommit", (Action<string>)(path =>
            {
                if (string.Equals(path, secondInstaller.WorkshopBridgePath,
                    StringComparison.OrdinalIgnoreCase)) secondCommit.Set();
            }));
            var first = new Thread(() =>
            {
                try { firstInstaller.InstallWorkshopBridge(); }
                catch (Exception ex) { firstError = ex; }
            }) { IsBackground = true };
            var second = new Thread(() =>
            {
                try { secondInstaller.InstallWorkshopBridge(); }
                catch (Exception ex) { secondError = ex; }
            }) { IsBackground = true };
            try
            {
                first.Start();
                Assert(firstHeld.WaitOne(TimeSpan.FromSeconds(5)),
                    "first Bridge repair must reach the atomic commit while holding the game lease");
                second.Start();
                Assert(!secondCommit.WaitOne(TimeSpan.FromMilliseconds(250)),
                    "a concurrent Bridge repair must block before creating or committing its live temp");
            }
            finally
            {
                releaseFirst.Set();
            }
            Assert(first.Join(TimeSpan.FromSeconds(10)) && second.Join(TimeSpan.FromSeconds(10)),
                "concurrent Bridge repair threads must finish within a bounded time");
            Assert(secondCommit.WaitOne(0) && firstError == null && secondError == null &&
                   initial.IsWorkshopBridgeCurrent(),
                "concurrent Bridge repair must serialize and verify each committed invocation");
            Assert(Directory.GetFiles(Path.GetDirectoryName(initial.WorkshopBridgePath),
                       "*.tmp").Length == 0,
                "concurrent Bridge repair must clean each invocation's unique temporary");
            firstHeld.Dispose();
            releaseFirst.Dispose();
            secondCommit.Dispose();
        }
        private static void RunWorkshopRelaunchTests(string root)
        {
            string managerAssemblyPath = typeof(MainForm).Assembly.Location;
            Type programType = typeof(MainForm).Assembly.GetType("StudentAgeModManager.Program", true);
            MethodInfo isWorkshopPath = programType.GetMethod("IsStudentAgeWorkshopItemPath",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert(isWorkshopPath != null,
                "manager should expose its Workshop launch-path classifier for regression coverage");

            string workshopExe = Path.Combine(root, "steamapps", "workshop", "content",
                "1991040", "123456789", "ModManager.exe");
            string ordinaryExe = Path.Combine(root, "Downloads", "ModManager.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(workshopExe));
            Directory.CreateDirectory(Path.GetDirectoryName(ordinaryExe));
            File.Copy(managerAssemblyPath, workshopExe);
            File.Copy(managerAssemblyPath, ordinaryExe);

            Assert((bool)isWorkshopPath.Invoke(null, new object[] { workshopExe }),
                "an executable in steamapps/workshop/content/1991040/<id> must relaunch outside Workshop");
            Assert(!(bool)isWorkshopPath.Invoke(null, new object[] { ordinaryExe }),
                "an ordinary downloaded executable must continue running in place");
            Assert(!(bool)isWorkshopPath.Invoke(null, new object[]
                { Path.Combine(root, "steamapps", "workshop", "content", "1991040", "not-an-id", "ModManager.exe") }),
                "a lookalike path without a canonical numeric Workshop ID must not trigger relocation");
            Assert(!(bool)isWorkshopPath.Invoke(null, new object[]
                { Path.Combine(root, "steamapps", "workshop", "content", "999", "123", "ModManager.exe") }),
                "another game's Workshop item must not trigger StudentAge relocation");
            MethodInfo relaunch = programType.GetMethod("RelaunchWorkshopExecutable",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert(relaunch != null,
                "manager should expose the serialized Workshop shadow-launch boundary");
            string runtimeRoot = Path.Combine(root, "runtime");
            var firstAtStart = new ManualResetEvent(false);
            var releaseFirst = new ManualResetEvent(false);
            var secondAtStart = new ManualResetEvent(false);
            string firstChild = null;
            Action<string> firstCallback = path =>
            {
                firstChild = path;
                firstAtStart.Set();
                if (!releaseFirst.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("test did not release first shadow launch");
                throw new IOException("stop before actual process creation");
            };
            Action<string> secondCallback = path =>
            {
                secondAtStart.Set();
                throw new IOException("stop before actual process creation");
            };
            var firstLaunch = new Thread(() =>
            {
                try { relaunch.Invoke(null, new object[]
                    { workshopExe, new[] { "first argument" }, runtimeRoot, firstCallback }); }
                catch (TargetInvocationException) { }
            }) { IsBackground = true };
            var secondLaunch = new Thread(() =>
            {
                try { relaunch.Invoke(null, new object[]
                    { workshopExe, new[] { "second" }, runtimeRoot, secondCallback }); }
                catch (TargetInvocationException) { }
            }) { IsBackground = true };
            try
            {
                firstLaunch.Start();
                Assert(firstAtStart.WaitOne(TimeSpan.FromSeconds(5)) && File.Exists(firstChild),
                    "the copied shadow child must exist immediately through the Process.Start boundary");
                secondLaunch.Start();
                Assert(!secondAtStart.WaitOne(TimeSpan.FromMilliseconds(250)) && File.Exists(firstChild),
                    "a concurrent launcher must not clean another launch's not-yet-started child");
            }
            finally
            {
                releaseFirst.Set();
            }
            Assert(firstLaunch.Join(TimeSpan.FromSeconds(10)) &&
                   secondLaunch.Join(TimeSpan.FromSeconds(10)),
                "concurrent shadow-launch threads must finish within a bounded time");
            Assert(secondAtStart.WaitOne(0),
                "the waiting launcher must proceed after the launch mutex is released");
            firstAtStart.Dispose();
            releaseFirst.Dispose();
            secondAtStart.Dispose();
        }

        private static void RunWorkshopReferenceTests()
        {
            string workshopId;
            Assert(!WorkshopItem.TryGetId(null, out workshopId) && workshopId == null,
                "null entries must be rejected without leaving an unassigned ID");

            var legacyNull = new ModEntry { workshopId = null };
            var legacyEmpty = new ModEntry { workshopId = string.Empty };
            Assert(!WorkshopItem.IsDeclared(legacyNull) && !WorkshopItem.IsDeclared(legacyEmpty),
                "null and empty workshop IDs are undeclared and must be rejected by index validation");

            AssertWorkshopReference("1234", "1234");
            AssertWorkshopReference("  001234  ", "1234");
            AssertWorkshopReference(
                "https://steamcommunity.com/sharedfiles/filedetails/?id=1234", "1234");
            AssertWorkshopReference(
                "https://steamcommunity.com/sharedfiles/filedetails?id=0001234", "1234");
            AssertWorkshopReference(
                "https://steamcommunity.com/workshop/filedetails/?id=1234", "1234");
            AssertWorkshopReference(
                "https://steamcommunity.com/workshop/filedetails?id=1234", "1234");
            AssertWorkshopReference(
                "https://steamcommunity.com/sharedfiles/filedetails/?source=author&id=0001234&searchtext=hello%20world",
                "1234");
            AssertWorkshopReference(
                "https://steamcommunity.com/sharedfiles/filedetails/?id=%31%32%33", "123");
            AssertWorkshopReference(
                "HTTPS://STEAMCOMMUNITY.COM:443/sharedfiles/filedetails/?id=1234", "1234");
            AssertWorkshopReference("18446744073709551615", "18446744073709551615");

            string[] invalidReferences =
            {
                "   ",
                "../plugin.dll",
                "0",
                "0000",
                "-1",
                "+1",
                "１２３",
                "18446744073709551616",
                "http://steamcommunity.com/sharedfiles/filedetails/?id=1234",
                "https://example.com/sharedfiles/filedetails/?id=1234",
                "https://steamcommunity.com.evil.example/sharedfiles/filedetails/?id=1234",
                "https://evil.steamcommunity.com/sharedfiles/filedetails/?id=1234",
                "https://steamcommunity.com./sharedfiles/filedetails/?id=1234",
                "https://steamcommunity。com/sharedfiles/filedetails/?id=1234",
                "https://user@steamcommunity.com/sharedfiles/filedetails/?id=1234",
                "https://steamcommunity.com:444/sharedfiles/filedetails/?id=1234",
                "https://steamcommunity.com/sharedfiles/other/?id=1234",
                "https://steamcommunity.com/SharedFiles/filedetails/?id=1234",
                "https://steamcommunity.com/sharedfiles/x/../filedetails/?id=1234",
                "https://steamcommunity.com/sharedfiles\\filedetails/?id=1234",
                "https://steamcommunity.com/sharedfiles/%66iledetails/?id=1234",
                "https://steamcommunity.com/sharedfiles/filedetails/",
                "https://steamcommunity.com/sharedfiles/filedetails/?source=author",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=1234&id=5678",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=1234&%69d=5678",
                "https://steamcommunity.com/sharedfiles/filedetails/?ID=1234",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=0",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=abc",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=18446744073709551616",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=1234#comments",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=12\n34",
                "https://steamcommunity.com/sharedfiles/filedetails/?id=1234&bad=%ZZ",
                "steam://url/CommunityFilePage/1234",
            };
            foreach (string reference in invalidReferences)
                AssertInvalidWorkshopReference(reference);
            AssertInvalidWorkshopReference(new string('1', 2049));

            Assert(WorkshopItem.PageUrl("1234") ==
                   "https://steamcommunity.com/sharedfiles/filedetails/?id=1234",
                "trusted Workshop URLs must be constructed from only the canonical numeric ID");
            Assert(WorkshopItem.SteamClientUrl("1234") ==
                   "steam://url/CommunityFilePage/1234",
                "trusted Steam client URLs must be constructed from only the canonical numeric ID");
            AssertPageUrlRejected("001234");
            AssertPageUrlRejected("0");
            AssertPageUrlRejected("https://steamcommunity.com/sharedfiles/filedetails/?id=1234");
        }

        private static void RunWorkshopPageLauncherTests()
        {
            var opened = new List<string>();
            var steamLauncher = new WorkshopPageLauncher(() => true, opened.Add);
            WorkshopPageTarget target = steamLauncher.Open("1234");
            Assert(target == WorkshopPageTarget.SteamClient && opened.Count == 1 &&
                   opened[0] == "steam://url/CommunityFilePage/1234",
                "a running Steam client should receive the canonical Steam Workshop URI");

            opened.Clear();
            var browserLauncher = new WorkshopPageLauncher(() => false, opened.Add);
            target = browserLauncher.Open("1234");
            Assert(target == WorkshopPageTarget.WebBrowser && opened.Count == 1 &&
                   opened[0] == "https://steamcommunity.com/sharedfiles/filedetails/?id=1234",
                "an inactive Steam client should fall back to the canonical HTTPS page");

            opened.Clear();
            var fallbackLauncher = new WorkshopPageLauncher(() => true, url =>
            {
                opened.Add(url);
                if (url.StartsWith("steam:", StringComparison.Ordinal))
                    throw new InvalidOperationException("broken Steam protocol association");
            });
            target = fallbackLauncher.Open("1234");
            Assert(target == WorkshopPageTarget.WebBrowser && opened.SequenceEqual(new[]
                {
                    "steam://url/CommunityFilePage/1234",
                    "https://steamcommunity.com/sharedfiles/filedetails/?id=1234",
                }), "a failed Steam protocol launch should retry in the browser");

            opened.Clear();
            var detectionFallback = new WorkshopPageLauncher(
                () => throw new InvalidOperationException("process query failed"), opened.Add);
            target = detectionFallback.Open("1234");
            Assert(target == WorkshopPageTarget.WebBrowser && opened.Count == 1 &&
                   opened[0].StartsWith("https://", StringComparison.Ordinal),
                "a Steam process detection failure should safely use the browser");
        }

        private static void RunIndexValidationTests()
        {
            Assert(IndexClient.DefaultIndexUrl.EndsWith("/main/mods.json",
                    StringComparison.Ordinal),
                "stable-channel builds must read the main index");
            const string validIndex =
                "{\"schemaVersion\":1,\"bepinex\":{" +
                "\"version\":\"5.4.23\",\"downloadUrl\":\"https://github.com/legacy.zip\"}," +
                "\"mods\":[" +
                "{\"id\":\"Numeric\",\"workshopId\":\" 000123 \"}," +
                "{\"id\":\"Url\",\"workshopId\":\"https://steamcommunity.com/workshop/filedetails/?id=000456&source=pr\"}]}";
            ModIndex index = IndexClient.ParseAndValidate(validIndex);
            Assert(index.mods.Count == 2, "a valid index should keep every Workshop entry");
            Assert(index.mods[0].workshopId == "123" && index.mods[1].workshopId == "456",
                "index validation must normalize numeric and URL workshop references in memory");
            Assert(index.bepinex != null && index.bepinex.version == "5.4.23" &&
                   index.bepinex.downloadUrl == "https://github.com/legacy.zip",
                "the legacy BepInEx URL must remain parseable for 1.1.0 index compatibility");

            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"Legacy\",\"downloadUrl\":\"https://example.invalid/plugin.dll\"}]}",
                "mods[0]", "workshopId", "不再支持");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"LegacyEmpty\",\"workshopId\":\"\"}]}",
                "mods[0]", "workshopId", "不再支持");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"LegacyNull\",\"workshopId\":null}]}",
                "mods[0]", "workshopId", "不再支持");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[" +
                "{\"id\":\"Example\",\"workshopId\":\"1\"}," +
                "{\"id\":\"example\",\"workshopId\":\"2\"}]}",
                "mods[1]", "mods[0]", "重复");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[" +
                "{\"id\":\"Numeric\",\"workshopId\":\"00123\"}," +
                "{\"id\":\"Url\",\"workshopId\":\"https://steamcommunity.com/sharedfiles/filedetails/?id=123\"}]}",
                "mods[1]", "mods[0]", "Workshop ID 123");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"BadWorkshop\",\"workshopId\":\"   \"}]}",
                "mods[0]", "BadWorkshop", "workshopId");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"Good\",\"workshopId\":\"1\"},null]}",
                "mods[1]", "null");
            AssertInvalidIndex("{\"schemaVersion\":2,\"mods\":[]}", "schemaVersion");
            AssertInvalidIndex("{\"schemaVersion\":1}", "mods");
            AssertInvalidIndex("null", "有效对象");
            AssertInvalidIndex("{not-json", "JSON");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"\"}]}",
                "mods[0]", "id");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":123}]}",
                "mods[0]", "id", "JSON 字符串");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"ID\":123}]}",
                "mods[0]", "id", "JSON 字符串");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"NumericToken\",\"workshopId\":123}]}",
                "mods[0]", "workshopId", "JSON 字符串");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"Exponent\",\"workshopId\":1e3}]}",
                "mods[0]", "workshopId", "JSON 字符串");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"ExponentAlias\",\"WorkshopId\":1e3}]}",
                "mods[0]", "workshopId", "JSON 字符串");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"NumericName\",\"name\":123}]}",
                "mods[0]", "name", "JSON 字符串");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"NumericDescription\",\"Description\":false}]}",
                "mods[0]", "description", "JSON 字符串");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"DuplicateNameField\",\"name\":\"A\",\"Name\":\"B\"}]}",
                "mods[0]", "name", "仅大小写不同", "重复字段");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"DuplicateDescriptionField\"," +
                "\"description\":\"A\",\"Description\":\"B\"}]}",
                "mods[0]", "description", "仅大小写不同", "重复字段");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[],\"Mods\":[]}",
                "mods", "仅大小写不同", "重复字段");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"First\",\"ID\":\"Second\"}]}",
                "mods[0]", "id", "仅大小写不同", "重复字段");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"DuplicateWorkshopField\",\"workshopId\":\"123\",\"WorkshopId\":\"456\"}]}",
                "mods[0]", "workshopId", "仅大小写不同", "重复字段");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\" padded \"}]}",
                "mods[0]", "首尾空白");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"bad\\u0001id\"}]}",
                "mods[0]", "控制字符");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"" +
                new string('a', 129) + "\"}]}",
                "mods[0]", "128");
            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[" +
                "{\"id\":\"Good\",\"workshopId\":\"123\"}," +
                "{\"id\":\"Bad\",\"workshopId\":\"https://evil.example/?id=456\"}," +
                "{\"id\":\"NeverReached\",\"workshopId\":\"789\"}]}",
                "mods[1]", "Bad", "workshopId");

            string overridePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "index_url.txt");
            bool hadOverride = File.Exists(overridePath);
            string previousOverride = hadOverride ? File.ReadAllText(overridePath) : null;
            string overrideVariable = "STUDENTAGE_MODMANAGER_ENABLE_INDEX_OVERRIDE";
            string previousVariable = Environment.GetEnvironmentVariable(overrideVariable);
            var overrideMethod = typeof(IndexClient).GetMethod("OverrideIndexUrl",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert(overrideMethod != null, "IndexClient should keep its guarded development override");
            try
            {
                File.WriteAllText(overridePath, "https://example.invalid/test/mods.json");
                Environment.SetEnvironmentVariable(overrideVariable, null);
                Assert(overrideMethod.Invoke(null, null) == null,
                    "a stray index_url.txt beside a release executable must never replace main");

                Environment.SetEnvironmentVariable(overrideVariable, "1");
                Assert(string.Equals(overrideMethod.Invoke(null, null) as string,
                        "https://example.invalid/test/mods.json", StringComparison.Ordinal),
                    "developers should retain an explicit opt-in index override");
            }
            finally
            {
                Environment.SetEnvironmentVariable(overrideVariable, previousVariable);
                if (hadOverride)
                    File.WriteAllText(overridePath, previousOverride);
                else if (File.Exists(overridePath))
                    File.Delete(overridePath);
            }
        }

        private static void AssertWorkshopReference(string reference, string expectedId)
        {
            var entry = new ModEntry { id = "reference-test", workshopId = reference };
            string actualId;
            Assert(WorkshopItem.IsDeclared(entry),
                "non-empty workshop references must select the Workshop flow: " + reference);
            Assert(WorkshopItem.TryGetId(entry, out actualId) && actualId == expectedId,
                "valid workshop reference should normalize to " + expectedId + ": " + reference);
            Assert(WorkshopItem.PageUrl(actualId) ==
                   "https://steamcommunity.com/sharedfiles/filedetails/?id=" + expectedId,
                "the index-provided reference must never be opened directly: " + reference);
            Assert(WorkshopItem.SteamClientUrl(actualId) ==
                   "steam://url/CommunityFilePage/" + expectedId,
                "the Steam URI must be rebuilt from the normalized numeric ID: " + reference);
        }

        private static void AssertInvalidWorkshopReference(string reference)
        {
            var entry = new ModEntry { id = "invalid-reference", workshopId = reference };
            string ignored;
            Assert(WorkshopItem.IsDeclared(entry) && !WorkshopItem.TryGetId(entry, out ignored),
                "invalid declared workshop reference must remain blocked: " + reference);
        }

        private static void AssertPageUrlRejected(string value)
        {
            foreach (Func<string, string> buildUrl in new Func<string, string>[]
                { WorkshopItem.PageUrl, WorkshopItem.SteamClientUrl })
            {
                bool rejected = false;
                try
                {
                    buildUrl(value);
                }
                catch (ArgumentException)
                {
                    rejected = true;
                }
                Assert(rejected, "Workshop page URL builders must reject non-canonical input: " +
                    value);
            }
        }

        private static void AssertInvalidIndex(string json, params string[] expectedFragments)
        {
            bool rejected = false;
            try
            {
                IndexClient.ParseAndValidate(json);
            }
            catch (InvalidDataException ex)
            {
                rejected = true;
                foreach (string fragment in expectedFragments)
                    Assert(ex.Message.Contains(fragment),
                        "index rejection should mention '" + fragment + "': " + ex.Message);
            }
            Assert(rejected, "invalid index must be rejected as a whole");
        }

        private static void RunWorkshopMetadataTests(string root)
        {
            Directory.CreateDirectory(root);
            RunWorkshopMetadataTextAndResponseTests();
            RunWorkshopMetadataProviderTests();
            RunWorkshopMetadataServiceTests(Path.Combine(root, "service"));
            RunWorkshopMetadataVerificationTests(Path.Combine(root, "verification"));
            RunIndexClientMetadataIntegrationTests(Path.Combine(root, "index-client"));
            RunWorkshopMetadataCliTests(Path.Combine(root, "cli"));
        }

        private static void RunWorkshopMetadataTextAndResponseTests()
        {
            string emoji = char.ConvertFromUtf32(0x1F600);
            Assert(WorkshopMetadataText.CleanTitle(
                       "  [b]A&nbsp;&amp; B[/b]\r\nC\u0001 " + emoji + "  ") ==
                   "A & B C " + emoji,
                "Steam titles should decode HTML, strip BBCode, controls, and extra whitespace");
            Assert(WorkshopMetadataText.CleanDescription(
                       "[url=https://evil.invalid/]Line&nbsp;one[/url]\r\n\tLine\u0002  two") ==
                   "Line one Line two",
                "Steam descriptions should retain only normalized plain display text");
            Assert(WorkshopMetadataText.CleanTitle("A\uD83DB\uDC00C") == "ABC",
                "unpaired UTF-16 surrogates should be removed from Steam display text");
            Assert(WorkshopMetadataText.CleanTitle("A" + emoji + "B") ==
                   "A" + emoji + "B",
                "valid emoji surrogate pairs should remain intact");
            string supplementaryFormat = char.ConvertFromUtf32(0xE0001);
            Assert(WorkshopMetadataText.CleanTitle("A" + supplementaryFormat + "B") == "A B",
                "supplementary-plane Unicode format characters should be removed");

            string longTitle = WorkshopMetadataText.CleanTitle(new string('x', 200));
            Assert(longTitle.Length == WorkshopMetadataText.MaxTitleLength &&
                   longTitle.EndsWith("…", StringComparison.Ordinal),
                "Steam titles should be truncated to the documented display limit");
            string longDescription = WorkshopMetadataText.CleanDescription(new string('y', 400));
            Assert(longDescription.Length == WorkshopMetadataText.MaxDescriptionLength &&
                   longDescription.EndsWith("…", StringComparison.Ordinal),
                "Steam descriptions should be truncated to the documented display limit");
            Assert(!HasUnpairedSurrogate(WorkshopMetadataText.CleanTitle(
                    new string('z', WorkshopMetadataText.MaxTitleLength - 2) + emoji + "tail")),
                "length limiting must not split an emoji surrogate pair");

            const string id = "123";
            string response = BuildSteamResponseFromDetails(
                "{\"publishedfileid\":\"123\",\"result\":1," +
                "\"consumer_app_id\":1991040," +
                "\"title\":\"  [b]Steam &amp; Title[/b]  \"," +
                "\"description\":\"[url=https://evil.invalid/]Line&nbsp;one[/url]\\r\\nLine two\"}");
            WorkshopMetadataBatchResult parsed = SteamWorkshopMetadataProvider.ParseResponse(
                response, new[] { id });
            Assert(parsed.Items.Count == 1 && parsed.Errors.Count == 0,
                "a valid Steam details response should produce one metadata item");
            Assert(parsed.Items[id].WorkshopId == id &&
                   parsed.Items[id].ConsumerAppId == WorkshopMetadataService.StudentAgeAppId,
                "Steam metadata must preserve the requested canonical Workshop ID and AppID");
            Assert(parsed.Items[id].Title == "Steam & Title" &&
                   parsed.Items[id].Description == "Line one Line two",
                "Steam response parsing should sanitize title and description as plain text");

            WorkshopMetadataBatchResult missing = SteamWorkshopMetadataProvider.ParseResponse(
                BuildSteamSuccessResponse(new[] { "123" }), new[] { "123", "456" });
            Assert(missing.Items.ContainsKey("123") && missing.Errors.ContainsKey("456"),
                "missing Workshop IDs should be reported per item");

            WorkshopMetadataBatchResult unavailable = SteamWorkshopMetadataProvider.ParseResponse(
                BuildSteamResponseFromDetails(
                    "{\"publishedfileid\":\"123\",\"result\":9}"),
                new[] { "123" });
            Assert(unavailable.Errors.ContainsKey("123") && unavailable.Items.Count == 0,
                "Steam item-level failure results should be reported as unavailable items");

            AssertSteamResponseRejected("{}", new[] { "123" }, "response");
            AssertSteamResponseRejected(
                "{\"response\":{\"result\":2,\"publishedfiledetails\":[]}}",
                new[] { "123" }, "result");
            AssertSteamResponseRejected(
                "{\"response\":{\"result\":\"1\",\"publishedfiledetails\":[]}}",
                new[] { "123" }, "JSON 整数");
            AssertSteamResponseRejected(
                "{\"response\":{\"result\":1.0,\"publishedfiledetails\":[]}}",
                new[] { "123" }, "JSON 整数");
            AssertSteamResponseRejected(
                "{\"response\":{\"result\":1,\"publishedfiledetails\":{}}}",
                new[] { "123" }, "不是数组");
            AssertSteamResponseRejected(BuildSteamResponseFromDetails(
                    "{\"publishedfileid\":123,\"result\":1," +
                    "\"consumer_app_id\":1991040,\"title\":\"T\"}"),
                new[] { "123" }, "不是字符串");
            AssertSteamResponseRejected(BuildSteamResponseFromDetails(
                    "{\"publishedfileid\":\"00123\",\"result\":1," +
                    "\"consumer_app_id\":1991040,\"title\":\"T\"}"),
                new[] { "123" }, "非规范");
            AssertSteamResponseRejected(BuildSteamSuccessResponse(new[] { "456" }),
                new[] { "123" }, "未请求", "456");
            string duplicate = BuildSteamResponseFromDetails(
                BuildSteamDetail("123", "One", "Description", 1991040),
                BuildSteamDetail("123", "Two", "Description", 1991040));
            AssertSteamResponseRejected(duplicate, new[] { "123" }, "重复");
            AssertSteamResponseRejected(BuildSteamResponseFromDetails(
                    "{\"publishedfileid\":\"123\",\"result\":\"1\"}"),
                new[] { "123" }, "JSON 整数");
            AssertSteamResponseRejected(BuildSteamResponseFromDetails(
                    "{\"publishedfileid\":\"123\",\"result\":1," +
                    "\"consumer_app_id\":\"1991040\",\"title\":\"T\"}"),
                new[] { "123" }, "JSON 无符号整数");
            AssertSteamResponseRejected(BuildSteamResponseFromDetails(
                    "{\"publishedfileid\":\"123\",\"result\":1," +
                    "\"consumer_app_id\":1991040,\"title\":7}"),
                new[] { "123" }, "title", "不是字符串");

            AssertThrows<ArgumentException>(() =>
                    SteamWorkshopMetadataProvider.ParseResponse(
                        BuildSteamSuccessResponse(new[] { "123" }),
                        new[] { "123", "123" }),
                "response parsing should reject duplicate requested IDs");
        }

        private static void RunWorkshopMetadataProviderTests()
        {
            var realTransport = new SteamWorkshopMetadataTransport();
            AssertThrows<ArgumentException>(() => realTransport.PostFormAsync(
                    "http://api.steampowered.com/invalid", new NameValueCollection(),
                    TimeSpan.FromSeconds(1), 1024).GetAwaiter().GetResult(),
                "the Steam transport must reject every non-HTTPS endpoint before network access");
            AssertThrows<ArgumentException>(() => realTransport.PostFormAsync(
                    "https://example.invalid/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
                    new NameValueCollection(), TimeSpan.FromSeconds(1), 1024)
                    .GetAwaiter().GetResult(),
                "the Steam transport must reject non-official HTTPS endpoints before network access");
            AssertThrows<ArgumentOutOfRangeException>(() => realTransport.PostFormAsync(
                    SteamWorkshopMetadataProvider.Endpoint, new NameValueCollection(),
                    TimeSpan.FromSeconds(1), 0).GetAwaiter().GetResult(),
                "the Steam transport must require a positive response-size limit");

            var transport = new FakeWorkshopMetadataTransport
            {
                Handler = call => Encoding.UTF8.GetBytes(
                    BuildSteamSuccessResponse(ReadRequestedIds(call.Values))),
            };
            var provider = new SteamWorkshopMetadataProvider(transport, 0);

            WorkshopMetadataBatchResult empty = provider.GetDetailsAsync(new string[0])
                .GetAwaiter().GetResult();
            Assert(empty.Items.Count == 0 && empty.Errors.Count == 0 &&
                   transport.Calls.Count == 0,
                "an empty Workshop metadata request must not access the network");

            var ids = new List<string>();
            for (int i = 1; i <= 51; i++)
                ids.Add(i.ToString(CultureInfo.InvariantCulture));
            ids.Insert(1, "1");
            WorkshopMetadataBatchResult batched = provider.GetDetailsAsync(ids)
                .GetAwaiter().GetResult();
            Assert(batched.Items.Count == 51 && transport.Calls.Count == 2,
                "Workshop metadata requests should deduplicate IDs and split batches at 50 items");
            AssertTransportCall(transport.Calls[0], 50, "1", "50");
            AssertTransportCall(transport.Calls[1], 1, "51", "51");

            int callsBeforeInvalid = transport.Calls.Count;
            AssertThrows<ArgumentException>(() =>
                    provider.GetDetailsAsync(new[] { "001" }).GetAwaiter().GetResult(),
                "the Steam provider must accept canonical numeric IDs only");
            Assert(transport.Calls.Count == callsBeforeInvalid,
                "invalid metadata request IDs must be rejected before network access");

            var retryTransport = new FakeWorkshopMetadataTransport();
            retryTransport.Handler = call =>
            {
                if (retryTransport.Calls.Count == 1)
                    throw new TimeoutException("simulated timeout");
                return Encoding.UTF8.GetBytes(BuildSteamSuccessResponse(
                    ReadRequestedIds(call.Values)));
            };
            var retryProvider = new SteamWorkshopMetadataProvider(retryTransport, 0);
            WorkshopMetadataBatchResult retried = retryProvider.GetDetailsAsync(new[] { "77" })
                .GetAwaiter().GetResult();
            Assert(retried.Items.ContainsKey("77") && retryTransport.Calls.Count == 2,
                "a transient Steam transport failure should be retried once");

            var failingTransport = new FakeWorkshopMetadataTransport
            {
                Handler = call => throw new TimeoutException("simulated timeout"),
            };
            var failingProvider = new SteamWorkshopMetadataProvider(failingTransport, 0);
            AssertThrows<IOException>(() =>
                    failingProvider.GetDetailsAsync(new[] { "78" }).GetAwaiter().GetResult(),
                "two Steam transport failures should surface as a request failure");
            Assert(failingTransport.Calls.Count == 2,
                "network failures should use exactly the configured two attempts");

            var oversizedTransport = new FakeWorkshopMetadataTransport
            {
                Handler = call => new byte[(4 * 1024 * 1024) + 1],
            };
            AssertThrows<InvalidDataException>(() =>
                    new SteamWorkshopMetadataProvider(oversizedTransport, 0)
                        .GetDetailsAsync(new[] { "79" }).GetAwaiter().GetResult(),
                "Steam responses larger than 4 MiB must be rejected without parsing");
            Assert(oversizedTransport.Calls.Count == 1,
                "deterministically oversized responses should not be retried");

            var malformedTransport = new FakeWorkshopMetadataTransport
            {
                Handler = call => Encoding.UTF8.GetBytes("{not-json"),
            };
            AssertThrows<InvalidDataException>(() =>
                    new SteamWorkshopMetadataProvider(malformedTransport, 0)
                        .GetDetailsAsync(new[] { "80" }).GetAwaiter().GetResult(),
                "malformed Steam JSON should be rejected");
            Assert(malformedTransport.Calls.Count == 1,
                "malformed API content should not be treated as a transient transport error");

            var invalidUtf8Transport = new FakeWorkshopMetadataTransport
            {
                Handler = call => new byte[] { (byte)'{', 0xFF, (byte)'}' },
            };
            AssertThrows<InvalidDataException>(() =>
                    new SteamWorkshopMetadataProvider(invalidUtf8Transport, 0)
                        .GetDetailsAsync(new[] { "81" }).GetAwaiter().GetResult(),
                "malformed UTF-8 from Steam should be rejected before JSON parsing");
            Assert(invalidUtf8Transport.Calls.Count == 1,
                "invalid UTF-8 content should not be retried as a transient network failure");

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var cancelledTransport = new FakeWorkshopMetadataTransport
                {
                    Handler = call => Encoding.UTF8.GetBytes(
                        BuildSteamSuccessResponse(ReadRequestedIds(call.Values))),
                };
                AssertThrows<OperationCanceledException>(() =>
                        new SteamWorkshopMetadataProvider(cancelledTransport, 0)
                            .GetDetailsAsync(new[] { "82" }, cts.Token).GetAwaiter().GetResult(),
                    "metadata cancellation should propagate instead of falling back or retrying");
                Assert(cancelledTransport.Calls.Count == 0,
                    "pre-cancelled metadata operations must not access the network");
            }
        }

        private static void AssertTransportCall(FakeTransportCall call, int expectedCount,
            string expectedFirstId, string expectedLastId)
        {
            Assert(call.Endpoint == SteamWorkshopMetadataProvider.Endpoint,
                "Steam metadata must use only the fixed official API endpoint");
            Assert(call.Timeout == TimeSpan.FromSeconds(10),
                "Steam metadata transport should enforce the ten-second request timeout");
            Assert(call.MaxResponseBytes == 4 * 1024 * 1024,
                "Steam metadata transport should receive the four-MiB response cap");
            Assert(call.Values["itemcount"] == expectedCount.ToString(CultureInfo.InvariantCulture),
                "Steam form itemcount should match the current batch");
            Assert(call.Values["publishedfileids[0]"] == expectedFirstId &&
                   call.Values["publishedfileids[" + (expectedCount - 1).ToString(
                       CultureInfo.InvariantCulture) + "]"] == expectedLastId,
                "Steam form fields should carry canonical IDs in stable batch order");
            Assert(call.Values.Count == expectedCount + 1,
                "Steam metadata form should contain only itemcount and indexed Workshop IDs");
        }

        private static void AssertSteamResponseRejected(string json, IList<string> requestedIds,
            params string[] expectedFragments)
        {
            bool rejected = false;
            try
            {
                SteamWorkshopMetadataProvider.ParseResponse(json, requestedIds);
            }
            catch (InvalidDataException ex)
            {
                rejected = true;
                foreach (string fragment in expectedFragments)
                    Assert(ex.Message.Contains(fragment),
                        "Steam response rejection should mention '" + fragment + "': " + ex.Message);
            }
            Assert(rejected, "invalid Steam metadata responses must be rejected");
        }

        private static void RunWorkshopMetadataServiceTests(string root)
        {
            Directory.CreateDirectory(root);
            DateTime now = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
            string cachePath = Path.Combine(root, "state", "workshop-metadata.json");
            string expectedCachePath = Path.Combine(root, "game", "BepInEx", "ModManager",
                "workshop-metadata.json");
            Assert(new LocalState(Path.Combine(root, "game")).WorkshopMetadataCachePath ==
                   expectedCachePath,
                "Workshop metadata cache should live under BepInEx/ModManager");

            var provider = new FakeWorkshopMetadataProvider
            {
                Handler = ids =>
                {
                    var result = new WorkshopMetadataBatchResult();
                    foreach (string id in ids)
                    {
                        result.Items.Add(id, CreateMetadata(id,
                            "[b]Live&nbsp;Title " + id + "[/b]",
                            "[url=https://evil.invalid/]Live description " + id + "[/url]"));
                    }
                    return result;
                },
            };
            var index = CreateWorkshopIndex(
                new ModEntry
                {
                    id = "missing-both",
                    workshopId = "101",
                },
                new ModEntry
                {
                    id = "explicit-name",
                    workshopId = "102",
                    name = "Creator Name",
                },
                new ModEntry
                {
                    id = "explicit-description",
                    workshopId = "103",
                    description = "Creator [b]Description[/b]",
                },
                new ModEntry
                {
                    id = "explicit-both",
                    workshopId = "104",
                    name = "  Explicit [b]Name[/b]  ",
                    description = " Explicit description ",
                },
                new ModEntry
                {
                    id = "whitespace-fields",
                    workshopId = "105",
                    name = "   ",
                    description = "\r\n\t",
                });

            new WorkshopMetadataService(provider, cachePath, () => now)
                .EnrichMissingAsync(index).GetAwaiter().GetResult();
            Assert(provider.Calls.Count == 1 && provider.Calls[0].Count == 4 &&
                   !provider.Calls[0].Contains("104"),
                "runtime enrichment should request only Workshop entries with missing display fields");
            Assert(index.mods[0].name == "Live Title 101" &&
                   index.mods[0].description == "Live description 101",
                "missing name and description should be filled from sanitized live Steam metadata");
            Assert(index.mods[1].name == "Creator Name" &&
                   index.mods[1].description == "Live description 102",
                "an explicit index name must win while only the missing description is filled");
            Assert(index.mods[2].name == "Live Title 103" &&
                   index.mods[2].description == "Creator [b]Description[/b]",
                "an explicit index description must remain byte-for-byte unchanged");
            Assert(index.mods[3].name == "  Explicit [b]Name[/b]  " &&
                   index.mods[3].description == " Explicit description ",
                "Steam enrichment must not clean or overwrite explicit index display metadata");
            Assert(index.mods[4].name == "Live Title 105" &&
                   index.mods[4].description == "Live description 105",
                "empty and whitespace-only display fields should be treated as missing");
            for (int i = 0; i < index.mods.Count; i++)
                Assert(index.mods[i].workshopId == (101 + i).ToString(CultureInfo.InvariantCulture),
                    "metadata enrichment must never change a normalized Workshop ID");
            Assert(File.Exists(cachePath),
                "successful live Steam metadata should be persisted in the local cache");
            Assert(Directory.GetFiles(Path.GetDirectoryName(cachePath), "*.tmp").Length == 0,
                "atomic metadata cache writes must not leave temporary files behind");

            var freshProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new IOException("fresh cache should avoid network access"),
            };
            var freshIndex = CreateWorkshopIndex(
                new ModEntry { id = "fresh", workshopId = "101" });
            new WorkshopMetadataService(freshProvider, cachePath, () => now.AddHours(1))
                .EnrichMissingAsync(freshIndex).GetAwaiter().GetResult();
            Assert(freshProvider.Calls.Count == 0 &&
                   freshIndex.mods[0].name == "Live Title 101" &&
                   freshIndex.mods[0].description == "Live description 101",
                "a cache younger than 24 hours should satisfy missing fields without a request");

            var updateProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids => BatchWith(CreateMetadata(ids[0],
                    "New live title", "New live description")),
            };
            var staleIndex = CreateWorkshopIndex(
                new ModEntry { id = "stale", workshopId = "101" });
            new WorkshopMetadataService(updateProvider, cachePath, () => now.AddHours(25))
                .EnrichMissingAsync(staleIndex).GetAwaiter().GetResult();
            Assert(updateProvider.Calls.Count == 1 &&
                   staleIndex.mods[0].name == "New live title" &&
                   staleIndex.mods[0].description == "New live description",
                "valid live Steam metadata must take precedence over stale cache content");
            string prunedCache = File.ReadAllText(cachePath);
            Assert(prunedCache.Contains("\"101\"") &&
                   !prunedCache.Contains("\"102\"") &&
                   !prunedCache.Contains("\"103\"") &&
                   !prunedCache.Contains("\"105\""),
                "cache rewrites should retain metadata only for the current validated index IDs");

            var staleFailureProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new IOException("simulated network outage"),
            };
            var staleFallback = CreateWorkshopIndex(
                new ModEntry { id = "stale-fallback", workshopId = "101" });
            new WorkshopMetadataService(staleFailureProvider, cachePath, () => now.AddHours(50))
                .EnrichMissingAsync(staleFallback).GetAwaiter().GetResult();
            Assert(staleFailureProvider.Calls.Count == 1 &&
                   staleFallback.mods[0].name == "New live title" &&
                   staleFallback.mods[0].description == "New live description",
                "an expired cache should remain available when a refresh request fails");

            var partialLiveProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids => BatchWith(CreateMetadata(ids[0], "Latest title", "")),
            };
            var partialLive = CreateWorkshopIndex(
                new ModEntry { id = "partial-live", workshopId = "101" });
            new WorkshopMetadataService(partialLiveProvider, cachePath, () => now.AddHours(51))
                .EnrichMissingAsync(partialLive).GetAwaiter().GetResult();
            Assert(partialLive.mods[0].name == "Latest title" &&
                   partialLive.mods[0].description == "New live description",
                "a live nonempty field should win while a missing live field may use stale cache");

            string fallbackCache = Path.Combine(root, "fallback", "workshop-metadata.json");
            var unavailableProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new IOException("offline"),
            };
            var fallbackIndex = CreateWorkshopIndex(
                new ModEntry
                {
                    id = "internal-fallback",
                    workshopId = "201",
                    name = null,
                    description = "   ",
                });
            new WorkshopMetadataService(unavailableProvider, fallbackCache, () => now)
                .EnrichMissingAsync(fallbackIndex).GetAwaiter().GetResult();
            Assert(fallbackIndex.mods[0].name == "internal-fallback" &&
                   fallbackIndex.mods[0].description == WorkshopMetadataService.DefaultDescription,
                "without Steam or cache, runtime display metadata should use safe local fallbacks");

            var nullProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids => null,
            };
            var nullResponseIndex = CreateWorkshopIndex(
                new ModEntry { id = "null-response", workshopId = "205" });
            new WorkshopMetadataService(nullProvider,
                    Path.Combine(root, "null-response", "workshop-metadata.json"), () => now)
                .EnrichMissingAsync(nullResponseIndex).GetAwaiter().GetResult();
            Assert(nullResponseIndex.mods[0].name == "null-response" &&
                   nullResponseIndex.mods[0].description ==
                       WorkshopMetadataService.DefaultDescription,
                "a null provider result should be treated as an optional display-data failure");

            var invalidLiveProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids =>
                {
                    var result = new WorkshopMetadataBatchResult();
                    result.Items.Add(ids[0], CreateMetadata(ids[0], "Wrong app", "Remote", 7));
                    result.Items.Add(ids[1], CreateMetadata(ids[1], "   ", "Remote"));
                    result.Items.Add(ids[2], CreateMetadata("999", "Wrong ID", "Remote"));
                    return result;
                },
            };
            var invalidLive = CreateWorkshopIndex(
                new ModEntry { id = "wrong-app", workshopId = "202" },
                new ModEntry { id = "empty-title", workshopId = "203" },
                new ModEntry { id = "wrong-id", workshopId = "204" });
            new WorkshopMetadataService(invalidLiveProvider,
                    Path.Combine(root, "invalid-live", "workshop-metadata.json"), () => now)
                .EnrichMissingAsync(invalidLive).GetAwaiter().GetResult();
            foreach (ModEntry entry in invalidLive.mods)
                Assert(entry.name == entry.id &&
                       entry.description == WorkshopMetadataService.DefaultDescription,
                    "wrong AppID, empty title, or mismatched ID must never become display metadata");

            string fetchedAt = now.ToString("o", CultureInfo.InvariantCulture);
            AssertInvalidCacheFallsBack(root, "corrupt-json", "{not-json", now);
            AssertInvalidCacheFallsBack(root, "wrong-schema",
                "{\"schemaVersion\":2,\"items\":{}}", now);
            AssertInvalidCacheFallsBack(root, "string-app-id",
                "{\"schemaVersion\":1,\"items\":{\"201\":{" +
                "\"consumerAppId\":\"1991040\",\"title\":\"Cached\"," +
                "\"description\":\"Cached\",\"fetchedAtUtc\":" +
                JsonQuote(fetchedAt) + "}}}", now);
            AssertInvalidCacheFallsBack(root, "wrong-app-id",
                "{\"schemaVersion\":1,\"items\":{\"201\":{" +
                "\"consumerAppId\":7,\"title\":\"Cached\"," +
                "\"description\":\"Cached\",\"fetchedAtUtc\":" +
                JsonQuote(fetchedAt) + "}}}", now);
            AssertInvalidCacheFallsBack(root, "noncanonical-id",
                "{\"schemaVersion\":1,\"items\":{\"0201\":{" +
                "\"consumerAppId\":1991040,\"title\":\"Cached\"," +
                "\"description\":\"Cached\",\"fetchedAtUtc\":" +
                JsonQuote(fetchedAt) + "}}}", now);
            AssertInvalidCacheFallsBack(root, "non-string-title",
                "{\"schemaVersion\":1,\"items\":{\"201\":{" +
                "\"consumerAppId\":1991040,\"title\":7," +
                "\"description\":\"Cached\",\"fetchedAtUtc\":" +
                JsonQuote(fetchedAt) + "}}}", now);
            AssertInvalidCacheFallsBack(root, "non-string-description",
                "{\"schemaVersion\":1,\"items\":{\"201\":{" +
                "\"consumerAppId\":1991040,\"title\":\"Cached\"," +
                "\"description\":false,\"fetchedAtUtc\":" +
                JsonQuote(fetchedAt) + "}}}", now);
            AssertInvalidCacheFallsBack(root, "invalid-timestamp",
                "{\"schemaVersion\":1,\"items\":{\"201\":{" +
                "\"consumerAppId\":1991040,\"title\":\"Cached\"," +
                "\"description\":\"Cached\",\"fetchedAtUtc\":7}}}", now);
            AssertInvalidCacheFallsBack(root, "oversized", null, now);
        }

        private static void AssertInvalidCacheFallsBack(string root, string caseName,
            string contents, DateTime now)
        {
            string directory = Path.Combine(root, "bad-cache", caseName);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "workshop-metadata.json");
            if (contents == null)
                File.WriteAllText(path, new string('x', (1024 * 1024) + 1));
            else
                File.WriteAllText(path, contents, new UTF8Encoding(false));

            var provider = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new IOException("cache miss network failure"),
            };
            var index = CreateWorkshopIndex(
                new ModEntry { id = "bad-cache-fallback", workshopId = "201" });
            new WorkshopMetadataService(provider, path, () => now)
                .EnrichMissingAsync(index).GetAwaiter().GetResult();
            Assert(provider.Calls.Count == 1 &&
                   index.mods[0].name == "bad-cache-fallback" &&
                   index.mods[0].description == WorkshopMetadataService.DefaultDescription,
                "invalid cache case '" + caseName + "' should be treated as a cache miss");
        }

        private static void RunWorkshopMetadataVerificationTests(string root)
        {
            Directory.CreateDirectory(root);
            var neverCalled = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new InvalidOperationException("empty index requested Steam"),
            };
            int emptyCount = new WorkshopMetadataService(neverCalled)
                .VerifyIndexAsync(CreateWorkshopIndex()).GetAwaiter().GetResult();
            Assert(emptyCount == 0 && neverCalled.Calls.Count == 0,
                "online verification of an empty index should succeed without a Steam request");

            var successProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids =>
                {
                    var result = new WorkshopMetadataBatchResult();
                    foreach (string id in ids)
                        result.Items.Add(id, CreateMetadata(id, "Public " + id, "Description"));
                    return result;
                },
            };
            var validIndex = CreateWorkshopIndex(
                new ModEntry { id = "verify-one", workshopId = "301" },
                new ModEntry { id = "verify-two", workshopId = "302" },
                new ModEntry { id = "legacy" });
            int verified = new WorkshopMetadataService(successProvider)
                .VerifyIndexAsync(validIndex).GetAwaiter().GetResult();
            Assert(verified == 2 && successProvider.Calls.Count == 1,
                "online verification should count and validate every declared Workshop item");

            AssertWorkshopVerificationRejected(validIndex,
                ids => BatchWith(CreateMetadata(ids[0], "Wrong app", "", 42),
                    CreateMetadata(ids[1], "Valid", "")),
                "AppID", "1991040");
            AssertWorkshopVerificationRejected(validIndex,
                ids => BatchWith(CreateMetadata(ids[0], "   ", ""),
                    CreateMetadata(ids[1], "Valid", "")),
                "标题为空");
            AssertWorkshopVerificationRejected(validIndex,
                ids =>
                {
                    var result = BatchWith(CreateMetadata(ids[1], "Valid", ""));
                    result.Items.Add(ids[0], CreateMetadata("999", "Wrong ID", ""));
                    return result;
                },
                "ID 不匹配");
            AssertWorkshopVerificationRejected(validIndex,
                ids =>
                {
                    var result = BatchWith(CreateMetadata(ids[1], "Valid", ""));
                    result.Errors.Add(ids[0], "Steam 返回 result=9。");
                    return result;
                },
                "result=9");
            AssertWorkshopVerificationRejected(validIndex,
                ids => BatchWith(CreateMetadata(ids[0], "Valid", "")),
                "缺少该项目");

            string cachePath = Path.Combine(root, "workshop-metadata.json");
            string fetchedAt = new DateTime(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc)
                .ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(cachePath,
                "{\"schemaVersion\":1,\"items\":{\"301\":{" +
                "\"consumerAppId\":1991040,\"title\":\"Cached title\"," +
                "\"description\":\"Cached description\",\"fetchedAtUtc\":" +
                JsonQuote(fetchedAt) + "}}}", new UTF8Encoding(false));
            string cacheBeforeVerification = File.ReadAllText(cachePath);
            var networkFailure = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new IOException("simulated Steam outage"),
            };
            bool networkRejected = false;
            try
            {
                new WorkshopMetadataService(networkFailure, cachePath)
                    .VerifyIndexAsync(validIndex).GetAwaiter().GetResult();
            }
            catch (InvalidDataException ex)
            {
                networkRejected = ex.Message.Contains("在线验证请求失败") &&
                                  ex.Message.Contains("simulated Steam outage");
            }
            Assert(networkRejected && networkFailure.Calls.Count == 1,
                "CI verification must fail on network errors and must never use runtime cache");
            Assert(File.ReadAllText(cachePath) == cacheBeforeVerification,
                "online verification must not rewrite the runtime metadata cache");
        }

        private static void AssertWorkshopVerificationRejected(ModIndex index,
            Func<IList<string>, WorkshopMetadataBatchResult> handler,
            params string[] expectedFragments)
        {
            var provider = new FakeWorkshopMetadataProvider { Handler = handler };
            bool rejected = false;
            try
            {
                new WorkshopMetadataService(provider).VerifyIndexAsync(index)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidDataException ex)
            {
                rejected = true;
                foreach (string fragment in expectedFragments)
                    Assert(ex.Message.Contains(fragment),
                        "online verification rejection should mention '" + fragment +
                        "': " + ex.Message);
            }
            Assert(rejected && provider.Calls.Count == 1,
                "invalid or unavailable Workshop projects must fail online verification");
        }

        private static void RunIndexClientMetadataIntegrationTests(string root)
        {
            Directory.CreateDirectory(root);
            string validPath = Path.Combine(root, "valid.json");
            File.WriteAllText(validPath,
                "{\"schemaVersion\":1,\"mirrors\":[\"https://mirror.invalid/\"]," +
                "\"mods\":[{\"id\":\"runtime\",\"workshopId\":\"301\"}]}",
                new UTF8Encoding(false));
            var downloader = new Downloader();
            bool metadataRanBeforeMirrorUpdate = false;
            var provider = new FakeWorkshopMetadataProvider
            {
                Handler = ids =>
                {
                    metadataRanBeforeMirrorUpdate =
                        !downloader.Mirrors.Contains("https://mirror.invalid/");
                    return BatchWith(CreateMetadata(ids[0],
                        "Runtime title", "Runtime description"));
                },
            };
            var client = new IndexClient(downloader,
                new WorkshopMetadataService(provider,
                    Path.Combine(root, "cache", "workshop-metadata.json")));
            ModIndex loaded = client.FetchAsync(new Uri(validPath).AbsoluteUri)
                .GetAwaiter().GetResult();
            Assert(loaded.mods[0].name == "Runtime title" &&
                   loaded.mods[0].description == "Runtime description" &&
                   loaded.mods[0].workshopId == "301",
                "IndexClient should enrich only after deterministic validation and normalization");
            Assert(metadataRanBeforeMirrorUpdate && downloader.Mirrors.Count == 1 &&
                   downloader.Mirrors[0] == "https://mirror.invalid/",
                "metadata enrichment should finish before the validated index replaces mirrors");

            string invalidPath = Path.Combine(root, "invalid.json");
            File.WriteAllText(invalidPath,
                "{\"schemaVersion\":1,\"mods\":[{" +
                "\"id\":\"invalid\",\"workshopId\":\"   \"}]}",
                new UTF8Encoding(false));
            int callsBefore = provider.Calls.Count;
            AssertThrows<InvalidDataException>(() =>
                    client.FetchAsync(new Uri(invalidPath).AbsoluteUri).GetAwaiter().GetResult(),
                "invalid indexes should fail before optional Steam metadata enrichment");
            Assert(provider.Calls.Count == callsBefore,
                "deterministic index rejection must occur before any Steam metadata request");

            string offlinePath = Path.Combine(root, "offline.json");
            File.WriteAllText(offlinePath,
                "{\"schemaVersion\":1,\"mods\":[{" +
                "\"id\":\"offline-runtime\",\"workshopId\":\"302\"}]}",
                new UTF8Encoding(false));
            var offlineProvider = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new IOException("offline"),
            };
            var offlineClient = new IndexClient(
                new Downloader(),
                new WorkshopMetadataService(offlineProvider,
                    Path.Combine(root, "offline-cache", "workshop-metadata.json")));
            ModIndex offline = offlineClient.FetchAsync(new Uri(offlinePath).AbsoluteUri)
                .GetAwaiter().GetResult();
            Assert(offline.mods[0].name == "offline-runtime" &&
                   offline.mods[0].description == WorkshopMetadataService.DefaultDescription,
                "runtime Steam network failure must not reject a deterministically valid index");
        }

        private static void RunWorkshopMetadataCliTests(string root)
        {
            Directory.CreateDirectory(root);
            string emptyPath = Path.Combine(root, "empty.json");
            File.WriteAllText(emptyPath, "{\"schemaVersion\":1,\"mods\":[]}",
                new UTF8Encoding(false));
            var mustNotCall = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new InvalidOperationException("empty index called Steam"),
            };
            Assert(RunCommandWithHandling(new[]
                {
                    "--validate-index", emptyPath, "--verify-workshop",
                }, mustNotCall, false) == 0 && mustNotCall.Calls.Count == 0,
                "online CLI mode should return zero for an empty index without network access");
            Assert(RunCommandWithHandling(new[]
                {
                    "--validate-index", emptyPath, "--wrong-option",
                }, mustNotCall, false) == 2,
                "unsupported CLI argument combinations should return usage exit code 2");

            string validPath = Path.Combine(root, "valid.json");
            File.WriteAllText(validPath,
                "{\"schemaVersion\":1,\"mods\":[{" +
                "\"id\":\"cli-workshop\",\"workshopId\":\"401\"}]}",
                new UTF8Encoding(false));
            var success = new FakeWorkshopMetadataProvider
            {
                Handler = ids => BatchWith(CreateMetadata(ids[0], "CLI title", "")),
            };
            Assert(RunCommandWithHandling(new[]
                {
                    "--validate-index", validPath, "--verify-workshop",
                }, success, false) == 0,
                "online CLI mode should return zero after successful live verification");

            var explicitFailure = new FakeWorkshopMetadataProvider
            {
                Handler = ids =>
                {
                    var result = new WorkshopMetadataBatchResult();
                    result.Errors.Add(ids[0], "Steam 返回 result=9。");
                    return result;
                },
            };
            Assert(RunCommandWithHandling(new[]
                {
                    "--validate-index", validPath, "--verify-workshop",
                }, explicitFailure, false) == 1,
                "online CLI mode should return one for an explicitly unavailable Workshop item");

            var networkFailure = new FakeWorkshopMetadataProvider
            {
                Handler = ids => throw new IOException("CLI network failure"),
            };
            Assert(RunCommandWithHandling(new[]
                {
                    "--validate-index", validPath, "--verify-workshop",
                }, networkFailure, false) == 1,
                "online CLI mode should return one for Steam network failures");
            Assert(RunCommandWithHandling(new[]
                {
                    "--validate-index", validPath,
                }, networkFailure, false) == 0,
                "offline CLI validation must stay compatible and avoid Steam verification");

            string invalidPath = Path.Combine(root, "invalid.json");
            File.WriteAllText(invalidPath, "{not-json", new UTF8Encoding(false));
            Assert(RunCommandWithHandling(new[]
                {
                    "--validate-index", invalidPath,
                }, null, false) == 1,
                "invalid index content should return CLI failure exit code 1");
        }




        private static void RunMainFormUiTests(string root)
        {
            Directory.CreateDirectory(root);
            using (var form = new MainForm())
            {
                var guide = GetControl<Panel>(form, "_workshopGuide");
                var setupTitle = GetControl<Label>(form, "_workshopSetupTitle");
                var setupText = GetControl<Label>(form, "_workshopSetupText");
                var manageTitle = GetControl<Label>(form, "_workshopManageTitle");
                var manageText = GetControl<Label>(form, "_workshopManageText");
                var submissionFooter = GetControl<Panel>(form, "_submissionFooter");
                var submissionLink = GetControl<LinkLabel>(form, "_workshopSubmissionLink");
                var banner = GetControl<Panel>(form, "_banner");
                var flow = GetControl<FlowLayoutPanel>(form, "_flow");
                var status = GetControl<Label>(form, "_lblStatus");
                var refresh = GetControl<Button>(form, "_btnRefresh");

                Assert(setupTitle.Text == "第一次使用前的准备" && setupTitle.Font.Bold,
                    "workshop guide should keep the original first-use heading");
                Assert(setupText.Text.Contains("点击“一键安装完整前置”") &&
                       setupText.Text.Contains("内置包") &&
                       setupText.Text.Contains("无需联网"),
                    "first-use text should explain offline setup");
                Assert(setupText.Text.Contains("点“刷新”") &&
                       setupText.Text.Contains("下次启动游戏时自动启用") &&
                       !setupText.Text.Contains("中央索引") &&
                       !setupText.Text.Contains("白名单") &&
                       !setupText.Text.Contains("合法"),
                    "first-use text should explain both manual refresh and startup sync " +
                    "without internal jargon");
                Assert(refresh.Text == "刷新",
                    "the refresh button should use the plain player-facing label");
                Assert(typeof(MainForm).GetField("_cmbMirror",
                           BindingFlags.Instance | BindingFlags.NonPublic) == null &&
                       !form.Controls.OfType<ComboBox>().Any(),
                    "the obsolete user-facing direct/mirror selector must be removed");
                var releaseUrlField = typeof(MainForm).GetField("ManagerReleaseUrl",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert(releaseUrlField != null && string.Equals(
                       releaseUrlField.GetRawConstantValue() as string,
                       "https://github.com/white12666/StudentAgeModManager/releases/latest",
                       StringComparison.Ordinal),
                    "a mirrored text index must not control the executable update destination");
                Assert(manageTitle.Text == "如何管理 Mod" && manageTitle.Font.Bold,
                    "workshop guide should use a clear management heading");
                Assert(manageText.Text.Contains("订阅和取消订阅请在 Steam 中操作") &&
                       manageText.Text.Contains("可在下方列表") &&
                       manageText.Text.Contains("游戏内“本地”页") &&
                       manageText.Text.Contains("不在推荐列表中的工坊 Mod 也能正常使用") &&
                       manageText.Text.Contains("已禁用的 Mod 仍会随 Steam 更新"),
                    "management text should explain both toggle surfaces, local visibility, and updates");
                Assert(submissionLink.Text.Contains("“已收录”表示 Mod 已进入官方推荐列表") &&
                       submissionLink.Text.Contains("可自定义显示名称与简介") &&
                       submissionLink.Text.Contains("欢迎 Mod 作者") &&
                       submissionLink.Text.Contains("GitHub 提交收录"),
                    "the fixed footer should explain 收录, index display metadata, and contribution");
                Assert(submissionLink.Links.Count == 1 &&
                       string.Equals(submissionLink.Links[0].LinkData as string,
                           "https://github.com/white12666/StudentAgeModManager/blob/main/CONTRIBUTING.md",
                           StringComparison.Ordinal),
                    "the author submission link should target the main-branch contribution guide");
                AssertTextFits(setupText,
                    "first-use instructions must fit without clipping");
                AssertTextFits(manageText,
                    "management instructions must fit without clipping");
                Assert(!submissionLink.Text.Contains("\r") &&
                       !submissionLink.Text.Contains("\n"),
                    "the contribution footer must stay on one line");
                Assert(Math.Abs(submissionLink.Font.Size - 8f) < 0.01f,
                    "the contribution footer should use the requested one-step-smaller 8pt font");
                AssertSingleLineFits(submissionLink,
                    "submission hint must fit on one line at 8pt");
                Assert(setupTitle.Bottom <= setupText.Top &&
                       setupText.Bottom <= manageTitle.Top &&
                       manageTitle.Bottom <= manageText.Top &&
                       manageText.Bottom <= guide.ClientSize.Height,
                    "workshop guide sections must not overlap or leave the panel");
                Assert(guide.Bottom <= banner.Top,
                    "workshop guide must not overlap the BepInEx banner");
                Assert(submissionLink.Parent == submissionFooter &&
                       flow.Bottom + 4 == submissionFooter.Top &&
                       submissionFooter.Bottom + 4 == status.Top &&
                       submissionLink.Top == 3,
                    "the smaller contribution line should sit lower with tight fixed gaps above the status bar");
                Assert(guide.Bottom <= flow.Top,
                    "the initial Mod list must remain below the top guide");

                var gameRoot = Path.Combine(root, "StudentAge");
                Directory.CreateDirectory(gameRoot);
                var installer = new ModInstaller(new LocalState(gameRoot));
                SetPrivateField(form, "_installer", installer);

                InvokePrivate(form, "UpdateBepInExUi");
                Assert(flow.Top == 234 && banner.Bottom <= flow.Top,
                    "visible prerequisite banner must remain above the mod list");
                Assert(flow.Bottom + 4 == submissionFooter.Top,
                    "banner-visible Mod list must end before the fixed contribution footer");

                installer.InstallBepInExAsync(null).GetAwaiter().GetResult();
                InvokePrivate(form, "UpdateBepInExUi");
                Assert(flow.Top == 196 && guide.Bottom <= flow.Top,
                    "hidden prerequisite banner must leave the mod list below the guide");
                Assert(flow.Bottom + 4 == submissionFooter.Top,
                    "banner-hidden Mod list must end before the fixed contribution footer");
                Assert(flow is WheelFlowLayoutPanel && flow.AutoScroll,
                    "the Mod list should use the wheel-aware auto-scrolling panel");

                SetPrivateField(form, "_gameDir", gameRoot);
                SetPrivateField(form, "_isGameRunning", new Func<bool>(() => false));
                bool synchronizeCalled = false;
                SetPrivateField(form, "_workshopSynchronizer",
                    new Func<string, BridgeResult>(path =>
                    {
                        synchronizeCalled = string.Equals(path, gameRoot,
                            StringComparison.OrdinalIgnoreCase);
                        return WorkshopBridgeSynchronizer.Synchronize(new BridgeOptions
                        {
                            PluginRootPath = Path.Combine(gameRoot, "BepInEx", "plugins"),
                            WorkshopRootPath = Path.Combine(root, "missing-workshop-root"),
                        });
                    }));
                var syncMethod = typeof(MainForm).GetMethod(
                    "SynchronizeWorkshopForRefreshAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var syncTask = (Task)syncMethod.Invoke(form, new object[] { true });
                syncTask.GetAwaiter().GetResult();
                Assert(synchronizeCalled,
                    "manual refresh should invoke the shared Workshop Bridge synchronizer");
                Assert(File.Exists(Path.Combine(gameRoot, "BepInEx", "ModManager",
                        "WorkshopRefresh.log")),
                    "manual Workshop synchronization should leave a diagnostic log");

                synchronizeCalled = false;
                SetPrivateField(form, "_isGameRunning", new Func<bool>(() => true));
                syncTask = (Task)syncMethod.Invoke(form, new object[] { true });
                syncTask.GetAwaiter().GetResult();
                Assert(!synchronizeCalled,
                    "manual refresh must not mutate Workshop links while the game is running");

                var connected = new LocalPluginUnit
                {
                    UnitKey = ".workshop/100",
                    DisplayName = "Connected Listed",
                    DisplayVersion = "1.0.0",
                    RelativePath = "BepInEx\\plugins\\.workshop\\100",
                    EnabledRelativePath = "BepInEx\\plugins\\.workshop\\100",
                    IsDirectory = true,
                    Source = LocalPluginSource.SteamWorkshop,
                    WorkshopId = "100",
                    IsWorkshopSubscribed = true,
                    IsWorkshopDownloaded = true,
                    IsWorkshopConnected = true,
                    HasWorkshopManifest = true,
                    IsWorkshopPackageValid = true,
                    DllCount = 1,
                    Plugins = new List<ScannedPlugin>
                    {
                        new ScannedPlugin { Name = "Connected Listed", Version = "1.0.0" },
                    },
                };

                bool workshopToggleCalled = false;
                SetPrivateField(form, "_workshopToggler",
                    new Func<string, string, bool, WorkshopToggleResult>((path, id, enabled) =>
                    {
                        workshopToggleCalled = true;
                        return new WorkshopToggleResult();
                    }));
                var setWorkshopEnabledMethod = typeof(MainForm).GetMethod(
                    "SetWorkshopPluginEnabledAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(setWorkshopEnabledMethod != null,
                    "MainForm should expose a testable guarded Workshop state operation");
                SetPrivateField(form, "_isGameRunning", new Func<bool>(() => true));
                var guardedToggleTask = (Task)setWorkshopEnabledMethod.Invoke(form,
                    new object[] { connected, false });
                AssertThrows<InvalidOperationException>(
                    () => guardedToggleTask.GetAwaiter().GetResult(),
                    "Workshop state changes must be rejected while the game is running");
                Assert(!workshopToggleCalled,
                    "the game-running guard must stop before invoking Workshop management");

                string managedSource = Path.Combine(root, "managed-workshop", "100",
                    "BepInEx", "plugins", "ConnectedListed");
                WritePluginAssembly(Path.Combine(managedSource, "ConnectedListed.dll"),
                    "tests.connected.listed", "Connected Listed", "1.0.1");
                var managedItem = new WorkshopManagedItem();
                SetNonPublicProperty(managedItem, "WorkshopId", "100");
                SetNonPublicProperty(managedItem, "IsSubscribed", true);
                SetNonPublicProperty(managedItem, "IsDownloaded", true);
                SetNonPublicProperty(managedItem, "IsEnabled", false);
                SetNonPublicProperty(managedItem, "IsConnected", false);
                SetNonPublicProperty(managedItem, "HasBridgeManifest", true);
                SetNonPublicProperty(managedItem, "IsValidBridgePackage", true);
                SetNonPublicProperty(managedItem, "ContentPath",
                    Path.Combine(root, "managed-workshop", "100"));
                SetNonPublicProperty(managedItem, "PluginRootPath",
                    Path.Combine(root, "managed-workshop", "100", "BepInEx", "plugins"));
                SetNonPublicProperty(managedItem, "DllCount", 1);
                var refreshedDiscovery = new WorkshopDiscoveryResult();
                SetNonPublicProperty(refreshedDiscovery, "Succeeded", true);
                refreshedDiscovery.Items.Add(managedItem);
                var successfulToggle = new WorkshopToggleResult();
                SetNonPublicProperty(successfulToggle, "Succeeded", true);
                SetNonPublicProperty(successfulToggle, "Changed", true);
                SetNonPublicProperty(successfulToggle, "IsEnabled", false);
                var workshopOperationOrder = new List<string>();
                string workshopUiError = null;
                SetPrivateField(form, "_isGameRunning", new Func<bool>(() => false));
                SetPrivateField(form, "_pluginScanner", new LocalPluginScanner());
                SetPrivateField(form, "_workshopToggleErrorPresenter",
                    new Action<string>(message => workshopUiError = message));
                SetPrivateField(form, "_workshopToggler",
                    new Func<string, string, bool, WorkshopToggleResult>((path, id, enabled) =>
                    {
                        workshopOperationOrder.Add("toggle");
                        Assert(string.Equals(path, gameRoot, StringComparison.OrdinalIgnoreCase) &&
                               id == "100" && !enabled,
                            "Workshop disable should pass the current game, ID, and requested state");
                        return successfulToggle;
                    }));
                SetPrivateField(form, "_workshopDiscoverer",
                    new Func<string, WorkshopDiscoveryResult>(path =>
                    {
                        workshopOperationOrder.Add("discover");
                        return refreshedDiscovery;
                    }));
                var toggleWorkshopUiMethod = typeof(MainForm).GetMethod(
                    "ToggleWorkshopPluginAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var toggleWorkshopUiTask = (Task)toggleWorkshopUiMethod.Invoke(form,
                    new object[] { connected });
                WaitForUiTask(toggleWorkshopUiTask,
                    "Workshop toggle UI should finish after background discovery and scanning");
                List<LocalPluginUnit> refreshedUnits = GetPrivateField<List<LocalPluginUnit>>(
                    form, "_localUnits");
                LocalPluginUnit refreshedWorkshop = refreshedUnits.Single(unit =>
                    unit.Source == LocalPluginSource.SteamWorkshop && unit.WorkshopId == "100");
                ModCard refreshedCard = flow.Controls.OfType<ModCard>().Single(card =>
                    card.LocalUnit != null && card.LocalUnit.WorkshopId == "100");
                Assert(string.IsNullOrEmpty(workshopUiError) &&
                       workshopOperationOrder.SequenceEqual(new[] { "toggle", "discover" }) &&
                       refreshedWorkshop.IsDisabled && !refreshedWorkshop.IsWorkshopConnected &&
                       refreshedCard.StatusText == "Steam 工坊 · 未收录 · 未启用",
                    "a successful Workshop disable must rediscover, rescan, and immediately rerender " +
                    "the item as visible but disabled; error=" + workshopUiError);

                var unindexed = new LocalPluginUnit
                {
                    UnitKey = ".workshop/999",
                    DisplayName = "Unindexed Connected",
                    DisplayVersion = "1.0.0",
                    RelativePath = "BepInEx\\plugins\\.workshop\\999",
                    EnabledRelativePath = "BepInEx\\plugins\\.workshop\\999",
                    IsDirectory = true,
                    Source = LocalPluginSource.SteamWorkshop,
                    WorkshopId = "999",
                    IsWorkshopSubscribed = true,
                    IsWorkshopDownloaded = true,
                    IsWorkshopConnected = true,
                    HasWorkshopManifest = true,
                    IsWorkshopPackageValid = true,
                    DllCount = 1,
                    Plugins = new List<ScannedPlugin>
                    {
                        new ScannedPlugin { Name = "Unindexed Connected", Version = "1.0.0" },
                    },
                };
                var local = new LocalPluginUnit
                {
                    UnitKey = "LocalOnly",
                    DisplayName = "Local Only",
                    DisplayVersion = "1.0.0",
                    RelativePath = "BepInEx\\plugins\\LocalOnly",
                    EnabledRelativePath = "BepInEx\\plugins\\LocalOnly",
                    IsDirectory = true,
                    Source = LocalPluginSource.Local,
                    DllCount = 1,
                    Plugins = new List<ScannedPlugin>
                    {
                        new ScannedPlugin { Name = "Local Only", Version = "1.0.0" },
                    },
                };
                SetPrivateField(form, "_index", CreateWorkshopIndex(
                    new ModEntry { id = "listed-connected", name = "Listed Connected", workshopId = "100" },
                    new ModEntry { id = "listed-unconnected", name = "Listed Unconnected", workshopId = "200" }));
                SetPrivateField(form, "_localUnits", new List<LocalPluginUnit>
                    { connected, unindexed, local });
                InvokePrivate(form, "RenderList");

                List<ModCard> cards = flow.Controls.OfType<ModCard>().ToList();
                Assert(cards.Count == 4,
                    "one listed+connected Workshop item must merge instead of producing a duplicate fifth card");
                ModCard merged = cards.Single(card =>
                    card.Entry != null && card.Entry.id == "listed-connected");
                Assert(ReferenceEquals(merged.LocalUnit, connected) &&
                       merged.StatusText == "Steam 工坊 · 已收录 · 已启用",
                    "RenderList should merge an indexed item with its matching Workshop unit by ID; " +
                    "sameUnit=" + ReferenceEquals(merged.LocalUnit, connected) +
                    ", status=" + merged.StatusText);
                Assert(cards.Single(card => card.Entry != null &&
                           card.Entry.id == "listed-unconnected").LocalUnit == null,
                    "an indexed item without a link should remain a single unconnected catalog card");
                Assert(cards.Count(card => card.LocalUnit != null &&
                           card.LocalUnit.WorkshopId == "999") == 1,
                    "an unindexed Workshop link should remain once in the Workshop section");
                Rectangle footerBoundsBeforeScroll = submissionFooter.Bounds;
                var listWheel = typeof(WheelFlowLayoutPanel).GetMethod("OnMouseWheel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                listWheel.Invoke(flow, new object[]
                {
                    new MouseEventArgs(MouseButtons.None, 0, 10, 10, -120),
                });
                Assert(submissionFooter.Bounds == footerBoundsBeforeScroll &&
                       flow.Bottom + 4 == submissionFooter.Top,
                    "wheel input on the Mod list must not move or overlap the fixed footer; " +
                    "flowBottom=" + flow.Bottom +
                    ", footerTop=" + submissionFooter.Top +
                    ", footerMoved=" + (submissionFooter.Bounds != footerBoundsBeforeScroll));
                string[] sectionTexts = flow.Controls.OfType<Label>()
                    .Select(label => label.Text).ToArray();
                Assert(sectionTexts.Contains("Steam 创意工坊目录") &&
                       sectionTexts.Contains("本地已安装插件"),
                    "merged rendering should retain clear Workshop and local section headings");
                Label workshopHeading = flow.Controls.OfType<Label>().Single(label =>
                    label.Text == "Steam 创意工坊目录");
                Label localHeading = flow.Controls.OfType<Label>().Single(label =>
                    label.Text == "本地已安装插件");
                ModCard unindexedCard = cards.Single(card => card.LocalUnit != null &&
                    card.LocalUnit.WorkshopId == "999");
                ModCard localCard = cards.Single(card => ReferenceEquals(card.LocalUnit, local));
                Assert(flow.Controls.IndexOf(workshopHeading) < flow.Controls.IndexOf(unindexedCard) &&
                       flow.Controls.IndexOf(unindexedCard) < flow.Controls.IndexOf(localHeading) &&
                       flow.Controls.IndexOf(localHeading) < flow.Controls.IndexOf(localCard),
                    "every Workshop source must render in the leading Workshop section, never below " +
                    "the local-plugin heading");

                List<Control> indexedRenderedControls =
                    flow.Controls.Cast<Control>().ToList();
                SetPrivateField(form, "_index", CreateWorkshopIndex());
                SetPrivateField(form, "_localUnits", new List<LocalPluginUnit>
                    { unindexed, local });
                InvokePrivate(form, "RenderList");
                Assert(indexedRenderedControls.All(control => control.IsDisposed),
                    "switching to an offline/empty index should dispose the previous rendered controls");
                List<ModCard> offlineCards = flow.Controls.OfType<ModCard>().ToList();
                string[] offlineSections = flow.Controls.OfType<Label>()
                    .Select(label => label.Text).ToArray();
                workshopHeading = flow.Controls.OfType<Label>().Single(label =>
                    label.Text == "Steam 创意工坊目录");
                localHeading = flow.Controls.OfType<Label>().Single(label =>
                    label.Text == "本地已安装插件");
                unindexedCard = offlineCards.Single(card =>
                    ReferenceEquals(card.LocalUnit, unindexed));
                localCard = offlineCards.Single(card => ReferenceEquals(card.LocalUnit, local));
                Assert(offlineSections.SequenceEqual(new[]
                       { "Steam 创意工坊目录", "本地已安装插件" }) &&
                       flow.Controls.IndexOf(workshopHeading) < flow.Controls.IndexOf(unindexedCard) &&
                       flow.Controls.IndexOf(unindexedCard) < flow.Controls.IndexOf(localHeading) &&
                       flow.Controls.IndexOf(localHeading) < flow.Controls.IndexOf(localCard),
                    "an unavailable central index must preserve a separate leading Workshop section " +
                    "for locally discovered subscriptions");

                List<Control> oldRenderedControls = flow.Controls.Cast<Control>().ToList();
                InvokePrivate(form, "RenderList");
                Assert(oldRenderedControls.All(control => control.IsDisposed),
                    "re-rendering should dispose removed cards and labels instead of leaking handles");
            }

            using (var disposedProgressForm = new MainForm())
            {
                SetPrivateField(disposedProgressForm, "_initializeOnShown", false);
                disposedProgressForm.Show();
                Application.DoEvents();
                disposedProgressForm.Dispose();
                MethodInfo onProgress = typeof(MainForm).GetMethod("OnProgress",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(onProgress != null,
                    "MainForm should retain a guarded installation progress callback");
                onProgress.Invoke(disposedProgressForm, new object[] { 50, "late callback" });
                Assert(disposedProgressForm.IsDisposed,
                    "a progress callback arriving after deferred close must be ignored safely");
            }
            using (var scrollForm = new MainForm())
            using (var scrollFlow = new WheelFlowLayoutPanel
            {
                Size = new Size(592, 300),
                AutoScroll = true,
                AutoScrollMinSize = new Size(0, 800),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            })
            {
                SetPrivateField(scrollForm, "_flow", scrollFlow);
                SetPrivateField(scrollForm, "_index", CreateWorkshopIndex(
                    Enumerable.Range(1, 8).Select(value => new ModEntry
                    {
                        id = "scroll-" + value,
                        name = "Scroll Workshop " + value,
                        workshopId = value.ToString(CultureInfo.InvariantCulture),
                    }).ToArray()));
                SetPrivateField(scrollForm, "_localUnits", new List<LocalPluginUnit>());
                scrollFlow.CreateControl();
                InvokePrivate(scrollForm, "RenderList");
                scrollFlow.PerformLayout();
                scrollFlow.AutoScrollPosition = new Point(0, 120);
                int requestedOffset = -scrollFlow.AutoScrollPosition.Y;
                Assert(requestedOffset == 120,
                    "scroll preservation fixture should establish a non-zero list position");

                List<Control> oldControls = scrollFlow.Controls.Cast<Control>().ToList();
                scrollFlow.AutoScrollPosition = Point.Empty;
                var renderAtScrollMethod = typeof(MainForm).GetMethod(
                    "RenderListAtScrollOffset", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(renderAtScrollMethod != null,
                    "MainForm should expose its scroll-preserving rerender implementation");
                renderAtScrollMethod.Invoke(scrollForm, new object[] { requestedOffset });
                Assert(oldControls.All(control => control.IsDisposed) &&
                       -scrollFlow.AutoScrollPosition.Y == requestedOffset,
                    "a toggle-style full rerender must restore the pre-operation list position");
            }

            using (var focusForm = new MainForm
            {
                ShowInTaskbar = false,
                Opacity = 0,
            })
            {
                SetPrivateField(focusForm, "_initializeOnShown", false);
                SetPrivateField(focusForm, "_index", CreateWorkshopIndex(
                    Enumerable.Range(20, 10).Select(value => new ModEntry
                    {
                        id = "focus-" + value,
                        name = "Focus Workshop " + value,
                        workshopId = value.ToString(CultureInfo.InvariantCulture),
                    }).ToArray()));
                SetPrivateField(focusForm, "_localUnits", new List<LocalPluginUnit>());
                InvokePrivate(focusForm, "RenderList");
                focusForm.Show();
                focusForm.Activate();
                Application.DoEvents();

                var focusFlow = GetControl<FlowLayoutPanel>(focusForm, "_flow");
                List<ModCard> focusCards = focusFlow.Controls.OfType<ModCard>().ToList();
                Button focusedCardButton = GetButton(focusCards[6], "_btnMain");
                focusedCardButton.Select();
                Application.DoEvents();
                int offsetBeforeBusy = -focusFlow.AutoScrollPosition.Y;
                Assert(focusedCardButton.Focused && focusFlow.ContainsFocus &&
                       offsetBeforeBusy > 0,
                    "focus-walk fixture should begin on a card button below the first viewport");

                foreach (ModCard card in focusCards) card.SetBusy(true);
                Application.DoEvents();
                int oldBehaviorOffset = -focusFlow.AutoScrollPosition.Y;
                Assert(oldBehaviorOffset > offsetBeforeBusy,
                    "the old direct-disable sequence should reproduce WinForms focus walking " +
                    "through later cards and scrolling the list downward");
                foreach (ModCard card in focusCards) card.SetBusy(false);
                focusedCardButton.Select();
                Application.DoEvents();
                offsetBeforeBusy = -focusFlow.AutoScrollPosition.Y;

                var setBusyMethod = typeof(MainForm).GetMethod("SetBusy",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                setBusyMethod.Invoke(focusForm, new object[] { true, "focus test" });
                Application.DoEvents();
                Assert(focusForm.ActiveControl == null && !focusFlow.ContainsFocus &&
                       -focusFlow.AutoScrollPosition.Y == offsetBeforeBusy,
                    "entering busy state must clear card focus before disabling " +
                    "buttons, preventing WinForms from walking focus and scrolling to the bottom");
                focusForm.Close();
                Application.DoEvents();
                Assert(!focusForm.IsDisposed && GetPrivateField<bool>(focusForm, "_closePending"),
                    "closing during a mutation must be deferred rather than abandoning it");
                setBusyMethod.Invoke(focusForm, new object[] { false, null });
                Application.DoEvents();
                Assert(focusForm.IsDisposed,
                    "a deferred close must complete immediately after the mutation leaves busy state");
            }
        }

        private static void RunWheelFlowLayoutPanelTests()
        {
            using (var panel = new WheelFlowLayoutPanel
            {
                Size = new Size(220, 100),
                AutoScroll = true,
                AutoScrollMinSize = new Size(0, 600),
            })
            {
                panel.CreateControl();
                panel.PerformLayout();
                Assert(panel.TabStop && panel.DisplayRectangle.Height > panel.ClientSize.Height,
                    "wheel panel test setup should have a scrollable vertical range");

                var onMouseWheel = typeof(WheelFlowLayoutPanel).GetMethod("OnMouseWheel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(onMouseWheel != null, "wheel-aware panel should override OnMouseWheel");
                int before = -panel.AutoScrollPosition.Y;
                onMouseWheel.Invoke(panel, new object[]
                {
                    new MouseEventArgs(MouseButtons.None, 0, 10, 10, -120),
                });
                int afterDown = -panel.AutoScrollPosition.Y;
                Assert(afterDown - before >= 24,
                    "one wheel notch should move by a usable pixel distance, not only a few pixels");
                onMouseWheel.Invoke(panel, new object[]
                {
                    new MouseEventArgs(MouseButtons.None, 0, 10, 10, 120),
                });
                Assert(-panel.AutoScrollPosition.Y < afterDown,
                    "mouse-wheel up should move the Mod list upward");

                for (int i = 0; i < 20; i++)
                    onMouseWheel.Invoke(panel, new object[]
                    {
                        new MouseEventArgs(MouseButtons.None, 0, 10, 10, -120),
                    });
                int maxOffset = Math.Max(0,
                    panel.DisplayRectangle.Height - panel.ClientSize.Height);
                Assert(-panel.AutoScrollPosition.Y == maxOffset,
                    "repeated wheel-down input should clamp exactly at the bottom");
                for (int i = 0; i < 20; i++)
                    onMouseWheel.Invoke(panel, new object[]
                    {
                        new MouseEventArgs(MouseButtons.None, 0, 10, 10, 120),
                    });
                Assert(-panel.AutoScrollPosition.Y == 0,
                    "repeated wheel-up input should clamp exactly at the top");
            }

            using (var shortPanel = new WheelFlowLayoutPanel
            {
                Size = new Size(220, 100),
                AutoScroll = true,
            })
            {
                shortPanel.CreateControl();
                var onMouseWheel = typeof(WheelFlowLayoutPanel).GetMethod("OnMouseWheel",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                onMouseWheel.Invoke(shortPanel, new object[]
                {
                    new MouseEventArgs(MouseButtons.None, 0, 10, 10, -120),
                });
                Assert(shortPanel.AutoScrollPosition.Y == 0,
                    "wheel input should be harmless when the list does not overflow");
            }
        }

        private static void RunModCardUiTests()
        {
            var workshopEntry = new ModEntry
            {
                id = "workshop-ui-test",
                name = "Workshop Test",
                description = "Steam-managed entry",
                version = "v1.0.0",
                workshopId = "1234",
            };

            using (var card = new ModCard())
            {
                var title = GetCardLabel(card, "_title");
                var description = GetCardLabel(card, "_desc");
                var source = GetCardLabel(card, "_status");
                var registration = GetCardLabel(card, "_statusRegistration");
                var state = GetCardLabel(card, "_statusState");
                var statusPanel = GetCardPanel(card, "_statusPanel");
                var main = GetButton(card, "_btnMain");
                var toggle = GetButton(card, "_btnToggle");

                string[] missingValues = { null, string.Empty, "   " };
                foreach (string missing in missingValues)
                {
                    workshopEntry.name = missing;
                    workshopEntry.description = missing;
                    card.Bind(workshopEntry);
                    Assert(title.Text == workshopEntry.id,
                        "missing Workshop names should display the internal ID");
                    Assert(description.Text == WorkshopMetadataService.DefaultDescription,
                        "missing Workshop descriptions should display the safe default");
                }

                workshopEntry.name = "Workshop Test";
                workshopEntry.description = "Steam-managed entry";
                card.Bind(workshopEntry);
                Assert(card.StatusText == "Steam 工坊 · 已收录 · 未启用",
                    "an indexed Workshop item without a Bridge link should use concise status text");
                Assert(IsPositive(registration.ForeColor) && IsNegative(state.ForeColor),
                    "已收录 should be green while 未启用 should be red");
                Assert(source.Left >= 0 && source.Right <= registration.Left &&
                       registration.Right <= state.Left &&
                       state.Right <= statusPanel.ClientSize.Width,
                    "status segments should remain right-aligned without overlap or overflow");
                Assert(main.Visible && main.Enabled && main.Text == "打开工坊页面",
                    "Workshop buttons must accurately state that they only open the page");
                string requestedWorkshopId = null;
                card.WorkshopPageClicked += id => requestedWorkshopId = id;
                RaiseButtonClick(main);
                Assert(requestedWorkshopId == "1234",
                    "the page button should emit the normalized Workshop ID without subscribing directly");
                Assert(!toggle.Visible,
                    "Workshop index cards must not expose local file toggles");

                card.SetBusy(true);
                Assert(!main.Enabled, "busy state should disable the Workshop page action");
                card.SetBusy(false);
                Assert(main.Enabled && !toggle.Visible,
                    "leaving busy should restore only the Workshop page action");

                var installedWorkshop = new LocalPluginUnit
                {
                    UnitKey = ".workshop/1234",
                    DisplayName = "Installed Workshop Test",
                    DisplayVersion = "2.0.0",
                    RelativePath = "BepInEx\\plugins\\.workshop\\1234",
                    EnabledRelativePath = "BepInEx\\plugins\\.workshop\\1234",
                    IsDirectory = true,
                    Source = LocalPluginSource.SteamWorkshop,
                    WorkshopId = "1234",
                    IsWorkshopSubscribed = true,
                    IsWorkshopDownloaded = true,
                    IsWorkshopConnected = true,
                    HasWorkshopManifest = true,
                    IsWorkshopPackageValid = true,
                    DllCount = 1,
                    Plugins = new List<ScannedPlugin>
                    {
                        new ScannedPlugin { Name = "Installed Workshop Test", Version = "2.0.0" },
                    },
                };
                card.Bind(workshopEntry, installedWorkshop);
                Assert(card.StatusText == "Steam 工坊 · 已收录 · 已启用",
                    "an indexed and connected Workshop item should be represented by one merged card");
                Assert(IsPositive(registration.ForeColor) && IsPositive(state.ForeColor),
                    "已收录 and 已启用 should both be green");
                Assert(description.Text.Contains("Steam-managed entry") &&
                        description.Text.Contains("2.0.0") &&
                        description.Text.Contains("ID 1234") &&
                        !description.Text.Contains("已订阅") &&
                        !description.Text.Contains("已下载"),
                    "merged Workshop cards should keep the index description plus version and ID, " +
                    "without repeating the status labels");
                Assert(main.Text == "打开工坊页面" && toggle.Visible && toggle.Enabled &&
                       toggle.Text == "禁用" && main.Right <= toggle.Left,
                    "merged Workshop cards should expose non-overlapping page and disable actions");

                int compactHeight = card.Height;
                Assert(main.Top >= description.Bottom,
                    "buttons must sit below the description area");
                workshopEntry.description = string.Concat(
                    Enumerable.Repeat("这是一段足够长的简介，用来验证描述自动换行。", 4));
                card.Bind(workshopEntry, installedWorkshop);
                Assert(card.Height > compactHeight && main.Top >= description.Bottom,
                    "long descriptions should wrap and grow the card instead of being cut off");
                workshopEntry.description = "Steam-managed entry";
                card.Bind(workshopEntry, installedWorkshop);
                Assert(card.Height == compactHeight,
                    "short descriptions should restore the compact card height");

                installedWorkshop.IsDisabled = true;
                installedWorkshop.IsWorkshopConnected = false;
                card.Bind(workshopEntry, installedWorkshop);
                Assert(card.StatusText == "Steam 工坊 · 已收录 · 未启用" &&
                       toggle.Visible && toggle.Enabled && toggle.Text == "启用",
                    "disabled subscribed Workshop items should remain visible and re-enableable");

                workshopEntry.workshopId = "   ";
                card.Bind(workshopEntry);
                card.SetBusy(true);
                card.SetBusy(false);
                Assert(card.StatusText == "Steam 工坊 · 信息无效" &&
                       main.Visible && !main.Enabled && main.Text == "工坊信息无效",
                    "invalid Workshop references must remain visibly blocked");
            }

            var localUnit = new LocalPluginUnit
            {
                UnitKey = "LocalExample",
                DisplayName = "Local Example",
                DisplayVersion = "1.2.3",
                RelativePath = "BepInEx\\plugins\\LocalExample",
                EnabledRelativePath = "BepInEx\\plugins\\LocalExample",
                IsDirectory = true,
                Source = LocalPluginSource.Local,
                DllCount = 2,
                Plugins = new List<ScannedPlugin>
                {
                    new ScannedPlugin
                    {
                        Guid = "example.local",
                        Name = "Local Example",
                        Version = "1.2.3",
                        DllFileName = "LocalExample.dll",
                    },
                },
            };

            using (var card = new ModCard())
            {
                var description = GetCardLabel(card, "_desc");
                var registration = GetCardLabel(card, "_statusRegistration");
                var state = GetCardLabel(card, "_statusState");
                var main = GetButton(card, "_btnMain");
                var toggle = GetButton(card, "_btnToggle");

                card.BindLocal(localUnit);
                Assert(card.StatusText == "本地 · 未收录 · 已启用",
                    "enabled local plugins should use concise source and state text");
                Assert(IsNeutral(registration.ForeColor) && IsPositive(state.ForeColor),
                    "未收录 should be neutral gray while 已启用 should be green");
                Assert(description.Text.Contains("1.2.3") &&
                       description.Text.Contains("BepInEx\\plugins\\LocalExample"),
                    "local cards should display version and path");
                Assert(!main.Visible && toggle.Visible && toggle.Enabled && toggle.Text == "禁用",
                    "enabled local plugins should expose only the disable action");
                Assert(typeof(ModCard).GetField("_btnUninstall",
                           BindingFlags.Instance | BindingFlags.NonPublic) == null,
                    "local unlisted plugins must not retain a delete action");

                card.SetBusy(true);
                Assert(!toggle.Enabled, "busy state should disable local toggles");
                card.SetBusy(false);
                Assert(toggle.Enabled && toggle.Text == "禁用",
                    "leaving busy should restore the local toggle");

                localUnit.IsDisabled = true;
                localUnit.RelativePath = "BepInEx\\ModManager\\disabled\\LocalExample";
                card.BindLocal(localUnit);
                Assert(card.StatusText == "本地 · 未收录 · 未启用" && toggle.Text == "启用",
                    "disabled local plugins should use the red 未启用 state");
                Assert(IsNeutral(registration.ForeColor) && IsNegative(state.ForeColor),
                    "未收录 should be neutral gray while 未启用 should be red");

                localUnit.HasPathConflict = true;
                localUnit.LastWriteTimeUtc = DateTime.UtcNow;
                card.BindLocal(localUnit);
                Assert(card.StatusText == "本地 · 未收录 · 路径冲突" &&
                       toggle.Visible && toggle.Enabled && toggle.Text == "启用" &&
                       description.Text.Contains("conflict-backup") &&
                       description.Text.Contains("更新于"),
                    "conflicting disabled copies must keep an enable action, explain the " +
                    "archive behaviour and show the copy's write time");

                localUnit.IsDisabled = false;
                localUnit.RelativePath = "BepInEx\\plugins\\LocalExample";
                card.BindLocal(localUnit);
                Assert(card.StatusText == "本地 · 未收录 · 路径冲突" &&
                       toggle.Visible && toggle.Enabled && toggle.Text == "禁用" &&
                       description.Text.Contains("conflict-backup"),
                    "conflicting enabled copies must keep a disable action and explain " +
                    "that the other copy gets archived");

                localUnit.HasPathConflict = false;
                localUnit.IsDisabled = false;
                localUnit.HasGuidConflict = true;
                card.BindLocal(localUnit);
                Assert(card.StatusText == "本地 · 未收录 · 重复 GUID" &&
                       toggle.Visible && toggle.Text == "禁用",
                    "duplicate GUID warnings should retain the disable action needed to resolve them");
            }

            var unindexedWorkshop = new LocalPluginUnit
            {
                UnitKey = ".workshop/987654",
                DisplayName = "Unindexed Workshop Plugin",
                DisplayVersion = "2.0.0",
                RelativePath = "BepInEx\\plugins\\.workshop\\987654",
                EnabledRelativePath = "BepInEx\\plugins\\.workshop\\987654",
                IsDirectory = true,
                Source = LocalPluginSource.SteamWorkshop,
                WorkshopId = "987654",
                IsWorkshopSubscribed = true,
                IsWorkshopDownloaded = true,
                IsWorkshopConnected = true,
                HasWorkshopManifest = true,
                IsWorkshopPackageValid = true,
                DllCount = 1,
                Plugins = new List<ScannedPlugin>
                {
                    new ScannedPlugin { Name = "Unindexed Workshop Plugin", Version = "2.0.0" },
                },
            };
            using (var card = new ModCard())
            {
                card.BindLocal(unindexedWorkshop);
                var registration = GetCardLabel(card, "_statusRegistration");
                var state = GetCardLabel(card, "_statusState");
                var main = GetButton(card, "_btnMain");
                var toggle = GetButton(card, "_btnToggle");
                Assert(card.StatusText == "Steam 工坊 · 未收录 · 已启用",
                    "unindexed connected Workshop plugins should use the requested concise status");
                Assert(IsNeutral(registration.ForeColor) && IsPositive(state.ForeColor),
                    "未收录 should be neutral gray while 已启用 should be green");
                Assert(main.Visible && main.Enabled && main.Text == "打开工坊页面" &&
                       toggle.Visible && toggle.Enabled && toggle.Text == "禁用",
                    "unindexed Workshop items should expose page and disable actions");
            }
        }

        private static bool IsPositive(Color color)
        {
            return color.ToArgb() == Color.FromArgb(45, 135, 70).ToArgb();
        }

        private static bool IsNegative(Color color)
        {
            return color.ToArgb() == Color.Firebrick.ToArgb();
        }

        private static bool IsNeutral(Color color)
        {
            return color.ToArgb() == Color.FromArgb(130, 130, 130).ToArgb();
        }


        private static void RunLocalPluginScannerTests(string root)
        {
            string gameRoot = Path.Combine(root, "StudentAge");
            string pluginsRoot = Path.Combine(gameRoot, "BepInEx", "plugins");
            Directory.CreateDirectory(pluginsRoot);

            var scanner = new LocalPluginScanner();
            Assert(scanner.Scan(gameRoot).Count == 0,
                "an empty plugins directory should produce no local plugin cards");

            string directoryUnit = Path.Combine(pluginsRoot, "DirectoryMod");
            Directory.CreateDirectory(directoryUnit);
            WritePluginAssembly(Path.Combine(directoryUnit, "DirectoryMod.dll"),
                "tests.directory", "Directory Mod", "1.0.0");
            File.WriteAllBytes(Path.Combine(directoryUnit, "NativeDependency.dll"),
                new byte[] { 0x4d, 0x5a, 0, 0 });

            string rootDll = Path.Combine(pluginsRoot, "RootMod.dll");
            WritePluginAssembly(rootDll, "tests.root", "Root Mod", "2.0.0");

            List<LocalPluginUnit> units = scanner.Scan(gameRoot);
            LocalPluginUnit directory = units.FirstOrDefault(unit =>
                unit.UnitKey == "DirectoryMod");
            LocalPluginUnit rootPlugin = units.FirstOrDefault(unit =>
                unit.UnitKey == "RootMod.dll");
            Assert(directory != null && directory.Source == LocalPluginSource.Local &&
                   directory.DisplayName == "Directory Mod" &&
                   directory.DisplayVersion == "1.0.0" && directory.DllCount == 2 &&
                   directory.Plugins.Count == 1 && directory.LastWriteTimeUtc != null,
                "directory plugins should be grouped with dependencies, read BepInPlugin metadata " +
                "and carry the newest DLL write time");
            Assert(rootPlugin != null && !rootPlugin.IsDirectory &&
                   rootPlugin.DisplayName == "Root Mod" && rootPlugin.DisplayVersion == "2.0.0",
                "a root-level plugin DLL should be represented as an independent local unit");

            AssertScannerWorksWithInternetZoneMarker(scanner, gameRoot, "RootMod.dll");
            AssertScannerWorksWhenManagerIsRenamed(root, gameRoot, "RootMod.dll");

            var manager = new LocalPluginManager(new LocalState(gameRoot));
            manager.Disable(directory);
            Assert(!Directory.Exists(directoryUnit) &&
                   Directory.Exists(Path.Combine(gameRoot, "BepInEx", "ModManager",
                       "disabled", "DirectoryMod")),
                "disabling should move the complete local directory without deleting it");

            LocalPluginUnit disabled = scanner.Scan(gameRoot).FirstOrDefault(unit =>
                unit.UnitKey == "DirectoryMod" && unit.IsDisabled);
            Assert(disabled != null && disabled.Source == LocalPluginSource.Local,
                "disabled and former direct-install directories should be unified as local unlisted plugins");
            manager.Enable(disabled);
            Assert(Directory.Exists(directoryUnit),
                "enabling should restore a disabled local unit to BepInEx/plugins");

            string disabledRoot = Path.Combine(gameRoot, "BepInEx", "ModManager", "disabled");
            Directory.CreateDirectory(disabledRoot);
            string rootConflict = Path.Combine(disabledRoot, "RootMod.dll");
            File.Copy(rootDll, rootConflict);
            List<LocalPluginUnit> conflictingUnits = scanner.Scan(gameRoot)
                .Where(unit => unit.UnitKey == "RootMod.dll").ToList();
            Assert(conflictingUnits.Count == 2 &&
                   conflictingUnits.All(unit => unit.HasPathConflict),
                "enabled and disabled same-name plugins should be visibly marked as a path conflict");
            long rootLength = new FileInfo(rootDll).Length;
            string staleCardArchive = manager.Disable(rootPlugin);
            Assert(staleCardArchive != null && File.Exists(staleCardArchive) &&
                   !File.Exists(rootDll) && File.Exists(rootConflict),
                "a stale card without HasPathConflict must revalidate and archive the live target under lock");
            File.Copy(rootConflict, rootDll);
            conflictingUnits = scanner.Scan(gameRoot)
                .Where(unit => unit.UnitKey == "RootMod.dll").ToList();

            // 冲突不再是死胡同：带 HasPathConflict 标记的单元可以照常禁用，
            // 目标位置的旧副本先被整体归档到 conflict-backup，从不覆盖丢失。
            using (var stream = new FileStream(rootConflict, FileMode.Append, FileAccess.Write))
                stream.WriteByte(0); // 让旧副本长度可区分，验证归档的是哪一份。
            long staleLength = rootLength + 1;
            LocalPluginUnit conflictedEnabled =
                conflictingUnits.Single(unit => !unit.IsDisabled);
            string conflictBackupRoot = Path.Combine(gameRoot, "BepInEx", "ModManager",
                "conflict-backup");
            string archivedStale = manager.Disable(conflictedEnabled);
            Assert(archivedStale != null && File.Exists(archivedStale) &&
                   new FileInfo(archivedStale).Length == staleLength &&
                   archivedStale.StartsWith(conflictBackupRoot,
                       StringComparison.OrdinalIgnoreCase),
                "conflict-aware disable must archive the stale disabled copy into conflict-backup");
            Assert(!File.Exists(rootDll) && File.Exists(rootConflict) &&
                   new FileInfo(rootConflict).Length == rootLength,
                "conflict-aware disable must then move the enabled copy into the disabled slot");

            // 反向：冲突下的“启用”把 plugins 现用副本归档、让禁用副本回位。
            File.Copy(rootConflict, rootDll);
            List<LocalPluginUnit> reconflicted = scanner.Scan(gameRoot)
                .Where(unit => unit.UnitKey == "RootMod.dll").ToList();
            Assert(reconflicted.Count == 2 && reconflicted.All(unit => unit.HasPathConflict),
                "recreating the duplicate should re-flag both copies as a path conflict");
            using (var stream = new FileStream(rootDll, FileMode.Append, FileAccess.Write))
            {
                stream.WriteByte(0);
                stream.WriteByte(0);
            }
            LocalPluginUnit conflictedDisabled =
                reconflicted.Single(unit => unit.IsDisabled);
            string archivedActive = manager.Enable(conflictedDisabled);
            Assert(archivedActive != null && File.Exists(archivedActive) &&
                   new FileInfo(archivedActive).Length == rootLength + 2 &&
                   !string.Equals(archivedActive, archivedStale,
                       StringComparison.OrdinalIgnoreCase),
                "conflict-aware enable must archive the active plugins copy " +
                "without touching earlier archives");
            Assert(File.Exists(rootDll) && !File.Exists(rootConflict) &&
                   new FileInfo(rootDll).Length == rootLength,
                "conflict-aware enable must restore the disabled copy into BepInEx/plugins");
            File.Copy(rootDll, rootConflict);
            LocalPluginUnit rollbackUnit = scanner.Scan(gameRoot)
                .Single(unit => unit.UnitKey == "RootMod.dll" && !unit.IsDisabled);
            long rollbackSourceLength = new FileInfo(rootDll).Length;
            long rollbackTargetLength = new FileInfo(rootConflict).Length;
            SetPrivateField(manager, "_beforeSourceMove", (Action<string>)(path =>
                { throw new IOException("injected after archive"); }));
            AssertThrows<IOException>(() => manager.Disable(rollbackUnit),
                "a failure after conflict archive must surface after conditional rollback");
            Assert(File.Exists(rootDll) && File.Exists(rootConflict) &&
                   new FileInfo(rootDll).Length == rollbackSourceLength &&
                   new FileInfo(rootConflict).Length == rollbackTargetLength,
                "failure after archive must restore the old target without losing the source copy");
            SetPrivateField(manager, "_beforeSourceMove", null);
            File.Delete(rootConflict);

            manager.Disable(rootPlugin);
            Assert(!File.Exists(rootDll) && File.Exists(rootConflict),
                "root-level DLL units should use the file move branch when disabled");
            LocalPluginUnit disabledRootPlugin = scanner.Scan(gameRoot).FirstOrDefault(unit =>
                unit.UnitKey == "RootMod.dll" && unit.IsDisabled);
            Assert(disabledRootPlugin != null && !disabledRootPlugin.IsDirectory,
                "disabled root-level DLLs should remain discoverable");
            manager.Enable(disabledRootPlugin);
            Assert(File.Exists(rootDll) && !File.Exists(rootConflict),
                "root-level DLL units should be restored without copying or deleting data");

            string fakeGameExe = Path.Combine(root, "StudentAge.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fakeGameExe, true);
            Process fakeGame = Process.Start(new ProcessStartInfo
            {
                FileName = fakeGameExe,
                Arguments = "-t 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            try
            {
                var wait = Stopwatch.StartNew();
                while (!ModInstaller.IsGameRunning() && wait.Elapsed < TimeSpan.FromSeconds(3))
                    Thread.Sleep(25);
                Assert(ModInstaller.IsGameRunning(),
                    "test setup should expose a process named StudentAge");
                AssertThrows<InvalidOperationException>(() => manager.Disable(rootPlugin),
                    "local plugin moves must be rejected while the game is running");
                Assert(File.Exists(rootDll),
                    "game-running rejection must leave the plugin in place");
            }
            finally
            {
                if (fakeGame != null && !fakeGame.HasExited)
                {
                    fakeGame.Kill();
                    fakeGame.WaitForExit();
                }
                if (fakeGame != null) fakeGame.Dispose();
            }

            string workshopTarget = Path.Combine(root, "workshop-content", "987654",
                "BepInEx", "plugins");
            string workshopPluginDir = Path.Combine(workshopTarget, "WorkshopMod");
            Directory.CreateDirectory(workshopPluginDir);
            WritePluginAssembly(Path.Combine(workshopPluginDir, "WorkshopMod.dll"),
                "tests.workshop", "Workshop Mod", "3.0.0");
            string workshopRoot = Path.Combine(pluginsRoot, ".workshop");
            Directory.CreateDirectory(workshopRoot);
            string workshopLink = Path.Combine(workshopRoot, "987654");
            Assert(CreateJunction(workshopLink, workshopTarget),
                "test setup should be able to create a Workshop-style directory junction");

            units = scanner.Scan(gameRoot);
            LocalPluginUnit workshop = units.FirstOrDefault(unit =>
                unit.Source == LocalPluginSource.SteamWorkshop && unit.WorkshopId == "987654");
            Assert(workshop != null && workshop.DisplayName == "Workshop Mod" &&
                   workshop.DisplayVersion == "3.0.0" && !workshop.IsDisabled,
                "Bridge junction plugins should be identified as installed Steam Workshop content");
            AssertThrows<InvalidOperationException>(() => manager.Disable(workshop),
                "the local manager must never move Workshop Bridge junctions");
            Assert(units.TakeWhile(unit => unit.Source == LocalPluginSource.Local).Count() == 2 &&
                   units.Last().Source == LocalPluginSource.SteamWorkshop,
                "scan results should sort local units before Workshop units");

            string duplicateDirectory = Path.Combine(pluginsRoot, "DuplicateWorkshopMod");
            WritePluginAssembly(Path.Combine(duplicateDirectory, "DuplicateWorkshopMod.dll"),
                "tests.workshop", "Duplicate Workshop Mod", "3.0.0");
            units = scanner.Scan(gameRoot);
            LocalPluginUnit duplicateLocal = units.Single(unit =>
                unit.UnitKey == "DuplicateWorkshopMod");
            workshop = units.Single(unit =>
                unit.Source == LocalPluginSource.SteamWorkshop && unit.WorkshopId == "987654");
            Assert(duplicateLocal.HasGuidConflict && workshop.HasGuidConflict &&
                   duplicateLocal.ConflictingPluginGuids.Contains("tests.workshop") &&
                   workshop.ConflictingPluginGuids.Contains("tests.workshop"),
                "an enabled local copy and connected Workshop copy with the same BepInPlugin GUID " +
                "must both show a duplicate-GUID warning");

            manager.Disable(duplicateLocal);
            units = scanner.Scan(gameRoot);
            duplicateLocal = units.Single(unit => unit.UnitKey == "DuplicateWorkshopMod");
            workshop = units.Single(unit =>
                unit.Source == LocalPluginSource.SteamWorkshop && unit.WorkshopId == "987654");
            Assert(duplicateLocal.IsDisabled && !duplicateLocal.HasGuidConflict &&
                   !workshop.HasGuidConflict,
                "disabling either duplicate load candidate should clear the GUID conflict on rescan");

            string ordinaryWorkshopDir = Path.Combine(workshopRoot, "123456");
            Directory.CreateDirectory(ordinaryWorkshopDir);
            WritePluginAssembly(Path.Combine(ordinaryWorkshopDir, "Ordinary.dll"),
                "tests.ordinary", "Ordinary Workshop Directory", "1.0.0");
            string nonCanonicalLink = Path.Combine(workshopRoot, "000123");
            Assert(CreateJunction(nonCanonicalLink, workshopTarget),
                "test setup should create a non-canonical Workshop junction");
            units = scanner.Scan(gameRoot);
            Assert(!units.Any(unit => unit.WorkshopId == "123456" ||
                                      unit.WorkshopId == "000123"),
                "ordinary Workshop directories and non-canonical IDs must be ignored");

            string hugeDll = Path.Combine(pluginsRoot, "Huge.dll");
            using (var huge = new FileStream(hugeDll, FileMode.Create, FileAccess.Write,
                FileShare.None))
                huge.SetLength(128L * 1024L * 1024L + 1L);
            Assert(!scanner.Scan(gameRoot).Any(unit => unit.UnitKey == "Huge.dll"),
                "DLLs above the metadata size limit must be skipped without loading");

            string unsafeGame = Path.Combine(root, "unsafe-workshop-root");
            string unsafePlugins = Path.Combine(unsafeGame, "BepInEx", "plugins");
            string unsafeTarget = Path.Combine(root, "unsafe-workshop-target");
            Directory.CreateDirectory(unsafePlugins);
            Directory.CreateDirectory(Path.Combine(unsafeTarget, "555"));
            WritePluginAssembly(Path.Combine(unsafeTarget, "555", "Unsafe.dll"),
                "tests.unsafe", "Unsafe Workshop Root", "1.0.0");
            Assert(CreateJunction(Path.Combine(unsafePlugins, ".workshop"), unsafeTarget),
                "test setup should create a reparse-point Workshop root");
            Assert(!scanner.Scan(unsafeGame).Any(unit =>
                    unit.Source == LocalPluginSource.SteamWorkshop),
                "a reparse-point .workshop root must be rejected as a whole");

            string suspiciousTarget = Path.Combine(root, "suspicious-target");
            Directory.CreateDirectory(suspiciousTarget);
            WritePluginAssembly(Path.Combine(suspiciousTarget, "Suspicious.dll"),
                "tests.suspicious", "Suspicious", "1.0.0");
            string suspiciousLink = Path.Combine(pluginsRoot, "SuspiciousLink");
            Assert(CreateJunction(suspiciousLink, suspiciousTarget),
                "test setup should create an arbitrary reparse point");
            Assert(!scanner.Scan(gameRoot).Any(unit => unit.UnitKey == "SuspiciousLink"),
                "non-Workshop reparse points must not be followed or displayed");
        }

        private static void AssertScannerWorksWithInternetZoneMarker(
            LocalPluginScanner scanner, string gameRoot, string expectedUnitKey)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

            string hostPath = typeof(LocalPluginScanner).Assembly.Location;
            string zoneStreamPath = hostPath + ":Zone.Identifier";
            byte[] previousZone = null;
            bool hadPreviousZone = false;
            try
            {
                previousZone = ReadAlternateDataStream(zoneStreamPath, out hadPreviousZone);

                WriteAlternateDataStream(zoneStreamPath,
                    Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"));
                Assert(scanner.Scan(gameRoot).Any(unit => unit.UnitKey == expectedUnitKey),
                    "plugin scanning must work when the browser-downloaded manager carries MOTW");
            }
            finally
            {
                if (hadPreviousZone)
                    WriteAlternateDataStream(zoneStreamPath, previousZone);
                else
                    DeleteFile(zoneStreamPath);
            }
        }

        private static void AssertScannerWorksWhenManagerIsRenamed(string root,
            string gameRoot, string expectedUnitKey)
        {
            string hostRoot = Path.Combine(root, "renamed-manager-host");
            Directory.CreateDirectory(hostRoot);
            string renamedManagerPath = Path.Combine(hostRoot, "ModManager (4).exe");
            File.Copy(typeof(LocalPluginScanner).Assembly.Location, renamedManagerPath, true);

            Assert(ScanWithIsolatedManagerHost(renamedManagerPath, gameRoot, expectedUnitKey),
                "plugin scanning must use the running manager bytes when its executable " +
                "has been renamed and no canonical ModManager.exe exists beside it");

            // A stale or hostile same-name sibling must not decide which probe runs. A malformed
            // assembly is enough to prove that activation uses the trusted self-copy by path.
            File.WriteAllBytes(Path.Combine(hostRoot, "ModManager.exe"),
                new byte[] { 0x4d, 0x5a, 0, 0 });
            Assert(ScanWithIsolatedManagerHost(renamedManagerPath, gameRoot, expectedUnitKey),
                "plugin scanning must ignore an unrelated sibling ModManager.exe when the " +
                "running executable has a browser-added suffix");
        }

        private static bool ScanWithIsolatedManagerHost(string managerPath, string gameRoot,
            string expectedUnitKey)
        {
            string testHostDirectory = Path.GetDirectoryName(
                typeof(RenamedManagerScannerHost).Assembly.Location);
            var setup = new AppDomainSetup
            {
                ApplicationBase = testHostDirectory,
                ShadowCopyFiles = "false",
            };
            AppDomain domain = AppDomain.CreateDomain(
                "StudentAge.RenamedManagerTest." + Guid.NewGuid().ToString("N"), null, setup);
            try
            {
                var host = (RenamedManagerScannerHost)domain.CreateInstanceAndUnwrap(
                    typeof(RenamedManagerScannerHost).Assembly.FullName,
                    typeof(RenamedManagerScannerHost).FullName);
                return host.ScanContains(managerPath, gameRoot, expectedUnitKey);
            }
            finally
            {
                AppDomain.Unload(domain);
            }
        }

        private static byte[] ReadAlternateDataStream(string path, out bool exists)
        {
            using (SafeFileHandle handle = CreateFile(path, GenericRead, FileShareAll,
                IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorFileNotFound || error == ErrorPathNotFound)
                    {
                        exists = false;
                        return null;
                    }
                    throw new Win32Exception(error, "无法读取备用数据流: " + path);
                }

                exists = true;
                using (var stream = new FileStream(handle, FileAccess.Read))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }

        private static void WriteAlternateDataStream(string path, byte[] bytes)
        {
            using (SafeFileHandle handle = CreateFile(path, GenericWrite, FileShareAll,
                IntPtr.Zero, CreateAlways, FileAttributeNormal, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "无法写入备用数据流: " + path);
                using (var stream = new FileStream(handle, FileAccess.Write))
                    stream.Write(bytes, 0, bytes.Length);
            }
        }

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareAll = 0x00000007;
        private const uint CreateAlways = 2;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string fileName);

        private static void WritePluginAssembly(string path, string guid, string name,
            string version)
        {
            string directory = Path.GetDirectoryName(path);
            string fileName = Path.GetFileName(path);
            Directory.CreateDirectory(directory);
            string assemblyName = Path.GetFileNameWithoutExtension(fileName) + "." +
                                  Guid.NewGuid().ToString("N");
            var builder = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName(assemblyName),
                System.Reflection.Emit.AssemblyBuilderAccess.Save, directory);
            var module = builder.DefineDynamicModule(assemblyName, fileName);

            var attributeBuilder = module.DefineType("BepInEx.BepInPlugin",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
                typeof(Attribute));
            var constructorBuilder = attributeBuilder.DefineConstructor(
                MethodAttributes.Public, CallingConventions.Standard,
                new[] { typeof(string), typeof(string), typeof(string) });
            var il = constructorBuilder.GetILGenerator();
            var attributeBaseConstructor = typeof(Attribute).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Call, attributeBaseConstructor);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            Type attributeType = attributeBuilder.CreateType();

            var pluginBuilder = module.DefineType("Fixture.Plugin",
                TypeAttributes.Public | TypeAttributes.Class, typeof(object));
            var attribute = new System.Reflection.Emit.CustomAttributeBuilder(
                attributeType.GetConstructor(new[]
                    { typeof(string), typeof(string), typeof(string) }),
                new object[] { guid, name, version });
            pluginBuilder.SetCustomAttribute(attribute);
            pluginBuilder.CreateType();
            builder.Save(fileName);
        }

        private static bool CreateJunction(string link, string target)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c mklink /J \"" + link + "\" \"" + target + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process process = Process.Start(startInfo))
            {
                process.WaitForExit();
                return process.ExitCode == 0 && Directory.Exists(link) &&
                       (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
        }


        private static void AssertTextFits(Label label, string message)
        {
            var measured = TextRenderer.MeasureText(label.Text, label.Font,
                new Size(label.ClientSize.Width, int.MaxValue),
                TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak);
            Assert(measured.Width <= label.ClientSize.Width &&
                   measured.Height <= label.ClientSize.Height,
                message + "; measured=" + measured.Width + "x" + measured.Height +
                ", available=" + label.ClientSize.Width + "x" + label.ClientSize.Height);
        }

        private static void AssertSingleLineFits(Label label, string message)
        {
            var measured = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Assert(measured.Width <= label.ClientSize.Width &&
                   measured.Height <= label.ClientSize.Height,
                message + "; measured=" + measured.Width + "x" + measured.Height +
                ", available=" + label.ClientSize.Width + "x" + label.ClientSize.Height);
        }

        private static T GetControl<T>(MainForm form, string fieldName) where T : Control
        {
            var field = typeof(MainForm).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing MainForm field: " + fieldName);
            var control = field.GetValue(form) as T;
            Assert(control != null, "MainForm field has the wrong control type: " + fieldName);
            return control;
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            var field = instance.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing private field: " + fieldName);
            field.SetValue(instance, value);
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing private field: " + fieldName);
            object value = field.GetValue(instance);
            Assert(value is T, "private field has the wrong type: " + fieldName);
            return (T)value;
        }

        private static void SetNonPublicProperty(object instance, string propertyName,
            object value)
        {
            var property = instance.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert(property != null, "missing property: " + propertyName);
            property.SetValue(instance, value, null);
        }

        private static void InvokePrivate(object instance, string methodName)
        {
            var method = instance.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(method != null, "missing private method: " + methodName);
            method.Invoke(instance, null);
        }

        private static Button GetButton(ModCard card, string fieldName)
        {
            var field = typeof(ModCard).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing ModCard field: " + fieldName);
            var button = field.GetValue(card) as Button;
            Assert(button != null, "ModCard field is not a button: " + fieldName);
            return button;
        }

        private static void RaiseButtonClick(Button button)
        {
            var method = typeof(Button).GetMethod("OnClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(method != null, "Button.OnClick should be available for UI tests");
            method.Invoke(button, new object[] { EventArgs.Empty });
        }

        private static void WaitForUiTask(Task task, string message)
        {
            var wait = Stopwatch.StartNew();
            while (!task.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            Assert(task.IsCompleted, message);
            task.GetAwaiter().GetResult();
        }

        private static Label GetCardLabel(ModCard card, string fieldName)
        {
            var field = typeof(ModCard).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing ModCard field: " + fieldName);
            var label = field.GetValue(card) as Label;
            Assert(label != null, "ModCard field is not a label: " + fieldName);
            return label;
        }

        private static Panel GetCardPanel(ModCard card, string fieldName)
        {
            var field = typeof(ModCard).GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(field != null, "missing ModCard field: " + fieldName);
            var panel = field.GetValue(card) as Panel;
            Assert(panel != null, "ModCard field is not a panel: " + fieldName);
            return panel;
        }

        private static ModIndex CreateWorkshopIndex(params ModEntry[] entries)
        {
            return new ModIndex
            {
                schemaVersion = 1,
                mods = new List<ModEntry>(entries ?? new ModEntry[0]),
            };
        }

        private static WorkshopMetadata CreateMetadata(string workshopId, string title,
            string description, uint appId = WorkshopMetadataService.StudentAgeAppId)
        {
            return new WorkshopMetadata
            {
                WorkshopId = workshopId,
                ConsumerAppId = appId,
                Title = title,
                Description = description,
            };
        }

        private static WorkshopMetadataBatchResult BatchWith(
            params WorkshopMetadata[] metadataItems)
        {
            var result = new WorkshopMetadataBatchResult();
            foreach (WorkshopMetadata metadata in metadataItems)
                result.Items.Add(metadata.WorkshopId, metadata);
            return result;
        }

        private static string BuildSteamSuccessResponse(IList<string> ids)
        {
            var details = new string[ids.Count];
            for (int i = 0; i < ids.Count; i++)
                details[i] = BuildSteamDetail(ids[i], "Title " + ids[i],
                    "Description " + ids[i], WorkshopMetadataService.StudentAgeAppId);
            return BuildSteamResponseFromDetails(details);
        }

        private static string BuildSteamDetail(string id, string title, string description,
            uint appId)
        {
            return "{\"publishedfileid\":" + JsonQuote(id) +
                   ",\"result\":1,\"consumer_app_id\":" +
                   appId.ToString(CultureInfo.InvariantCulture) +
                   ",\"title\":" + JsonQuote(title) +
                   ",\"description\":" + JsonQuote(description) + "}";
        }

        private static string BuildSteamResponseFromDetails(params string[] details)
        {
            return "{\"response\":{\"result\":1,\"publishedfiledetails\":[" +
                   string.Join(",", details ?? new string[0]) + "]}}";
        }

        private static IList<string> ReadRequestedIds(NameValueCollection values)
        {
            int count = int.Parse(values["itemcount"], CultureInfo.InvariantCulture);
            var ids = new List<string>(count);
            for (int i = 0; i < count; i++)
                ids.Add(values["publishedfileids[" + i.ToString(
                    CultureInfo.InvariantCulture) + "]"]);
            return ids;
        }

        private static string JsonQuote(string value)
        {
            if (value == null) return "null";
            var output = new StringBuilder(value.Length + 2);
            output.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            output.Append("\\u").Append(((int)c).ToString("x4",
                                CultureInfo.InvariantCulture));
                        else
                            output.Append(c);
                        break;
                }
            }
            output.Append('"');
            return output.ToString();
        }

        private static bool HasUnpairedSurrogate(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                        return true;
                    i++;
                }
                else if (char.IsLowSurrogate(value[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Assertion failed: " + message +
                    "; expected " + typeof(TException).Name + " but got " +
                    ex.GetType().Name + ": " + ex.Message, ex);
            }
            throw new InvalidOperationException("Assertion failed: " + message +
                "; expected " + typeof(TException).Name + " but no exception was thrown");
        }

        private sealed class FakeWorkshopMetadataProvider : IWorkshopMetadataProvider
        {
            public Func<IList<string>, WorkshopMetadataBatchResult> Handler { get; set; }
            public List<List<string>> Calls { get; } = new List<List<string>>();

            public async Task<WorkshopMetadataBatchResult> GetDetailsAsync(
                IList<string> workshopIds,
                CancellationToken ct = default(CancellationToken))
            {
                ct.ThrowIfCancellationRequested();
                var copy = new List<string>(workshopIds);
                Calls.Add(copy);
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                if (Handler == null)
                    throw new InvalidOperationException("Fake Workshop provider has no handler.");
                return Handler(copy);
            }
        }

        private sealed class FakeTransportCall
        {
            public string Endpoint { get; set; }
            public NameValueCollection Values { get; set; }
            public TimeSpan Timeout { get; set; }
            public int MaxResponseBytes { get; set; }
        }

        private sealed class FakeWorkshopMetadataTransport : IWorkshopMetadataTransport
        {
            public Func<FakeTransportCall, byte[]> Handler { get; set; }
            public List<FakeTransportCall> Calls { get; } = new List<FakeTransportCall>();

            public async Task<byte[]> PostFormAsync(string endpoint, NameValueCollection values,
                TimeSpan timeout, int maxResponseBytes,
                CancellationToken ct = default(CancellationToken))
            {
                ct.ThrowIfCancellationRequested();
                var call = new FakeTransportCall
                {
                    Endpoint = endpoint,
                    Values = new NameValueCollection(values),
                    Timeout = timeout,
                    MaxResponseBytes = maxResponseBytes,
                };
                Calls.Add(call);
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                if (Handler == null)
                    throw new InvalidOperationException("Fake Workshop transport has no handler.");
                return Handler(call);
            }
        }


        private static string HashEmbeddedBridge()
        {
            using (var stream = typeof(ModInstaller).Assembly.GetManifestResourceStream(BridgeResourceName))
            {
                Assert(stream != null, "ModManager.exe should contain the bridge resource");
                using (var sha256 = SHA256.Create())
                    return Convert.ToBase64String(sha256.ComputeHash(stream));
            }
        }

        private static string HashEmbeddedBepInExPackage()
        {
            using (var stream = typeof(ModInstaller).Assembly
                .GetManifestResourceStream(BepInExResourceName))
            {
                Assert(stream != null, "ModManager.exe should contain the BepInEx package");
                using (var sha256 = SHA256.Create())
                    return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
            }
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha256 = SHA256.Create())
                return Convert.ToBase64String(sha256.ComputeHash(stream));
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }
    }

    /// <summary>
    /// Loads a manager copy by its physical path inside a clean AppDomain. This keeps the
    /// regression test honest: the statically referenced canonical ModManager.exe from the test
    /// output cannot satisfy the renamed manager's own metadata-domain binding.
    /// </summary>
    public sealed class RenamedManagerScannerHost : MarshalByRefObject
    {
        public bool ScanContains(string managerPath, string gameRoot, string expectedUnitKey)
        {
            string expectedManagerPath = Path.GetFullPath(managerPath);
            Assembly managerAssembly = Assembly.LoadFrom(expectedManagerPath);
            if (!string.Equals(Path.GetFullPath(managerAssembly.Location), expectedManagerPath,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test manager resolved from an unexpected path: " +
                    managerAssembly.Location);

            Type scannerType = managerAssembly.GetType(
                "StudentAgeModManager.Core.LocalPluginScanner", true);
            object scanner = Activator.CreateInstance(scannerType);
            MethodInfo scanMethod = scannerType.GetMethod("Scan", new[] { typeof(string) });
            object units;
            try
            {
                units = scanMethod.Invoke(scanner, new object[] { gameRoot });
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException != null) throw ex.InnerException;
                throw;
            }

            foreach (object unit in (IEnumerable)units)
            {
                PropertyInfo unitKeyProperty = unit.GetType().GetProperty("UnitKey");
                string unitKey = unitKeyProperty?.GetValue(unit, null) as string;
                if (string.Equals(unitKey, expectedUnitKey, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }
    }
}
