using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
            var legacyEnv = new AutoTestEnvironment(Path.Combine(tempRoot, "legacy-sync"), 32);
            var gameRoot = legacyEnv.GameRoot;
            var workshopRoot = legacyEnv.WorkshopRoot;
            var pluginRoot = legacyEnv.PluginRoot;
            var activeList = legacyEnv.ActiveListPath;

            CreateWorkshopDllMod(workshopRoot, "100", withMarker: true);
            CreateWorkshopDllMod(workshopRoot, "200", withMarker: false);
            CreateWorkshopDllMod(workshopRoot, "300", withMarker: true);
            File.WriteAllText(Path.Combine(workshopRoot, "300", BridgeOptions.DefaultMarkerFileName),
                "{\"schemaVersion\":1,\"type\":\"bepinex-plugin\",\"pluginRoot\":\"../outside\"}");
            File.WriteAllText(activeList, "100\r\n200\r\n300\r\ninvalid\r\n0\r\n100\r\n");
            legacyEnv.WriteMetadata(
                WorkshopRecord.Current("100", legacyEnv.AccountId, installed: true),
                WorkshopRecord.Current("200", legacyEnv.AccountId, installed: true),
                WorkshopRecord.Current("300", legacyEnv.AccountId, installed: true));

            var options = legacyEnv.Options;

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
                WorkshopMetadataPath = legacyEnv.MetadataPath,
                ActiveModListPath = activeList,
                AutoEnableStatePath = legacyEnv.StatePath,
                ActiveSteamAccountId = legacyEnv.AccountId,
                ActiveSteamId64 = legacyEnv.SteamId64,
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

            TestAutoEnableLifecycle(Path.Combine(tempRoot, "auto-lifecycle"));
            TestCurrentUserSubscriptionFiltering(Path.Combine(tempRoot, "subscription-filter"));
            TestAutoEnableFiltersAndDownloadReadiness(Path.Combine(tempRoot, "auto-filters"));
            TestAutoEnableUserIsolation(Path.Combine(tempRoot, "auto-users"));
            TestAutoEnableFailClosedState(Path.Combine(tempRoot, "auto-state"));
            TestAutoEnableWriteFailureAndExistingId(Path.Combine(tempRoot, "auto-write"));
        }

        private static void TestAutoEnableLifecycle(string root)
        {
            var env = new AutoTestEnvironment(root, 42);
            CreateWorkshopDllMod(env.WorkshopRoot, "100", withMarker: true);
            CreateWorkshopDllMod(env.WorkshopRoot, "110", withMarker: false);
            env.WriteMetadata(
                WorkshopRecord.Current("100", env.AccountId, installed: true),
                WorkshopRecord.Current("110", env.AccountId, installed: true),
                WorkshopRecord.Current("120", env.AccountId, installed: false));
            File.WriteAllText(env.ActiveListPath, "900\r\n");

            var baseline = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(baseline.BaselineIdCount == 3,
                "first run should baseline every current subscription, not only DLL items");
            Assert(baseline.AutoEnabledIdCount == 0,
                "first run must not auto-enable existing subscriptions");
            AssertActiveIds(env.ActiveListPath, "900");
            Assert(StateContainsId(env.StatePath, "100") && StateContainsId(env.StatePath, "110") &&
                   StateContainsId(env.StatePath, "120"),
                "per-user baseline should remember downloaded and not-yet-downloaded subscriptions");

            CreateWorkshopDllMod(env.WorkshopRoot, "200", withMarker: true);
            CreateWorkshopDllMod(env.WorkshopRoot, "120", withMarker: true);
            env.WriteMetadata(
                WorkshopRecord.Current("100", env.AccountId, installed: true),
                WorkshopRecord.Current("110", env.AccountId, installed: true),
                WorkshopRecord.Current("120", env.AccountId, installed: true),
                WorkshopRecord.Current("200", env.AccountId, installed: true));

            var added = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(added.AutoEnabledIdCount == 1,
                "a newly subscribed, downloaded, valid DLL item should be added to _mod");
            AssertActiveIds(env.ActiveListPath, "200", "900");
            Assert(CountActiveId(env.ActiveListPath, "120") == 0,
                "a baseline subscription must stay off even if its download completes later");
            var link = Path.Combine(env.PluginRoot,
                WorkshopBridgeSynchronizer.LinkDirectoryName, "200");
            Assert(Directory.Exists(link) &&
                (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
                "a newly auto-enabled item should be linked in the same synchronization");

            File.WriteAllText(env.ActiveListPath, "900\r\n");
            var manuallyDisabled = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(manuallyDisabled.AutoEnabledIdCount == 0,
                "a manually disabled item must not be auto-enabled again");
            AssertActiveIds(env.ActiveListPath, "900");
            Assert(!Directory.Exists(link), "manual disable should remove the DLL link");

            env.WriteMetadata(
                WorkshopRecord.Current("100", env.AccountId, installed: true),
                WorkshopRecord.Current("110", env.AccountId, installed: true),
                WorkshopRecord.Current("120", env.AccountId, installed: true));
            WorkshopBridgeSynchronizer.Synchronize(env.Options);
            env.WriteMetadata(
                WorkshopRecord.Current("100", env.AccountId, installed: true),
                WorkshopRecord.Current("110", env.AccountId, installed: true),
                WorkshopRecord.Current("120", env.AccountId, installed: true),
                WorkshopRecord.Current("200", env.AccountId, installed: true));
            var resubscribed = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(resubscribed.AutoEnabledIdCount == 0,
                "unsubscribing and resubscribing the same Workshop ID must not restore it");
            AssertActiveIds(env.ActiveListPath, "900");
        }

        private static void TestCurrentUserSubscriptionFiltering(string root)
        {
            var env = new AutoTestEnvironment(root, 43);
            File.WriteAllText(env.ActiveListPath, string.Empty);
            env.WriteMetadata();
            WorkshopBridgeSynchronizer.Synchronize(env.Options);

            CreateWorkshopDllMod(env.WorkshopRoot, "100", withMarker: true);
            CreateWorkshopDllMod(env.WorkshopRoot, "200", withMarker: true);
            env.WriteMetadata(
                WorkshopRecord.Current("100", env.AccountId, installed: true),
                WorkshopRecord.Current("200", env.AccountId, installed: true));
            var enabled = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(enabled.AutoEnabledIdCount == 2 && enabled.LinkedCount == 2,
                "new DLL subscriptions should be enabled and linked before filtering is tested");
            AssertActiveIds(env.ActiveListPath, "100", "200");

            var link100 = Path.Combine(env.PluginRoot,
                WorkshopBridgeSynchronizer.LinkDirectoryName, "100");
            var link200 = Path.Combine(env.PluginRoot,
                WorkshopBridgeSynchronizer.LinkDirectoryName, "200");

            env.WriteMetadata(
                WorkshopRecord.Current("100", env.AccountId, installed: true,
                    latestManifest: "9999999"),
                WorkshopRecord.Current("200", env.AccountId, installed: false));
            var incomplete = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(incomplete.Synchronized && incomplete.LinkedCount == 0 &&
                   incomplete.SkippedCount == 2 && !Directory.Exists(link100) &&
                   !Directory.Exists(link200),
                "subscribed items must not link until Steam reports a complete matching download");

            File.Delete(env.MetadataPath);
            var missingMetadata = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(!missingMetadata.Synchronized && !Directory.Exists(link100) &&
                   !Directory.Exists(link200),
                "missing subscription metadata must remove stale links and fail closed");
            AssertActiveIds(env.ActiveListPath, "100", "200");

            env.WriteMetadata(
                WorkshopRecord.Current("100", env.AccountId, installed: true),
                WorkshopRecord.Current("200", env.AccountId, installed: true));
            var relinked = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(relinked.LinkedCount == 2 && Directory.Exists(link100) &&
                   Directory.Exists(link200),
                "valid metadata should restore links for still-enabled current subscriptions");

            // Steam can leave installed content and the native _mod entry behind after an
            // unsubscribe. Neither residue nor another account's subscribedby is authority.
            env.WriteMetadata(
                WorkshopRecord.Current("100", null, installed: true),
                WorkshopRecord.Current("200", "999", installed: true));
            var filtered = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(filtered.Synchronized && filtered.LinkedCount == 0 &&
                   filtered.SkippedCount == 2,
                "installed items not subscribed by the current account must be skipped");
            Assert(!Directory.Exists(link100) && !Directory.Exists(link200),
                "unsubscribed and other-user items must not retain Bridge links");
            AssertActiveIds(env.ActiveListPath, "100", "200");
            Assert(File.Exists(Path.Combine(env.WorkshopRoot, "100", "BepInEx", "plugins",
                    "ProbeMod", "Probe.dll")),
                "subscription filtering must never delete Steam Workshop source files");
        }

        private static void TestAutoEnableFiltersAndDownloadReadiness(string root)
        {
            var env = new AutoTestEnvironment(root, 52);
            File.WriteAllText(env.ActiveListPath, string.Empty);
            env.WriteMetadata();
            WorkshopBridgeSynchronizer.Synchronize(env.Options);

            CreateWorkshopDllMod(env.WorkshopRoot, "200", withMarker: false);
            CreateWorkshopDllMod(env.WorkshopRoot, "300", withMarker: true);
            File.WriteAllText(Path.Combine(env.WorkshopRoot, "300",
                    BridgeOptions.DefaultMarkerFileName),
                "{\"schemaVersion\":1,\"type\":\"bepinex-plugin\",\"pluginRoot\":\"../outside\"}");
            CreateWorkshopItemWithoutDll(env.WorkshopRoot, "400");
            CreateWorkshopDllMod(env.WorkshopRoot, "600", withMarker: true);
            CreateWorkshopDllMod(env.WorkshopRoot, "700", withMarker: true);
            CreateWorkshopDllMod(env.WorkshopRoot, "800", withMarker: true);
            CreateWorkshopDllMod(env.WorkshopRoot, "900", withMarker: true);
            CreateWorkshopDllMod(env.WorkshopRoot, "1000", withMarker: false);
            var manifestReparseTarget = Path.Combine(root, "manifest-reparse-target");
            Directory.CreateDirectory(manifestReparseTarget);
            File.WriteAllText(Path.Combine(manifestReparseTarget, "keep.txt"), "do not touch");
            var manifestReparsePath = Path.Combine(env.WorkshopRoot, "1000",
                BridgeOptions.DefaultMarkerFileName);
            CreateJunction(manifestReparsePath, manifestReparseTarget);

            env.WriteMetadata(
                WorkshopRecord.Current("200", env.AccountId, installed: true),
                WorkshopRecord.Current("300", env.AccountId, installed: true),
                WorkshopRecord.Current("400", env.AccountId, installed: true),
                WorkshopRecord.Current("500", env.AccountId, installed: true),
                WorkshopRecord.Current("600", env.AccountId, installed: false),
                WorkshopRecord.Current("700", "999", installed: true),
                WorkshopRecord.Current("800", null, installed: true),
                WorkshopRecord.Current("900", env.AccountId, installed: true,
                    latestManifest: "9999999"),
                WorkshopRecord.Current("1000", env.AccountId, installed: true));

            var filtered = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(filtered.AutoEnabledIdCount == 0,
                "ordinary, invalid, incomplete, other-user and not-downloaded items must stay off");
            AssertActiveIds(env.ActiveListPath);
            Assert(StateContainsId(env.StatePath, "200") &&
                   StateContainsId(env.StatePath, "300") &&
                   StateContainsId(env.StatePath, "400") &&
                   StateContainsId(env.StatePath, "1000"),
                "completed ordinary, invalid, or reparse-manifest packages should be marked seen safely");
            Assert(!StateContainsId(env.StatePath, "500") &&
                   !StateContainsId(env.StatePath, "600") &&
                   !StateContainsId(env.StatePath, "900"),
                "missing or not fully downloaded packages should remain retryable");
            Assert(!StateContainsId(env.StatePath, "700") && !StateContainsId(env.StatePath, "800"),
                "items not subscribed by the exact current account must not enter its state");
            Assert(Directory.Exists(manifestReparsePath) &&
                   File.Exists(Path.Combine(manifestReparseTarget, "keep.txt")),
                "a manifest reparse point must be rejected without mutating its target");
            Directory.Delete(manifestReparsePath, false);

            // Once a completed ordinary/invalid package has been observed, later changing it
            // into executable content must not trigger surprise code execution.
            File.WriteAllText(Path.Combine(env.WorkshopRoot, "200",
                    BridgeOptions.DefaultMarkerFileName), ValidManifestJson());
            File.WriteAllText(Path.Combine(env.WorkshopRoot, "300",
                    BridgeOptions.DefaultMarkerFileName), ValidManifestJson());
            File.WriteAllText(Path.Combine(env.WorkshopRoot, "400", "BepInEx", "plugins", "Probe.dll"),
                "test assembly placeholder");
            CreateWorkshopDllMod(env.WorkshopRoot, "500", withMarker: true);

            env.WriteMetadata(
                WorkshopRecord.Current("200", env.AccountId, installed: true),
                WorkshopRecord.Current("300", env.AccountId, installed: true),
                WorkshopRecord.Current("400", env.AccountId, installed: true),
                WorkshopRecord.Current("500", env.AccountId, installed: true),
                WorkshopRecord.Current("600", env.AccountId, installed: true),
                WorkshopRecord.Current("700", "999", installed: true),
                WorkshopRecord.Current("800", null, installed: true),
                WorkshopRecord.Current("900", env.AccountId, installed: true));

            var ready = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(ready.AutoEnabledIdCount == 3,
                "valid packages should auto-enable after their files and matching manifests are complete");
            AssertActiveIds(env.ActiveListPath, "500", "600", "900");
        }

        private static void TestAutoEnableUserIsolation(string root)
        {
            var userA = new AutoTestEnvironment(root, 62);
            var userB = userA.CreateUser(63);
            CreateWorkshopDllMod(userA.WorkshopRoot, "100", withMarker: true);
            CreateWorkshopDllMod(userA.WorkshopRoot, "200", withMarker: true);
            CreateWorkshopDllMod(userA.WorkshopRoot, "300", withMarker: true);
            CreateWorkshopDllMod(userA.WorkshopRoot, "400", withMarker: true);
            File.WriteAllText(userA.ActiveListPath, string.Empty);
            File.WriteAllText(userB.ActiveListPath, string.Empty);

            userA.WriteMetadata(WorkshopRecord.Current("100", userA.AccountId, installed: true));
            WorkshopBridgeSynchronizer.Synchronize(userA.Options);
            userA.WriteMetadata(
                WorkshopRecord.Current("100", userA.AccountId, installed: true),
                WorkshopRecord.Current("200", userA.AccountId, installed: true));
            WorkshopBridgeSynchronizer.Synchronize(userA.Options);
            AssertActiveIds(userA.ActiveListPath, "200");

            userB.WriteMetadata(
                WorkshopRecord.Current("100", userB.AccountId, installed: true),
                WorkshopRecord.Current("200", userB.AccountId, installed: true));
            var userBBaseline = WorkshopBridgeSynchronizer.Synchronize(userB.Options);
            Assert(userBBaseline.BaselineIdCount == 2 && userBBaseline.AutoEnabledIdCount == 0,
                "a second Steam user must receive an independent first-run baseline");
            AssertActiveIds(userB.ActiveListPath);

            userB.WriteMetadata(
                WorkshopRecord.Current("100", userB.AccountId, installed: true),
                WorkshopRecord.Current("200", userB.AccountId, installed: true),
                WorkshopRecord.Current("300", userB.AccountId, installed: true));
            WorkshopBridgeSynchronizer.Synchronize(userB.Options);
            AssertActiveIds(userB.ActiveListPath, "300");
            AssertActiveIds(userA.ActiveListPath, "200");

            // Even if a state file is copied across profiles, its embedded account identity
            // must prevent it from authorizing executable content for another Steam user.
            File.Copy(userA.StatePath, userB.StatePath, true);
            userB.WriteMetadata(
                WorkshopRecord.Current("100", userB.AccountId, installed: true),
                WorkshopRecord.Current("200", userB.AccountId, installed: true),
                WorkshopRecord.Current("300", userB.AccountId, installed: true),
                WorkshopRecord.Current("400", userB.AccountId, installed: true));
            var mismatchedState = WorkshopBridgeSynchronizer.Synchronize(userB.Options);
            Assert(mismatchedState.ErrorCount > 0 && mismatchedState.AutoEnabledIdCount == 0,
                "state belonging to another Steam account must fail closed");
            AssertActiveIds(userB.ActiveListPath, "300");
        }

        private static void TestAutoEnableFailClosedState(string root)
        {
            var env = new AutoTestEnvironment(Path.Combine(root, "metadata"), 72);
            CreateWorkshopDllMod(env.WorkshopRoot, "100", withMarker: true);
            File.WriteAllText(env.ActiveListPath, string.Empty);

            if (File.Exists(env.MetadataPath)) File.Delete(env.MetadataPath);
            WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(!File.Exists(env.StatePath),
                "missing Workshop ACF must not silently create an empty baseline");

            File.WriteAllText(env.MetadataPath,
                "\"AppWorkshop\"\r\n{\r\n\"WorkshopItemDetails\" { \"100\" { \"subscribedby\" \"72\" } }");
            WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(!File.Exists(env.StatePath),
                "corrupt Workshop ACF must fail closed without creating state");

            SetFileLength(env.MetadataPath, 16L * 1024 * 1024 + 1);
            var oversizedMetadata = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(!oversizedMetadata.Synchronized && !File.Exists(env.StatePath),
                "Workshop ACF files over 16 MiB must fail closed without creating state");

            env.WriteMetadata(WorkshopRecord.Current("100", env.AccountId, installed: true));
            var recoveredBaseline = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(recoveredBaseline.BaselineIdCount == 1 && recoveredBaseline.AutoEnabledIdCount == 0,
                "after metadata recovers, current subscriptions must still become a baseline");
            AssertActiveIds(env.ActiveListPath);

            File.WriteAllText(env.ActiveListPath, "100\r\n");
            var linkedBeforeCorruption = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            var existingLink = Path.Combine(env.PluginRoot,
                WorkshopBridgeSynchronizer.LinkDirectoryName, "100");
            Assert(linkedBeforeCorruption.LinkedCount == 1 && Directory.Exists(existingLink),
                "a valid per-user state should permit an explicitly enabled current subscription");

            CreateWorkshopDllMod(env.WorkshopRoot, "200", withMarker: true);
            env.WriteMetadata(
                WorkshopRecord.Current("100", env.AccountId, installed: true),
                WorkshopRecord.Current("200", env.AccountId, installed: true));
            File.WriteAllText(env.StatePath, "{broken json");
            var corruptState = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(!corruptState.Synchronized && corruptState.ErrorCount > 0 &&
                   corruptState.AutoEnabledIdCount == 0,
                "corrupt per-user state must fail the entire DLL synchronization closed");
            Assert(!Directory.Exists(existingLink),
                "corrupt per-user state must remove an existing Bridge link");
            AssertActiveIds(env.ActiveListPath, "100");

            SetFileLength(env.StatePath, 1024L * 1024 + 1);
            var oversizedState = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(!oversizedState.Synchronized && oversizedState.ErrorCount > 0,
                "per-user state files over 1 MiB must fail the entire DLL synchronization closed");

            var reparseEnv = new AutoTestEnvironment(Path.Combine(root, "reparse"), 73);
            File.WriteAllText(reparseEnv.ActiveListPath, string.Empty);
            reparseEnv.WriteMetadata();
            WorkshopBridgeSynchronizer.Synchronize(reparseEnv.Options);
            File.Delete(reparseEnv.StatePath);
            CreateWorkshopDllMod(reparseEnv.WorkshopRoot, "300", withMarker: true);
            reparseEnv.WriteMetadata(
                WorkshopRecord.Current("300", reparseEnv.AccountId, installed: true));
            var externalStateTarget = Path.Combine(root, "external-state-target");
            Directory.CreateDirectory(externalStateTarget);
            CreateJunction(reparseEnv.StatePath, externalStateTarget);
            try
            {
                var reparseState = WorkshopBridgeSynchronizer.Synchronize(reparseEnv.Options);
                Assert(!reparseState.Synchronized && reparseState.AutoEnabledIdCount == 0,
                    "a reparse-point state path must block auto-enable");
                AssertActiveIds(reparseEnv.ActiveListPath);
            }
            finally
            {
                if (Directory.Exists(reparseEnv.StatePath))
                    Directory.Delete(reparseEnv.StatePath, false);
            }

            var pendingEnv = new AutoTestEnvironment(Path.Combine(root, "pending"), 74);
            File.WriteAllText(pendingEnv.ActiveListPath, string.Empty);
            CreateWorkshopDllMod(pendingEnv.WorkshopRoot, "400", withMarker: true);
            pendingEnv.WriteMetadata(
                WorkshopRecord.Current("400", pendingEnv.AccountId, installed: true));
            WriteState(pendingEnv.StatePath, pendingEnv.AccountId,
                seenIds: new string[0], pendingIds: new[] { "400" });
            var pendingRecovery = WorkshopBridgeSynchronizer.Synchronize(pendingEnv.Options);
            Assert(pendingRecovery.AutoEnabledIdCount == 0,
                "recovery from a pending transaction must not append an ID a second time");
            AssertActiveIds(pendingEnv.ActiveListPath);
            Assert(StateContainsId(pendingEnv.StatePath, "400"),
                "pending recovery should monotonically remember the ID as seen");

            var boundedEnv = new AutoTestEnvironment(Path.Combine(root, "bounded-vdf"), 75);
            File.WriteAllText(boundedEnv.ActiveListPath, string.Empty);
            boundedEnv.WriteMetadata(
                WorkshopRecord.Current("500", null, installed: true),
                WorkshopRecord.Current("600", boundedEnv.AccountId, installed: true));
            var boundedBaseline = WorkshopBridgeSynchronizer.Synchronize(boundedEnv.Options);
            Assert(boundedBaseline.BaselineIdCount == 1 &&
                   !StateContainsId(boundedEnv.StatePath, "500") &&
                   StateContainsId(boundedEnv.StatePath, "600"),
                "subscribedby must be read only from the matching WorkshopItemDetails block");

            var modStateEnv = new AutoTestEnvironment(Path.Combine(root, "mod-state"), 76);
            File.WriteAllText(modStateEnv.ActiveListPath, string.Empty);
            modStateEnv.WriteMetadata();
            WorkshopBridgeSynchronizer.Synchronize(modStateEnv.Options);
            CreateWorkshopDllMod(modStateEnv.WorkshopRoot, "700", withMarker: true);
            modStateEnv.WriteMetadata(
                WorkshopRecord.Current("700", modStateEnv.AccountId, installed: true));
            var modEnabled = WorkshopBridgeSynchronizer.Synchronize(modStateEnv.Options);
            var modLink = Path.Combine(modStateEnv.PluginRoot,
                WorkshopBridgeSynchronizer.LinkDirectoryName, "700");
            Assert(modEnabled.LinkedCount == 1 && Directory.Exists(modLink),
                "the _mod fail-closed fixture should begin with a live Bridge link");

            SetFileLength(modStateEnv.ActiveListPath, 1024L * 1024 + 1);
            var oversizedMod = WorkshopBridgeSynchronizer.Synchronize(modStateEnv.Options);
            Assert(!oversizedMod.Synchronized && !Directory.Exists(modLink),
                "an oversized _mod file must remove existing links and fail closed");

            File.Delete(modStateEnv.ActiveListPath);
            var externalModTarget = Path.Combine(root, "external-mod-target");
            Directory.CreateDirectory(externalModTarget);
            File.WriteAllText(Path.Combine(externalModTarget, "keep.txt"), "do not touch");
            CreateJunction(modStateEnv.ActiveListPath, externalModTarget);
            try
            {
                var reparseMod = WorkshopBridgeSynchronizer.Synchronize(modStateEnv.Options);
                Assert(!reparseMod.Synchronized && !Directory.Exists(modLink),
                    "a reparse-point _mod path must block all DLL links");
                Assert(File.Exists(Path.Combine(externalModTarget, "keep.txt")),
                    "_mod reparse-point validation must not mutate its target");
            }
            finally
            {
                if (Directory.Exists(modStateEnv.ActiveListPath))
                    Directory.Delete(modStateEnv.ActiveListPath, false);
            }
        }

        private static void TestAutoEnableWriteFailureAndExistingId(string root)
        {
            var env = new AutoTestEnvironment(Path.Combine(root, "locked-mod"), 82);
            File.WriteAllText(env.ActiveListPath, string.Empty);
            env.WriteMetadata();
            WorkshopBridgeSynchronizer.Synchronize(env.Options);
            CreateWorkshopDllMod(env.WorkshopRoot, "100", withMarker: true);
            env.WriteMetadata(WorkshopRecord.Current("100", env.AccountId, installed: true));

            BridgeResult lockedResult;
            using (var locked = new FileStream(env.ActiveListPath, FileMode.Open,
                FileAccess.Read, FileShare.Read))
            {
                lockedResult = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            }
            Assert(lockedResult.ErrorCount > 0 && lockedResult.AutoEnabledIdCount == 0,
                "an atomic _mod write failure should be reported without claiming success");
            AssertActiveIds(env.ActiveListPath);
            Assert(!StateContainsId(env.StatePath, "100"),
                "an _mod write failure must roll back pending state instead of committing seen");

            var retried = WorkshopBridgeSynchronizer.Synchronize(env.Options);
            Assert(retried.AutoEnabledIdCount == 1,
                "an item whose _mod write failed should remain eligible for a later retry");
            AssertActiveIds(env.ActiveListPath, "100");

            var existing = new AutoTestEnvironment(Path.Combine(root, "existing-id"), 83);
            File.WriteAllText(existing.ActiveListPath, string.Empty);
            existing.WriteMetadata();
            WorkshopBridgeSynchronizer.Synchronize(existing.Options);
            CreateWorkshopDllMod(existing.WorkshopRoot, "200", withMarker: true);
            File.WriteAllText(existing.ActiveListPath, "200\r\n");
            existing.WriteMetadata(
                WorkshopRecord.Current("200", existing.AccountId, installed: true));
            var alreadyActive = WorkshopBridgeSynchronizer.Synchronize(existing.Options);
            Assert(alreadyActive.AutoEnabledIdCount == 0,
                "an ID already present in _mod should not be counted as an append");
            Assert(CountActiveId(existing.ActiveListPath, "200") == 1,
                "an ID already present in _mod must not be duplicated");
            Assert(StateContainsId(existing.StatePath, "200"),
                "an already-active new subscription should still become seen");
        }

        private sealed class WorkshopRecord
        {
            public string Id;
            public string SubscribedBy;
            public bool Installed;
            public string Manifest;
            public string LatestManifest;

            public static WorkshopRecord Current(string id, string subscribedBy, bool installed,
                string latestManifest = null)
            {
                ulong numericId = ulong.Parse(id);
                string manifest = (numericId + 1000000UL).ToString();
                return new WorkshopRecord
                {
                    Id = id,
                    SubscribedBy = subscribedBy,
                    Installed = installed,
                    Manifest = manifest,
                    LatestManifest = latestManifest ?? manifest,
                };
            }
        }

        private sealed class AutoTestEnvironment
        {
            private const ulong SteamId64Base = 76561197960265728UL;

            public AutoTestEnvironment(string root, uint accountId)
                : this(root, accountId, null, null, null)
            {
            }

            private AutoTestEnvironment(string root, uint accountId, string workshopRoot,
                string metadataPath, string pluginRoot)
            {
                Root = root;
                AccountId = accountId.ToString();
                SteamId64 = (SteamId64Base + accountId).ToString();
                GameRoot = Path.Combine(root, "steamapps", "common", "StudentAge");
                WorkshopRoot = workshopRoot ?? Path.Combine(root, "steamapps", "workshop",
                    "content", BridgeOptions.WorkshopAppId);
                MetadataPath = metadataPath ?? Path.Combine(root, "steamapps", "workshop",
                    "appworkshop_" + BridgeOptions.WorkshopAppId + ".acf");
                PluginRoot = pluginRoot ?? Path.Combine(GameRoot, "BepInEx", "plugins");
                ProfileDirectory = Path.Combine(root, "Saves", SteamId64);
                ActiveListPath = Path.Combine(ProfileDirectory, "_mod");
                StatePath = Path.Combine(ProfileDirectory, BridgeOptions.AutoEnableStateFileName);
                Directory.CreateDirectory(WorkshopRoot);
                Directory.CreateDirectory(PluginRoot);
                Directory.CreateDirectory(ProfileDirectory);

                Options = new BridgeOptions
                {
                    GameRootPath = GameRoot,
                    WorkshopRootPath = WorkshopRoot,
                    WorkshopMetadataPath = MetadataPath,
                    ActiveModListPath = ActiveListPath,
                    AutoEnableStatePath = StatePath,
                    ActiveSteamAccountId = AccountId,
                    ActiveSteamId64 = SteamId64,
                    PluginRootPath = PluginRoot,
                };
            }

            public string Root { get; private set; }
            public string AccountId { get; private set; }
            public string SteamId64 { get; private set; }
            public string GameRoot { get; private set; }
            public string WorkshopRoot { get; private set; }
            public string MetadataPath { get; private set; }
            public string PluginRoot { get; private set; }
            public string ProfileDirectory { get; private set; }
            public string ActiveListPath { get; private set; }
            public string StatePath { get; private set; }
            public BridgeOptions Options { get; private set; }

            public AutoTestEnvironment CreateUser(uint accountId)
            {
                return new AutoTestEnvironment(Root, accountId, WorkshopRoot, MetadataPath, PluginRoot);
            }

            public void WriteMetadata(params WorkshopRecord[] records)
            {
                records = records ?? new WorkshopRecord[0];
                var builder = new StringBuilder();
                builder.Append("\"AppWorkshop\"\r\n{\r\n")
                    .Append("\t\"appid\"\t\t\"").Append(BridgeOptions.WorkshopAppId)
                    .Append("\"\r\n\t\"WorkshopItemsInstalled\"\r\n\t{\r\n");
                foreach (var record in records.Where(record => record.Installed))
                {
                    builder.Append("\t\t\"").Append(record.Id).Append("\"\r\n\t\t{\r\n")
                        .Append("\t\t\t\"manifest\"\t\t\"").Append(record.Manifest)
                        .Append("\"\r\n\t\t}\r\n");
                }
                builder.Append("\t}\r\n\t\"WorkshopItemDetails\"\r\n\t{\r\n");
                foreach (var record in records)
                {
                    builder.Append("\t\t\"").Append(record.Id).Append("\"\r\n\t\t{\r\n")
                        .Append("\t\t\t\"manifest\"\t\t\"").Append(record.Manifest)
                        .Append("\"\r\n");
                    if (record.SubscribedBy != null)
                        builder.Append("\t\t\t\"subscribedby\"\t\t\"")
                            .Append(record.SubscribedBy).Append("\"\r\n");
                    builder.Append("\t\t\t\"latest_manifest\"\t\t\"")
                        .Append(record.LatestManifest).Append("\"\r\n\t\t}\r\n");
                }
                builder.Append("\t}\r\n}\r\n");
                Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath));
                File.WriteAllText(MetadataPath, builder.ToString(), new UTF8Encoding(false));
            }
        }

        private static void CreateWorkshopItemWithoutDll(string workshopRoot, string id)
        {
            var pluginDirectory = Path.Combine(workshopRoot, id, "BepInEx", "plugins");
            Directory.CreateDirectory(pluginDirectory);
            File.WriteAllText(Path.Combine(workshopRoot, id,
                BridgeOptions.DefaultMarkerFileName), ValidManifestJson());
        }

        private static string ValidManifestJson()
        {
            return "{\"schemaVersion\":1,\"type\":\"bepinex-plugin\",\"pluginRoot\":\"BepInEx/plugins\"}";
        }

        private static bool StateContainsId(string statePath, string id)
        {
            return File.Exists(statePath) &&
                File.ReadAllText(statePath).Contains("\"" + id + "\"");
        }

        private static void WriteState(string statePath, string accountId,
            IEnumerable<string> seenIds, IEnumerable<string> pendingIds)
        {
            string seen = string.Join(",", seenIds.Select(id => "\"" + id + "\""));
            string pending = string.Join(",", pendingIds.Select(id => "\"" + id + "\""));
            File.WriteAllText(statePath,
                "{\"schemaVersion\":1,\"steamAccountId\":\"" + accountId +
                "\",\"seenWorkshopIds\":[" + seen + "],\"pendingWorkshopIds\":[" +
                pending + "]}", new UTF8Encoding(false));
        }

        private static void SetFileLength(string path, long length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.None))
                stream.SetLength(length);
        }

        private static void AssertActiveIds(string path, params string[] expected)
        {
            var actual = File.Exists(path)
                ? File.ReadAllLines(path).Select(line => line.Trim())
                    .Where(line => line.Length > 0).OrderBy(line => line).ToArray()
                : new string[0];
            var sortedExpected = (expected ?? new string[0]).OrderBy(id => id).ToArray();
            Assert(actual.SequenceEqual(sortedExpected),
                "active IDs mismatch; expected [" + string.Join(",", sortedExpected) +
                "] but got [" + string.Join(",", actual) + "]");
        }

        private static int CountActiveId(string path, string id)
        {
            return File.ReadAllLines(path).Count(line => string.Equals(line.Trim(), id,
                StringComparison.Ordinal));
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
