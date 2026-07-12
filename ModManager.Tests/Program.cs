using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using StudentAgeModManager;
using StudentAgeModManager.Core;

namespace StudentAgeModManager.Tests
{
    internal static class Program
    {
        private const string BridgeResourceName =
            "StudentAgeModManager.Resources.StudentAge.WorkshopBridge.dll";

        [STAThread]
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
            RunMainFormUiTests(Path.Combine(tempRoot, "main-form-ui"));
            RunModCardUiTests();

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
            var bridgeVersion = FileVersionInfo.GetVersionInfo(installer.WorkshopBridgePath);
            Assert(bridgeVersion.ProductVersion == "0.2.0" &&
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
            Assert(!File.Exists(installer.WorkshopBridgePath + ".tmp"),
                "temporary extraction file should be cleaned up");
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
                var banner = GetControl<Panel>(form, "_banner");
                var flow = GetControl<FlowLayoutPanel>(form, "_flow");
                var status = GetControl<Label>(form, "_lblStatus");

                Assert(setupTitle.Text == "第一次使用前的准备" && setupTitle.Font.Bold,
                    "workshop guide should use a clear first-use heading");
                Assert(setupText.Text.Contains("点击“一键安装完整前置”即可") &&
                       setupText.Text.Contains("已有订阅不会自动开启"),
                    "first-use text should explain the one-click setup and preserve existing subscriptions");
                Assert(setupText.Text.Contains("支持本功能的 DLL Mod") &&
                       setupText.Text.Contains("Steam 下载完成后，下次启动游戏") &&
                       setupText.Text.Contains("自动启用并生效"),
                    "first-use text should accurately explain when supported DLL mods become active");
                Assert(manageTitle.Text == "如何管理 Mod" && manageTitle.Font.Bold,
                    "workshop guide should use a clear management heading");
                Assert(manageText.Text.Contains("Steam 创意工坊") &&
                       manageText.Text.Contains("更新由 Steam 自动完成"),
                    "management text should explain Steam subscription and update ownership");
                Assert(manageText.Text.Contains("游戏“本地”页") &&
                       manageText.Text.Contains("重启后生效") &&
                       manageText.Text.Contains("手动关闭后不会被再次自动开启"),
                    "management text should explain the native switch and persistent manual disable");
                AssertTextFits(setupText,
                    "first-use instructions must fit without clipping");
                AssertTextFits(manageText,
                    "management instructions must fit without clipping");
                Assert(setupTitle.Bottom <= setupText.Top &&
                       setupText.Bottom <= manageTitle.Top &&
                       manageTitle.Bottom <= manageText.Top &&
                       manageText.Bottom <= guide.ClientSize.Height,
                    "workshop guide sections must not overlap or leave the panel");
                Assert(guide.Bottom <= banner.Top,
                    "workshop guide must not overlap the BepInEx banner");
                Assert(guide.Bottom <= flow.Top && flow.Bottom <= status.Top,
                    "initial workshop guide/list/status layout must not overlap");

                var gameRoot = Path.Combine(root, "StudentAge");
                Directory.CreateDirectory(gameRoot);
                var installer = new ModInstaller(new LocalState(gameRoot), new Downloader());
                SetPrivateField(form, "_installer", installer);

                InvokePrivate(form, "UpdateBepInExUi");
                Assert(flow.Top == 234 && banner.Bottom <= flow.Top,
                    "visible prerequisite banner must remain above the mod list");
                Assert(flow.Bottom <= status.Top,
                    "banner-visible mod list must remain above the status bar");

                Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "core"));
                File.WriteAllBytes(Path.Combine(gameRoot, "winhttp.dll"), new byte[] { 1 });
                installer.InstallWorkshopBridge();
                InvokePrivate(form, "UpdateBepInExUi");
                Assert(flow.Top == 196 && guide.Bottom <= flow.Top,
                    "hidden prerequisite banner must leave the mod list below the guide");
                Assert(flow.Bottom <= status.Top,
                    "banner-hidden mod list must remain above the status bar");
            }
        }

        private static void RunModCardUiTests()
        {
            var legacyEntry = new ModEntry
            {
                id = "legacy-test",
                name = "Legacy Test",
                description = "Legacy direct-install entry",
                repo = "owner/repository",
                version = "v2.0.0",
                installDir = "BepInEx/plugins/LegacyTest",
            };

            using (var card = new ModCard())
            {
                var main = GetButton(card, "_btnMain");
                var toggle = GetButton(card, "_btnToggle");
                var uninstall = GetButton(card, "_btnUninstall");
                var home = GetButton(card, "_btnHome");

                card.Bind(legacyEntry, ModStatus.UpdateAvailable, "v1.0.0");
                Assert(main.Enabled && toggle.Enabled && uninstall.Enabled && home.Enabled,
                    "legacy update actions should initially be enabled");
                Assert(main.Visible && toggle.Visible && uninstall.Visible && home.Visible,
                    "legacy update entry should expose update, toggle, uninstall, and home actions");

                card.SetBusy(true);
                Assert(!main.Enabled && !toggle.Enabled && !uninstall.Enabled && !home.Enabled,
                    "busy state should disable every legacy card action");
                card.SetBusy(false);
                Assert(main.Enabled && toggle.Enabled && uninstall.Enabled && home.Enabled,
                    "leaving busy state should restore legacy update actions");

                card.Bind(legacyEntry, ModStatus.UpToDate, "v2.0.0");
                card.SetBusy(true);
                card.SetBusy(false);
                Assert(!main.Enabled && toggle.Enabled && uninstall.Enabled && home.Enabled,
                    "up-to-date main action must remain disabled after busy state");

                card.Bind(legacyEntry, ModStatus.Disabled, "v2.0.0");
                card.SetBusy(true);
                card.SetBusy(false);
                Assert(!main.Enabled && toggle.Enabled && uninstall.Enabled && home.Enabled,
                    "disabled entry must require re-enable before updating after busy state");
                Assert(toggle.Text == "启用", "disabled entry should restore its enable action");
            }

            var workshopEntry = new ModEntry
            {
                id = "workshop-ui-test",
                name = "Workshop Test",
                description = "Steam-managed entry",
                repo = "owner/repository",
                version = "v1.0.0",
                installDir = "BepInEx/plugins/WorkshopTest",
                workshopId = "1234",
            };

            using (var card = new ModCard())
            {
                var main = GetButton(card, "_btnMain");
                var toggle = GetButton(card, "_btnToggle");
                var uninstall = GetButton(card, "_btnUninstall");
                var home = GetButton(card, "_btnHome");

                card.Bind(workshopEntry, ModStatus.NotInstalled, null);
                Assert(main.Visible && main.Enabled && main.Text == "订阅 / 查看工坊",
                    "normal workshop entry should expose only the Steam action");
                Assert(!toggle.Visible && !uninstall.Visible && !home.Visible,
                    "normal workshop entry must hide legacy management actions");

                card.SetBusy(true);
                Assert(!main.Enabled, "busy state should disable the Steam action");
                card.SetBusy(false);
                Assert(main.Enabled && !toggle.Visible && !uninstall.Visible && !home.Visible,
                    "workshop card should restore only its Steam action after busy state");

                card.Bind(workshopEntry, ModStatus.InstalledUnknown, null);
                Assert(main.Visible && main.Enabled && uninstall.Visible && uninstall.Enabled,
                    "workshop entry with a legacy install should expose Steam and cleanup actions");
                Assert(uninstall.Text == "清理旧安装",
                    "workshop legacy cleanup action should use explicit wording");
                Assert(!toggle.Visible && !home.Visible,
                    "workshop cleanup state must still hide toggle and home actions");

                card.SetBusy(true);
                card.SetBusy(false);
                Assert(main.Enabled && uninstall.Enabled && !toggle.Visible && !home.Visible,
                    "workshop cleanup actions should restore without revealing legacy-only actions");

                workshopEntry.workshopId = "   ";
                card.Bind(workshopEntry, ModStatus.NotInstalled, null);
                card.SetBusy(true);
                card.SetBusy(false);
                Assert(main.Visible && !main.Enabled && main.Text == "工坊 ID 无效",
                    "invalid declared workshop ID must remain visibly blocked after busy state");
                Assert(!toggle.Visible && !toggle.Enabled &&
                       !uninstall.Visible && !uninstall.Enabled &&
                       !home.Visible && !home.Enabled,
                    "invalid workshop entry must not expose any legacy fallback action");
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
