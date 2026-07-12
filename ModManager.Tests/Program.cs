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
        private static int Main(string[] args)
        {
            string tempRoot = null;
            try
            {
                if (args != null && args.Length > 0)
                    return RunCommand(args);

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

        private static int RunCommand(string[] args)
        {
            if (args.Length != 2 ||
                !string.Equals(args[0], "--validate-index", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Usage: StudentAgeModManager.Tests --validate-index <path>");
                return 2;
            }

            string path = Path.GetFullPath(args[1]);
            ModIndex index = IndexClient.ParseAndValidate(File.ReadAllText(path));
            Console.WriteLine("Index validation passed: " + path + " (" +
                index.mods.Count + " mods).");
            return 0;
        }

        private static void Run(string tempRoot)
        {
            RunMainFormUiTests(Path.Combine(tempRoot, "main-form-ui"));
            RunModCardUiTests();
            RunWorkshopReferenceTests();
            RunIndexValidationTests();

            var workshopEntry = new ModEntry { id = "workshop-test", workshopId = "1234" };
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

        private static void RunWorkshopReferenceTests()
        {
            string workshopId;
            Assert(!WorkshopItem.TryGetId(null, out workshopId) && workshopId == null,
                "null entries must be rejected without leaving an unassigned ID");

            var legacyNull = new ModEntry { workshopId = null };
            var legacyEmpty = new ModEntry { workshopId = string.Empty };
            Assert(!WorkshopItem.IsDeclared(legacyNull) && !WorkshopItem.IsDeclared(legacyEmpty),
                "null and empty workshop IDs should preserve legacy direct-download compatibility");

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
            AssertPageUrlRejected("001234");
            AssertPageUrlRejected("0");
            AssertPageUrlRejected("https://steamcommunity.com/sharedfiles/filedetails/?id=1234");
        }

        private static void RunIndexValidationTests()
        {
            const string validIndex =
                "{\"schemaVersion\":1,\"mods\":[" +
                "{\"id\":\"Numeric\",\"workshopId\":\" 000123 \"}," +
                "{\"id\":\"Url\",\"workshopId\":\"https://steamcommunity.com/workshop/filedetails/?id=000456&source=pr\"}," +
                "{\"id\":\"Legacy\",\"downloadUrl\":\"https://example.invalid/plugin.dll\"}," +
                "{\"id\":\"LegacyEmpty\",\"workshopId\":\"\"}," +
                "{\"id\":\"LegacyNull\",\"workshopId\":null}]}";
            ModIndex index = IndexClient.ParseAndValidate(validIndex);
            Assert(index.mods.Count == 5, "a valid mixed index should keep every entry");
            Assert(index.mods[0].workshopId == "123" && index.mods[1].workshopId == "456",
                "index validation must normalize numeric and URL workshop references in memory");
            Assert(!WorkshopItem.IsDeclared(index.mods[2]) &&
                   !WorkshopItem.IsDeclared(index.mods[3]) &&
                   !WorkshopItem.IsDeclared(index.mods[4]),
                "entries with an omitted, empty, or null workshopId must remain legacy entries");

            AssertInvalidIndex(
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"Example\"},{\"id\":\"example\"}]}",
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
                "{\"schemaVersion\":1,\"mods\":[{\"id\":\"Good\"},null]}",
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
            bool rejected = false;
            try
            {
                WorkshopItem.PageUrl(value);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Assert(rejected, "PageUrl must reject non-canonical input: " + value);
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
                Assert(main.Visible && !main.Enabled && main.Text == "工坊信息无效",
                    "invalid declared workshop reference must remain visibly blocked after busy state");
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
