using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StudentAge.WorkshopBridge
{
    public sealed class WorkshopManagedItem
    {
        public string WorkshopId { get; internal set; }
        public bool IsSubscribed { get; internal set; }
        public bool IsDownloaded { get; internal set; }
        public bool IsEnabled { get; internal set; }
        public bool IsConnected { get; internal set; }
        public bool HasBridgeManifest { get; internal set; }
        public bool IsValidBridgePackage { get; internal set; }
        public string ContentPath { get; internal set; }
        public string PluginRootPath { get; internal set; }
        public int DllCount { get; internal set; }
        public string ValidationError { get; internal set; }
    }

    public sealed class WorkshopDiscoveryResult
    {
        public bool Succeeded { get; internal set; }
        public string Error { get; internal set; }
        public List<WorkshopManagedItem> Items { get; internal set; } =
            new List<WorkshopManagedItem>();
    }

    public sealed class WorkshopToggleResult
    {
        public bool Succeeded { get; internal set; }
        public bool Changed { get; internal set; }
        public bool IsEnabled { get; internal set; }
        public string Error { get; internal set; }
        public BridgeResult Synchronization { get; internal set; }
    }

    /// <summary>
    /// Read-only discovery and explicit per-user enable/disable operations used by the manager.
    /// Steam subscription/content is never changed; only the native _mod list and Bridge links
    /// are reconciled.
    /// </summary>
    public static class WorkshopBridgeManagement
    {
        private const string WorkshopPluginRelativePath = @"BepInEx\plugins";

        public static WorkshopDiscoveryResult Discover(string gameRootPath)
        {
            using (WorkshopBridgeTransaction.Acquire(gameRootPath))
                return DiscoverUnderLease(BridgeOptions.ForGame(gameRootPath));
        }

        public static WorkshopDiscoveryResult Discover(BridgeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            using (WorkshopBridgeTransaction.Acquire(options.GameRootPath))
                return DiscoverUnderLease(options);
        }

        private static WorkshopDiscoveryResult DiscoverUnderLease(BridgeOptions options)
        {
            var output = new WorkshopDiscoveryResult();
            try
            {
                ValidatedContext context;
                string error;
                if (!TryResolveContext(options, out context, out error))
                {
                    output.Error = error;
                    return output;
                }

                output.Items = DiscoverItems(context);
                output.Succeeded = true;
                return output;
            }
            catch (Exception ex)
            {
                output.Error = ex.Message;
                output.Items.Clear();
                return output;
            }
        }

        public static WorkshopToggleResult SetEnabled(string gameRootPath,
            string canonicalWorkshopId, bool enabled)
        {
            using (WorkshopBridgeTransaction.Acquire(gameRootPath))
                return SetEnabledUnderLease(BridgeOptions.ForGame(gameRootPath),
                    canonicalWorkshopId, enabled);
        }

        public static WorkshopToggleResult SetEnabled(BridgeOptions options,
            string canonicalWorkshopId, bool enabled)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            using (WorkshopBridgeTransaction.Acquire(options.GameRootPath))
                return SetEnabledUnderLease(options, canonicalWorkshopId, enabled);
        }

        private static WorkshopToggleResult SetEnabledUnderLease(BridgeOptions options,
            string canonicalWorkshopId, bool enabled)
        {
            var output = new WorkshopToggleResult { IsEnabled = enabled };
            try
            {
                ulong workshopId;
                if (!TryParseCanonicalId(canonicalWorkshopId, out workshopId))
                    throw new ArgumentException("Workshop ID 必须是规范的非零纯数字。",
                        nameof(canonicalWorkshopId));

                ValidatedContext context;
                string contextError;
                if (!TryResolveContext(options, out context, out contextError))
                    throw new InvalidOperationException(contextError);

                WorkshopManagedItem item = DiscoverItems(context)
                    .FirstOrDefault(candidate => candidate.WorkshopId == canonicalWorkshopId);
                if (item == null || !item.IsSubscribed)
                    throw new InvalidOperationException("当前 Steam 用户未订阅该工坊项目。");
                if (enabled && !item.IsDownloaded)
                    throw new InvalidOperationException("工坊项目尚未下载完成或正在更新。");
                if (enabled && !item.IsValidBridgePackage)
                    throw new InvalidOperationException("工坊 DLL 包无效: " +
                        (item.ValidationError ?? "缺少合法声明或插件目录。"));

                // Persist the manual-choice marker first, but retain exact prior bytes so a
                // failed _mod commit cannot leave state claiming a choice that never happened.
                byte[] oldStateBytes = File.Exists(context.StatePath)
                    ? File.ReadAllBytes(context.StatePath) : null;
                HashSet<ulong> seenIds;
                HashSet<ulong> pendingIds;
                string stateError;
                if (oldStateBytes == null)
                {
                    seenIds = new HashSet<ulong>(context.Subscriptions.SubscribedIds);
                    pendingIds = new HashSet<ulong>();
                }
                else if (!WorkshopBridgeSynchronizer.TryLoadAutoEnableState(context.StatePath,
                    context.AccountId, out seenIds, out pendingIds, out stateError))
                {
                    throw new InvalidDataException("Bridge 自动启用状态无效: " + stateError);
                }

                seenIds.UnionWith(pendingIds);
                pendingIds.Clear();
                seenIds.Add(workshopId);
                if (!WorkshopBridgeSynchronizer.TrySaveAutoEnableState(context.StatePath,
                    context.AccountId, seenIds, pendingIds, out stateError))
                    throw new IOException("无法保存 Bridge 启用状态: " + stateError);

                bool changed;
                string modError;
                if (!WorkshopBridgeSynchronizer.TrySetActiveId(context.ActiveListPath,
                    workshopId, enabled, out changed, out modError))
                {
                    try
                    {
                        if (oldStateBytes == null)
                        {
                            if (File.Exists(context.StatePath)) File.Delete(context.StatePath);
                        }
                        else
                        {
                            WorkshopBridgeSynchronizer.WriteFileAtomically(context.StatePath,
                                oldStateBytes);
                        }
                    }
                    catch (Exception rollback)
                    {
                        throw new IOException("无法更新游戏 Mod 启用列表: " + modError +
                            "；同时无法恢复 Bridge 状态: " + rollback.Message);
                    }
                    throw new IOException("无法更新游戏 Mod 启用列表: " + modError);
                }

                BridgeResult synchronization =
                    WorkshopBridgeSynchronizer.SynchronizeUnderLease(options);
                output.Succeeded = true;
                output.Changed = changed;
                output.Synchronization = synchronization;
                return output;
            }
            catch (Exception ex)
            {
                output.Error = ex.Message;
                return output;
            }
        }

        private static List<WorkshopManagedItem> DiscoverItems(ValidatedContext context)
        {
            var enabledIds = new HashSet<ulong>();
            if (File.Exists(context.ActiveListPath))
                enabledIds = WorkshopBridgeSynchronizer.ReadEnabledIds(context.ActiveListPath,
                    new BridgeResult());

            var items = new List<WorkshopManagedItem>();
            foreach (ulong workshopId in context.Subscriptions.SubscribedIds.OrderBy(id => id))
            {
                string id = workshopId.ToString(CultureInfo.InvariantCulture);
                var item = new WorkshopManagedItem
                {
                    WorkshopId = id,
                    IsSubscribed = true,
                    IsDownloaded = context.Subscriptions.DownloadedIds.Contains(workshopId),
                    IsEnabled = enabledIds.Contains(workshopId),
                };
                InspectPackage(context, item);
                items.Add(item);
            }
            return items;
        }

        private static void InspectPackage(ValidatedContext context, WorkshopManagedItem item)
        {
            string itemRoot = Path.GetFullPath(Path.Combine(context.WorkshopRoot,
                item.WorkshopId));
            item.ContentPath = itemRoot;
            if (!string.Equals(Path.GetDirectoryName(itemRoot), context.WorkshopRoot,
                StringComparison.OrdinalIgnoreCase))
            {
                item.ValidationError = "工坊项目路径越过预期根目录。";
                return;
            }

            string linkPath = Path.Combine(context.PluginRoot,
                WorkshopBridgeSynchronizer.LinkDirectoryName, item.WorkshopId);
            ulong workshopId;
            string installedManifest;
            if (ulong.TryParse(item.WorkshopId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out workshopId) &&
                context.Subscriptions.DownloadedIds.Contains(workshopId) &&
                context.Subscriptions.InstalledManifests.TryGetValue(workshopId,
                    out installedManifest))
            {
                string expectedSnapshot = WorkshopBridgeSynchronizer.GetSnapshotPath(
                    context.Options, workshopId, installedManifest);
                bool pointsToExpected;
                string targetError;
                item.IsConnected = JunctionManager.TryPointsToSameDirectory(linkPath,
                    expectedSnapshot, out pointsToExpected, out targetError) &&
                    pointsToExpected;
            }

            if (!Directory.Exists(itemRoot))
            {
                item.ValidationError = "工坊内容尚未下载到本机。";
                return;
            }
            if (WorkshopBridgeSynchronizer.IsExistingReparsePoint(itemRoot))
            {
                item.ValidationError = "工坊项目根目录不能是重解析点。";
                return;
            }

            string markerPath = Path.Combine(itemRoot,
                context.MarkerFileName);
            item.HasBridgeManifest = File.Exists(markerPath) || Directory.Exists(markerPath);
            if (!item.HasBridgeManifest)
            {
                item.ValidationError = "不是 DLL 工坊包（缺少 workshop-plugin.json）。";
                return;
            }
            if (Directory.Exists(markerPath))
            {
                item.ValidationError = "workshop-plugin.json 路径被目录占用。";
                return;
            }

            string manifestError;
            if (!WorkshopBridgeSynchronizer.TryValidateManifest(markerPath, out manifestError))
            {
                item.ValidationError = "声明文件无效: " + manifestError;
                return;
            }

            string pluginRoot = Path.Combine(itemRoot, WorkshopPluginRelativePath);
            item.PluginRootPath = pluginRoot;
            if (!Directory.Exists(pluginRoot))
            {
                item.ValidationError = "缺少 BepInEx/plugins 目录。";
                return;
            }
            if (WorkshopBridgeSynchronizer.IsExistingReparsePoint(pluginRoot))
            {
                item.ValidationError = "工坊插件根目录不能是重解析点。";
                return;
            }

            try
            {
                item.DllCount = Directory.GetFiles(pluginRoot, "*.dll",
                    SearchOption.AllDirectories).Length;
            }
            catch (Exception ex)
            {
                item.ValidationError = "无法枚举 DLL: " + ex.Message;
                return;
            }
            if (item.DllCount == 0)
            {
                item.ValidationError = "BepInEx/plugins 中没有 DLL。";
                return;
            }
            item.IsValidBridgePackage = true;
            item.ValidationError = null;
        }

        private static bool TryResolveContext(BridgeOptions options,
            out ValidatedContext context, out string error)
        {
            context = null;
            error = null;
            try
            {
                if (options == null) throw new ArgumentNullException(nameof(options));
                if (string.IsNullOrWhiteSpace(options.GameRootPath) ||
                    string.IsNullOrWhiteSpace(options.WorkshopRootPath) ||
                    string.IsNullOrWhiteSpace(options.WorkshopMetadataPath) ||
                    string.IsNullOrWhiteSpace(options.ActiveModListPath) ||
                    string.IsNullOrWhiteSpace(options.AutoEnableStatePath) ||
                    string.IsNullOrWhiteSpace(options.ActiveSteamAccountId) ||
                    string.IsNullOrWhiteSpace(options.ActiveSteamId64) ||
                    string.IsNullOrWhiteSpace(options.PluginRootPath))
                    throw new InvalidOperationException(
                        "无法明确确定当前 Steam 用户、工坊目录或游戏插件目录。");

                string gameRoot = Path.GetFullPath(options.GameRootPath);
                string workshopRoot = Path.GetFullPath(options.WorkshopRootPath);
                string metadataPath = Path.GetFullPath(options.WorkshopMetadataPath);
                string activeListPath = Path.GetFullPath(options.ActiveModListPath);
                string statePath = Path.GetFullPath(options.AutoEnableStatePath);
                string pluginRoot = Path.GetFullPath(options.PluginRootPath);
                string expectedPluginRoot = Path.GetFullPath(Path.Combine(gameRoot,
                    "BepInEx", "plugins"));
                string markerFileName = string.IsNullOrWhiteSpace(options.MarkerFileName)
                    ? BridgeOptions.DefaultMarkerFileName
                    : options.MarkerFileName;
                string profileDirectory = Path.GetDirectoryName(activeListPath);
                string expectedMetadataPath = SteamPathLocator.FindWorkshopMetadata(workshopRoot);
                if (!Directory.Exists(gameRoot) || !Directory.Exists(workshopRoot) ||
                    string.IsNullOrEmpty(profileDirectory) ||
                    !Directory.Exists(profileDirectory) ||
                    !string.Equals(profileDirectory, Path.GetDirectoryName(statePath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(activeListPath), "_mod", StringComparison.Ordinal) ||
                    !string.Equals(Path.GetFileName(statePath), BridgeOptions.AutoEnableStateFileName,
                        StringComparison.Ordinal) || string.IsNullOrEmpty(expectedMetadataPath) ||
                    !string.Equals(metadataPath, expectedMetadataPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(pluginRoot, expectedPluginRoot,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("用户状态或 Steam 工坊路径不符合安全约束。");

                if (!string.Equals(markerFileName, Path.GetFileName(markerFileName),
                        StringComparison.Ordinal) ||
                    markerFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    string.Equals(markerFileName, ".bridge-snapshot",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("DLL 工坊声明文件名不符合安全约束。");

                FileAttributes profileAttributes = File.GetAttributes(profileDirectory);
                if ((profileAttributes & FileAttributes.ReparsePoint) != 0 ||
                    WorkshopBridgeSynchronizer.IsExistingReparsePoint(activeListPath) ||
                    WorkshopBridgeSynchronizer.IsExistingReparsePoint(statePath) ||
                    Directory.Exists(activeListPath) || Directory.Exists(statePath))
                    throw new InvalidDataException("用户存档目录、_mod 和状态文件不能是重解析点或目录。");

                string linkRoot = Path.Combine(pluginRoot,
                    WorkshopBridgeSynchronizer.LinkDirectoryName);
                if (File.Exists(pluginRoot) ||
                    WorkshopBridgeSynchronizer.IsExistingReparsePoint(pluginRoot) ||
                    File.Exists(linkRoot) ||
                    WorkshopBridgeSynchronizer.IsExistingReparsePoint(linkRoot))
                    throw new InvalidDataException(
                        "BepInEx 插件目录和工坊联接根必须是当前游戏中的普通目录。");

                uint accountId;
                if (!uint.TryParse(options.ActiveSteamAccountId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out accountId) || accountId == 0 ||
                    !string.Equals(options.ActiveSteamAccountId,
                        accountId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !SteamPathLocator.IsMatchingSteamIdentity(accountId, options.ActiveSteamId64) ||
                    !string.Equals(Path.GetFileName(profileDirectory), options.ActiveSteamId64,
                        StringComparison.Ordinal))
                    throw new InvalidDataException("当前 Steam 用户身份与存档目录不匹配。");

                WorkshopSubscriptionSnapshot subscriptions;
                string subscriptionError;
                if (!SteamWorkshopMetadata.TryRead(metadataPath, accountId,
                    out subscriptions, out subscriptionError))
                    throw new InvalidDataException("无法读取当前 Steam 用户订阅列表: " +
                        subscriptionError);

                context = new ValidatedContext
                {
                    AccountId = accountId,
                    WorkshopRoot = workshopRoot,
                    PluginRoot = pluginRoot,
                    MarkerFileName = markerFileName,
                    ActiveListPath = activeListPath,
                    StatePath = statePath,
                    Subscriptions = subscriptions,
                    Options = options,
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryParseCanonicalId(string value, out ulong workshopId)
        {
            workshopId = 0;
            return !string.IsNullOrEmpty(value) &&
                ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out workshopId) && workshopId != 0 &&
                string.Equals(value, workshopId.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
        }

        private sealed class ValidatedContext
        {
            public uint AccountId { get; set; }
            public string WorkshopRoot { get; set; }
            public string PluginRoot { get; set; }
            public string MarkerFileName { get; set; }
            public string ActiveListPath { get; set; }
            public string StatePath { get; set; }
            public BridgeOptions Options { get; set; }
            public WorkshopSubscriptionSnapshot Subscriptions { get; set; }
        }
    }
}
