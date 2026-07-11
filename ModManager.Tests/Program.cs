using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using StudentAgeModManager.Core;

namespace StudentAgeModManager.Tests
{
    internal static class Program
    {
        private const string BridgeResourceName =
            "StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll";

        private static int Main()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(),
                "StudentAgeModManager.Tests." + Guid.NewGuid().ToString("N"));
            try
            {
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
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private static void Run(string tempRoot)
        {
            string workshopId;
            var workshopEntry = new ModEntry { id = "workshop-test", workshopId = " 001234 " };
            Assert(WorkshopItem.IsDeclared(workshopEntry), "non-empty workshop ID should select workshop flow");
            Assert(WorkshopItem.TryGetId(workshopEntry, out workshopId) && workshopId == "1234",
                "workshop ID should be validated and normalized");
            Assert(WorkshopItem.PageUrl(workshopId).EndsWith("?id=1234"),
                "workshop URL should use only the normalized numeric ID");
            workshopEntry.workshopId = "../plugin.dll";
            Assert(WorkshopItem.IsDeclared(workshopEntry) && !WorkshopItem.TryGetId(workshopEntry, out workshopId),
                "an invalid declared workshop ID must not fall back to direct DLL installation");
            workshopEntry.workshopId = "   ";
            Assert(WorkshopItem.IsDeclared(workshopEntry) && !WorkshopItem.TryGetId(workshopEntry, out workshopId),
                "whitespace workshop ID must remain a blocked workshop declaration");
            workshopEntry.workshopId = string.Empty;
            Assert(!WorkshopItem.IsDeclared(workshopEntry),
                "an empty workshop ID should preserve legacy direct-download compatibility");
            workshopEntry.workshopId = "1234";
            workshopEntry.installDir = string.Empty;
            Assert(new LocalState(tempRoot).GetStatus(workshopEntry) == ModStatus.NotInstalled,
                "workshop-only index entries should not require a legacy installDir");

            var gameRoot = Path.Combine(tempRoot, "StudentAge");
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "core"));
            File.WriteAllBytes(Path.Combine(gameRoot, "winhttp.dll"), new byte[] { 1 });

            var installer = new ModInstaller(new LocalState(gameRoot), new Downloader());
            Assert(installer.IsBepInExInstalled(), "fake BepInEx installation should be detected");

            bool directInstallBlocked = false;
            try
            {
                installer.InstallAsync(workshopEntry, null).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                directInstallBlocked = ex.Message.Contains("Steam");
            }
            Assert(directInstallBlocked,
                "installer API must reject direct downloads for every declared workshop item");

            Assert(!installer.IsWorkshopBridgeInstalled(), "bridge should initially be absent");
            Assert(!installer.IsWorkshopBridgeCurrent(), "absent bridge cannot be current");

            installer.InstallWorkshopBridge();
            Assert(File.Exists(installer.WorkshopBridgePath), "bridge should be extracted to patchers");
            Assert(installer.IsWorkshopBridgeCurrent(), "freshly extracted bridge should be current");
            Assert(HashEmbeddedBridge() == HashFile(installer.WorkshopBridgePath),
                "extracted bridge must exactly match the embedded resource");

            File.AppendAllText(installer.WorkshopBridgePath, "corrupt");
            Assert(!installer.IsWorkshopBridgeCurrent(), "modified bridge should be reported as stale");

            installer.InstallWorkshopBridge();
            Assert(installer.IsWorkshopBridgeCurrent(), "repair should restore the embedded bridge");
            Assert(!File.Exists(installer.WorkshopBridgePath + ".tmp"),
                "temporary extraction file should be cleaned up");
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
}
