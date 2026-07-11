using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StudentAge.WorkshopBridge
{
    public sealed class BridgeOptions
    {
        public const string DefaultMarkerFileName = "workshop-plugin.json";
        public const string WorkshopAppId = "1991040";
        public const string AutoEnableStateFileName = "_workshop_bridge_state.json";

        public string GameRootPath { get; set; }
        public string WorkshopRootPath { get; set; }
        public string WorkshopMetadataPath { get; set; }
        public string ActiveModListPath { get; set; }
        public string AutoEnableStatePath { get; set; }
        public string ActiveSteamAccountId { get; set; }
        public string ActiveSteamId64 { get; set; }
        public string PluginRootPath { get; set; }
        public string MarkerFileName { get; set; } = DefaultMarkerFileName;

        public static BridgeOptions ForGame(string gameRootPath)
        {
            if (string.IsNullOrWhiteSpace(gameRootPath))
                throw new ArgumentException("游戏目录为空。", nameof(gameRootPath));

            gameRootPath = Path.GetFullPath(gameRootPath);
            var workshopRoot = SteamPathLocator.FindWorkshopRoot(gameRootPath);
            var activeUser = SteamPathLocator.FindActiveUser(gameRootPath);
            return new BridgeOptions
            {
                GameRootPath = gameRootPath,
                WorkshopRootPath = workshopRoot,
                WorkshopMetadataPath = SteamPathLocator.FindWorkshopMetadata(workshopRoot),
                ActiveModListPath = activeUser == null ? null : activeUser.ActiveModListPath,
                AutoEnableStatePath = activeUser == null ? null : activeUser.AutoEnableStatePath,
                ActiveSteamAccountId = activeUser == null ? null : activeUser.AccountId,
                ActiveSteamId64 = activeUser == null ? null : activeUser.SteamId64,
                PluginRootPath = Path.Combine(gameRootPath, "BepInEx", "plugins"),
            };
        }
    }

    public sealed class BridgeResult
    {
        private readonly List<string> _messages = new List<string>();

        public bool Synchronized { get; internal set; }
        public int EnabledIdCount { get; internal set; }
        public int BaselineIdCount { get; internal set; }
        public int AutoEnabledIdCount { get; internal set; }
        public int LinkedCount { get; internal set; }
        public int RemovedLinkCount { get; internal set; }
        public int SkippedCount { get; internal set; }
        public int ErrorCount { get; internal set; }
        public IReadOnlyList<string> Messages => _messages;

        internal void Info(string message) => _messages.Add("[Info] " + message);
        internal void Warning(string message) => _messages.Add("[Warning] " + message);
        internal void Error(string message)
        {
            ErrorCount++;
            _messages.Add("[Error] " + message);
        }
    }

    [DataContract]
    internal sealed class WorkshopPluginManifest
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "pluginRoot")]
        public string PluginRoot { get; set; }
    }

    [DataContract]
    internal sealed class WorkshopAutoEnableState
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "steamAccountId")]
        public string SteamAccountId { get; set; }

        [DataMember(Name = "seenWorkshopIds")]
        public List<string> SeenWorkshopIds { get; set; }

        [DataMember(Name = "pendingWorkshopIds")]
        public List<string> PendingWorkshopIds { get; set; }
    }

    internal sealed class WorkshopSubscriptionSnapshot
    {
        public WorkshopSubscriptionSnapshot()
        {
            SubscribedIds = new HashSet<ulong>();
            DownloadedIds = new HashSet<ulong>();
        }

        public HashSet<ulong> SubscribedIds { get; private set; }
        public HashSet<ulong> DownloadedIds { get; private set; }
    }

    public static class WorkshopBridgeSynchronizer
    {
        public const string LinkDirectoryName = ".workshop";
        private const string WorkshopPluginRelativePath = @"BepInEx\plugins";
        private const string ManifestType = "bepinex-plugin";
        private const string ManifestPluginRoot = "BepInEx/plugins";
        private const int MaxManifestBytes = 64 * 1024;
        private const int MaxAutoEnableStateBytes = 1024 * 1024;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static BridgeResult Synchronize(BridgeOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var result = new BridgeResult();

            if (string.IsNullOrWhiteSpace(options.PluginRootPath))
            {
                result.Error("BepInEx 插件目录为空；无法安全停用或同步工坊链接。");
                return result;
            }

            string markerFileName;
            try
            {
                markerFileName = string.IsNullOrWhiteSpace(options.MarkerFileName)
                    ? BridgeOptions.DefaultMarkerFileName
                    : options.MarkerFileName;
                if (!string.Equals(markerFileName, Path.GetFileName(markerFileName),
                    StringComparison.Ordinal) || markerFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    result.Error("DLL 工坊声明文件名无效；不执行同步。");
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Error("DLL 工坊声明文件名无效；不执行同步: " + ex.Message);
                return result;
            }

            var linkRoot = Path.Combine(options.PluginRootPath, LinkDirectoryName);
            try
            {
                Directory.CreateDirectory(options.PluginRootPath);
                var existingLinkRoot = Directory.GetFileSystemEntries(options.PluginRootPath)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), LinkDirectoryName,
                        StringComparison.OrdinalIgnoreCase));
                if (existingLinkRoot != null)
                {
                    var attributes = File.GetAttributes(existingLinkRoot);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        result.Error("工坊桥接根目录不能是重解析点；不执行同步: " + linkRoot);
                        return result;
                    }
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        result.Error("工坊桥接根路径被普通文件占用；不执行同步: " + linkRoot);
                        return result;
                    }
                }
                else
                {
                    Directory.CreateDirectory(linkRoot);
                }
            }
            catch (Exception ex)
            {
                result.Error("无法创建工坊桥接目录: " + ex.Message);
                return result;
            }

            if (string.IsNullOrWhiteSpace(options.WorkshopRootPath) ||
                !Directory.Exists(options.WorkshopRootPath))
            {
                RemoveExistingLinks(linkRoot, result);
                result.Warning("未找到 Steam 创意工坊目录；已清理可安全删除的旧联接，本次不加载 DLL 工坊项目。");
                return result;
            }

            HashSet<ulong> currentSubscribedIds;
            HashSet<ulong> currentDownloadedIds;
            // Auto-enable only newly observed, currently subscribed Bridge DLL items.
            // The first run records a baseline and intentionally leaves existing items off.
            TryAutoEnableNewItems(options, markerFileName, result, out currentSubscribedIds,
                out currentDownloadedIds);
            if (currentSubscribedIds == null || currentDownloadedIds == null)
            {
                RemoveExistingLinks(linkRoot, result);
                result.Warning("无法确认当前 Steam 用户的订阅列表；已清理可安全删除的旧联接，" +
                    "本次不加载 DLL 工坊项目。");
                return result;
            }

            // Fail closed. Existing junctions can belong to a previously active Steam
            // user, so an unavailable/ambiguous _mod file must not leave DLLs enabled.
            if (string.IsNullOrWhiteSpace(options.ActiveModListPath) ||
                !File.Exists(options.ActiveModListPath))
            {
                RemoveExistingLinks(linkRoot, result);
                result.Warning("未找到游戏原生 Mod 启用列表；已清理可安全删除的旧联接，本次不加载 DLL 工坊项目。");
                return result;
            }

            HashSet<ulong> enabledIds;
            try
            {
                enabledIds = ReadEnabledIds(options.ActiveModListPath, result);
            }
            catch (Exception ex)
            {
                RemoveExistingLinks(linkRoot, result);
                result.Error("读取 Mod 启用列表失败；已清理可安全删除的旧联接: " + ex.Message);
                return result;
            }

            result.EnabledIdCount = enabledIds.Count;
            // Links are cheap. Recreate bridge-owned reparse points on every start so an
            // updated/moved Steam library can never leave an old target behind.
            RemoveExistingLinks(linkRoot, result);

            foreach (var workshopId in enabledIds.OrderBy(id => id))
            {
                var id = workshopId.ToString(CultureInfo.InvariantCulture);
                var itemRoot = Path.Combine(options.WorkshopRootPath, id);
                var markerPath = Path.Combine(itemRoot, markerFileName);
                var sourcePluginRoot = Path.Combine(itemRoot, WorkshopPluginRelativePath);
                var destination = Path.Combine(linkRoot, id);

                if (!currentSubscribedIds.Contains(workshopId))
                {
                    result.SkippedCount++;
                    result.Info("跳过当前 Steam 用户未订阅的已启用工坊项目: " + id);
                    continue;
                }

                if (!currentDownloadedIds.Contains(workshopId))
                {
                    result.SkippedCount++;
                    result.Info("跳过尚未下载完成或正在更新的已启用工坊项目: " + id);
                    continue;
                }

                if (!Directory.Exists(itemRoot))
                {
                    result.SkippedCount++;
                    result.Warning("已启用的工坊项目尚未安装或已取消订阅: " + id);
                    continue;
                }

                if (IsExistingReparsePoint(itemRoot))
                {
                    result.SkippedCount++;
                    result.Error("工坊项目根目录不能是重解析点，已拒绝桥接: " + id);
                    continue;
                }

                if (Directory.Exists(markerPath))
                {
                    result.SkippedCount++;
                    result.Error("DLL 工坊项目声明路径被目录或目录重解析点占用: " + id);
                    continue;
                }

                if (!File.Exists(markerPath))
                {
                    // Normal JSON workshop mods intentionally land here.
                    result.SkippedCount++;
                    result.Info("跳过非 DLL 工坊项目（无声明文件）: " + id);
                    continue;
                }

                string manifestError;
                if (!TryValidateManifest(markerPath, out manifestError))
                {
                    result.SkippedCount++;
                    result.Error("DLL 工坊项目声明文件无效 " + id + ": " + manifestError);
                    continue;
                }

                if (!Directory.Exists(sourcePluginRoot))
                {
                    result.SkippedCount++;
                    result.Warning("DLL 工坊项目缺少 BepInEx/plugins 目录: " + id);
                    continue;
                }

                if (IsExistingReparsePoint(sourcePluginRoot))
                {
                    result.SkippedCount++;
                    result.Error("DLL 工坊项目的插件根目录不能是重解析点: " + id);
                    continue;
                }

                bool hasDll;
                try
                {
                    hasDll = Directory.GetFiles(sourcePluginRoot, "*.dll", SearchOption.AllDirectories).Length > 0;
                }
                catch (Exception ex)
                {
                    result.SkippedCount++;
                    result.Error("无法检查工坊项目 " + id + " 的 DLL: " + ex.Message);
                    continue;
                }

                if (!hasDll)
                {
                    result.SkippedCount++;
                    result.Warning("DLL 工坊项目的插件目录中没有 DLL: " + id);
                    continue;
                }

                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    result.SkippedCount++;
                    result.Error("桥接目标已被普通文件或目录占用，未覆盖: " + destination);
                    continue;
                }

                string error;
                if (JunctionManager.TryCreate(destination, sourcePluginRoot, out error))
                {
                    result.LinkedCount++;
                    result.Info("已桥接工坊 DLL: " + id + " -> " + sourcePluginRoot);
                }
                else
                {
                    result.Error("创建工坊链接失败 " + id + ": " + error);
                }
            }

            result.Synchronized = true;
            return result;
        }

        private static void TryAutoEnableNewItems(BridgeOptions options, string markerFileName,
            BridgeResult result, out HashSet<ulong> currentSubscribedIds,
            out HashSet<ulong> currentDownloadedIds)
        {
            currentSubscribedIds = null;
            currentDownloadedIds = null;
            if (string.IsNullOrWhiteSpace(options.ActiveModListPath) ||
                string.IsNullOrWhiteSpace(options.AutoEnableStatePath) ||
                string.IsNullOrWhiteSpace(options.WorkshopMetadataPath) ||
                string.IsNullOrWhiteSpace(options.ActiveSteamAccountId) ||
                string.IsNullOrWhiteSpace(options.ActiveSteamId64))
            {
                result.Info("自动启用未运行：无法明确确定当前 Steam 用户或其订阅状态。");
                return;
            }

            try
            {
                string activeListPath = Path.GetFullPath(options.ActiveModListPath);
                string statePath = Path.GetFullPath(options.AutoEnableStatePath);
                string workshopRoot = Path.GetFullPath(options.WorkshopRootPath);
                string metadataPath = Path.GetFullPath(options.WorkshopMetadataPath);
                string profileDirectory = Path.GetDirectoryName(activeListPath);
                string expectedMetadataPath = SteamPathLocator.FindWorkshopMetadata(workshopRoot);

                if (string.IsNullOrEmpty(profileDirectory) || !Directory.Exists(profileDirectory) ||
                    !string.Equals(profileDirectory, Path.GetDirectoryName(statePath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(activeListPath), "_mod", StringComparison.Ordinal) ||
                    !string.Equals(Path.GetFileName(statePath), BridgeOptions.AutoEnableStateFileName,
                        StringComparison.Ordinal) ||
                    string.IsNullOrEmpty(expectedMetadataPath) ||
                    !string.Equals(metadataPath, expectedMetadataPath, StringComparison.OrdinalIgnoreCase))
                {
                    result.Warning("自动启用未运行：用户状态或 Steam 工坊元数据路径不符合安全约束。");
                    return;
                }

                var profileAttributes = File.GetAttributes(profileDirectory);
                if ((profileAttributes & FileAttributes.ReparsePoint) != 0 ||
                    IsExistingReparsePoint(activeListPath) || IsExistingReparsePoint(statePath))
                {
                    result.Warning("自动启用未运行：用户存档目录和状态文件不能是重解析点。");
                    return;
                }
                if (Directory.Exists(activeListPath) || Directory.Exists(statePath))
                {
                    result.Warning("自动启用未运行：_mod 或 Bridge 状态路径被目录占用。");
                    return;
                }

                uint accountId;
                if (!uint.TryParse(options.ActiveSteamAccountId, NumberStyles.None,
                    CultureInfo.InvariantCulture, out accountId) || accountId == 0 ||
                    !string.Equals(options.ActiveSteamAccountId,
                        accountId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !SteamPathLocator.IsMatchingSteamIdentity(accountId, options.ActiveSteamId64) ||
                    !string.Equals(Path.GetFileName(profileDirectory), options.ActiveSteamId64,
                        StringComparison.Ordinal))
                {
                    result.Warning("自动启用未运行：当前 Steam 用户身份与存档目录不匹配。");
                    return;
                }

                WorkshopSubscriptionSnapshot subscriptions;
                string subscriptionError;
                if (!SteamWorkshopMetadata.TryRead(options.WorkshopMetadataPath, accountId,
                    out subscriptions, out subscriptionError))
                {
                    result.Warning("自动启用未运行：无法读取当前 Steam 用户的订阅列表: " +
                        subscriptionError);
                    return;
                }

                currentSubscribedIds = new HashSet<ulong>(subscriptions.SubscribedIds);
                currentDownloadedIds = new HashSet<ulong>(subscriptions.DownloadedIds);
                if (!File.Exists(statePath))
                {
                    string stateError;
                    if (!TrySaveAutoEnableState(statePath, accountId,
                        subscriptions.SubscribedIds, new HashSet<ulong>(), out stateError))
                    {
                        result.Error("保存当前 Steam 用户的 DLL 工坊自动启用基线失败: " + stateError);
                        currentSubscribedIds = null;
                        currentDownloadedIds = null;
                        return;
                    }

                    result.BaselineIdCount = subscriptions.SubscribedIds.Count;
                    result.Info("已为当前 Steam 用户建立工坊订阅基线；现有订阅 " +
                        result.BaselineIdCount + " 个仅登记，不会自动启用。");
                    return;
                }

                HashSet<ulong> seenIds;
                HashSet<ulong> pendingIds;
                string loadStateError;
                if (!TryLoadAutoEnableState(statePath, accountId, out seenIds,
                    out pendingIds, out loadStateError))
                {
                    result.Error("DLL 工坊自动启用状态无效；为避免重新开启旧项目，本次不加载或自动启用: " +
                        loadStateError);
                    currentSubscribedIds = null;
                    currentDownloadedIds = null;
                    return;
                }

                if (pendingIds.Count > 0)
                {
                    // A pending set means _mod may already have been committed before the
                    // previous state-finalization failed or the process stopped. Prefer the
                    // player's current _mod choice and never append these IDs a second time.
                    seenIds.UnionWith(pendingIds);
                    int recoveredCount = pendingIds.Count;
                    pendingIds.Clear();
                    string recoveryError;
                    if (!TrySaveAutoEnableState(statePath, accountId, seenIds,
                        pendingIds, out recoveryError))
                    {
                        result.Error("无法收敛上次未完成的自动启用事务；本次不再修改 _mod: " +
                            recoveryError);
                        currentSubscribedIds = null;
                        currentDownloadedIds = null;
                        return;
                    }
                    result.Warning("已安全收敛上次未完成的自动启用事务 " + recoveredCount +
                        " 项；保留玩家当前开关状态，不会重复开启。");
                }

                var readyNewIds = new HashSet<ulong>(subscriptions.DownloadedIds
                    .Where(id => !seenIds.Contains(id)));
                if (readyNewIds.Count == 0) return;

                HashSet<ulong> handledWithoutEnable;
                HashSet<ulong> autoEnableIds;
                InspectNewDownloadedItems(workshopRoot, markerFileName, readyNewIds,
                    result, out handledWithoutEnable, out autoEnableIds);

                seenIds.UnionWith(handledWithoutEnable);
                if (autoEnableIds.Count == 0)
                {
                    if (handledWithoutEnable.Count == 0) return;
                    string saveHandledError;
                    if (!TrySaveAutoEnableState(statePath, accountId, seenIds,
                        pendingIds, out saveHandledError))
                    {
                        result.Error("记录已检查的非 Bridge 工坊项目失败: " + saveHandledError);
                    }
                    return;
                }

                // Persist a pending transaction before touching _mod. If final state saving
                // later fails, the next launch resolves pending IDs without re-adding them,
                // so a manual disable can never be overridden by recovery.
                pendingIds.UnionWith(autoEnableIds);
                string prepareError;
                if (!TrySaveAutoEnableState(statePath, accountId, seenIds,
                    pendingIds, out prepareError))
                {
                    result.Error("准备 DLL 工坊自动启用事务失败；未修改 _mod: " + prepareError);
                    return;
                }

                int addedCount;
                string appendError;
                if (!TryAppendActiveIds(activeListPath, autoEnableIds,
                    out addedCount, out appendError))
                {
                    pendingIds.ExceptWith(autoEnableIds);
                    string rollbackError;
                    if (!TrySaveAutoEnableState(statePath, accountId, seenIds,
                        pendingIds, out rollbackError))
                    {
                        result.Error("自动启用新订阅 DLL 项目失败: " + appendError +
                            "；同时无法回滚待处理状态: " + rollbackError);
                    }
                    else
                    {
                        result.Error("自动启用新订阅 DLL 项目失败；未将项目记为已处理: " + appendError);
                    }
                    return;
                }

                seenIds.UnionWith(autoEnableIds);
                pendingIds.ExceptWith(autoEnableIds);
                result.AutoEnabledIdCount = addedCount;
                string finalizeError;
                if (!TrySaveAutoEnableState(statePath, accountId, seenIds,
                    pendingIds, out finalizeError))
                {
                    // _mod was atomically committed. The on-disk pending transaction remains
                    // and will be conservatively finalized without another append next time.
                    result.Error("新订阅 DLL 已写入游戏启用列表，但保存最终 Bridge 状态失败: " +
                        finalizeError);
                    return;
                }

                result.Info("已处理首次识别的合法 DLL 工坊项目: " +
                    string.Join(", ", autoEnableIds.OrderBy(id => id)
                        .Select(id => id.ToString(CultureInfo.InvariantCulture))) +
                    "；本次新增到 _mod " + addedCount + " 个。");
            }
            catch (Exception ex)
            {
                // An unexpected failure can leave the per-user state ambiguous. Do not
                // authorize even explicitly enabled links until a later launch can validate it.
                currentSubscribedIds = null;
                currentDownloadedIds = null;
                result.Error("DLL 工坊自动启用发生异常；本次不加载或自动启用工坊 DLL: " + ex.Message);
            }
        }

        private static void InspectNewDownloadedItems(string workshopRoot, string markerFileName,
            IEnumerable<ulong> workshopIds, BridgeResult result,
            out HashSet<ulong> handledWithoutEnable, out HashSet<ulong> autoEnableIds)
        {
            handledWithoutEnable = new HashSet<ulong>();
            autoEnableIds = new HashSet<ulong>();
            workshopRoot = Path.GetFullPath(workshopRoot);

            foreach (var workshopId in workshopIds.OrderBy(id => id))
            {
                var id = workshopId.ToString(CultureInfo.InvariantCulture);
                var itemRoot = Path.GetFullPath(Path.Combine(workshopRoot, id));
                if (!string.Equals(Path.GetDirectoryName(itemRoot), workshopRoot,
                    StringComparison.OrdinalIgnoreCase))
                {
                    handledWithoutEnable.Add(workshopId);
                    result.Error("工坊项目路径越过预期根目录，已拒绝自动启用: " + id);
                    continue;
                }

                if (!Directory.Exists(itemRoot))
                {
                    // Metadata can be committed slightly before files become visible. Leave
                    // this ID unseen so a later launch can retry after Steam finishes.
                    result.Info("新订阅工坊项目文件尚不可用，稍后启动时重试: " + id);
                    continue;
                }

                if (IsExistingReparsePoint(itemRoot))
                {
                    handledWithoutEnable.Add(workshopId);
                    result.Error("工坊项目根目录不能是重解析点，已拒绝自动启用: " + id);
                    continue;
                }

                var markerPath = Path.Combine(itemRoot, markerFileName);
                if (Directory.Exists(markerPath))
                {
                    handledWithoutEnable.Add(workshopId);
                    result.Error("新订阅 DLL 工坊项目声明路径被目录或目录重解析点占用: " + id);
                    continue;
                }

                if (!File.Exists(markerPath))
                {
                    handledWithoutEnable.Add(workshopId);
                    result.Info("已登记新订阅的普通工坊项目（无 DLL Bridge 声明）: " + id);
                    continue;
                }

                string manifestError;
                if (!TryValidateManifest(markerPath, out manifestError))
                {
                    handledWithoutEnable.Add(workshopId);
                    result.Error("新订阅 DLL 工坊项目声明文件无效 " + id + ": " + manifestError);
                    continue;
                }

                var sourcePluginRoot = Path.Combine(itemRoot, WorkshopPluginRelativePath);
                if (!Directory.Exists(sourcePluginRoot))
                {
                    handledWithoutEnable.Add(workshopId);
                    result.Warning("新订阅 DLL 工坊项目缺少 BepInEx/plugins 目录: " + id);
                    continue;
                }
                if (IsExistingReparsePoint(sourcePluginRoot))
                {
                    handledWithoutEnable.Add(workshopId);
                    result.Error("新订阅 DLL 工坊项目的插件根目录不能是重解析点: " + id);
                    continue;
                }

                try
                {
                    if (Directory.GetFiles(sourcePluginRoot, "*.dll", SearchOption.AllDirectories).Length == 0)
                    {
                        handledWithoutEnable.Add(workshopId);
                        result.Warning("新订阅 DLL 工坊项目的插件目录中没有 DLL: " + id);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    // An I/O error is not proof that the completed package is invalid. Keep
                    // it unseen and retry instead of permanently missing a valid DLL item.
                    result.Error("无法检查新订阅工坊项目 " + id + " 的 DLL；稍后重试: " + ex.Message);
                    continue;
                }

                autoEnableIds.Add(workshopId);
            }
        }

        private static bool TryLoadAutoEnableState(string path, uint accountId,
            out HashSet<ulong> seenIds, out HashSet<ulong> pendingIds, out string error)
        {
            seenIds = new HashSet<ulong>();
            pendingIds = new HashSet<ulong>();
            error = null;
            try
            {
                path = Path.GetFullPath(path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("状态文件不能是重解析点。");

                var fileInfo = new FileInfo(path);
                if (fileInfo.Length <= 0 || fileInfo.Length > MaxAutoEnableStateBytes)
                    throw new InvalidDataException("状态文件为空或超过 1 MiB 限制。");

                WorkshopAutoEnableState state;
                var serializer = new DataContractJsonSerializer(typeof(WorkshopAutoEnableState));
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    state = serializer.ReadObject(stream) as WorkshopAutoEnableState;

                string expectedAccountId = accountId.ToString(CultureInfo.InvariantCulture);
                if (state == null || state.SchemaVersion != 1 ||
                    !string.Equals(state.SteamAccountId, expectedAccountId, StringComparison.Ordinal) ||
                    state.SeenWorkshopIds == null || state.PendingWorkshopIds == null)
                    throw new InvalidDataException("状态文件 schema 或 Steam 用户身份无效。");

                ParseCanonicalIdList(state.SeenWorkshopIds, "seenWorkshopIds", seenIds);
                ParseCanonicalIdList(state.PendingWorkshopIds, "pendingWorkshopIds", pendingIds);
                if (seenIds.Overlaps(pendingIds))
                    throw new InvalidDataException("状态文件的 seen 与 pending ID 重叠。");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                seenIds.Clear();
                pendingIds.Clear();
                return false;
            }
        }

        private static void ParseCanonicalIdList(IEnumerable<string> values, string fieldName,
            HashSet<ulong> destination)
        {
            foreach (var rawId in values)
            {
                ulong id;
                if (string.IsNullOrEmpty(rawId) ||
                    !ulong.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out id) ||
                    id == 0 || !string.Equals(rawId,
                        id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    throw new InvalidDataException("状态文件的 " + fieldName + " 含非规范 Workshop ID。");
                if (!destination.Add(id))
                    throw new InvalidDataException("状态文件的 " + fieldName + " 含重复 Workshop ID。");
            }
        }

        private static bool TrySaveAutoEnableState(string path, uint accountId,
            HashSet<ulong> seenIds, HashSet<ulong> pendingIds, out string error)
        {
            error = null;
            try
            {
                if (seenIds.Overlaps(pendingIds))
                    throw new InvalidDataException("不能保存互相重叠的 seen 与 pending ID。");

                var state = new WorkshopAutoEnableState
                {
                    SchemaVersion = 1,
                    SteamAccountId = accountId.ToString(CultureInfo.InvariantCulture),
                    SeenWorkshopIds = seenIds.OrderBy(id => id)
                        .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(),
                    PendingWorkshopIds = pendingIds.OrderBy(id => id)
                        .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList(),
                };
                var serializer = new DataContractJsonSerializer(typeof(WorkshopAutoEnableState));
                byte[] bytes;
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, state);
                    bytes = stream.ToArray();
                }
                if (bytes.Length <= 0 || bytes.Length > MaxAutoEnableStateBytes)
                    throw new InvalidDataException("待保存的状态超过 1 MiB 限制。");
                WriteFileAtomically(path, bytes);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryAppendActiveIds(string path, HashSet<ulong> ids,
            out int addedCount, out string error)
        {
            addedCount = 0;
            error = null;
            try
            {
                path = Path.GetFullPath(path);
                if (Directory.Exists(path))
                    throw new InvalidDataException("游戏 Mod 启用列表路径被目录占用。");

                string existing = string.Empty;
                var activeIds = new HashSet<ulong>();
                if (File.Exists(path))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("游戏 Mod 启用列表不能是重解析点。");
                    if (new FileInfo(path).Length > MaxAutoEnableStateBytes)
                        throw new InvalidDataException("游戏 Mod 启用列表超过 1 MiB 限制。");
                    existing = File.ReadAllText(path);
                    foreach (var rawLine in existing.Split(new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        var line = rawLine.Trim();
                        ulong existingId;
                        if (ulong.TryParse(line, NumberStyles.None,
                            CultureInfo.InvariantCulture, out existingId) && existingId != 0 &&
                            string.Equals(line, existingId.ToString(CultureInfo.InvariantCulture),
                                StringComparison.Ordinal))
                            activeIds.Add(existingId);
                    }
                }

                var appendIds = ids.Where(id => !activeIds.Contains(id)).OrderBy(id => id).ToArray();
                if (appendIds.Length == 0) return true;

                var builder = new StringBuilder(existing);
                if (builder.Length > 0 && builder[builder.Length - 1] != '\r' &&
                    builder[builder.Length - 1] != '\n')
                    builder.Append("\r\n");
                foreach (var id in appendIds)
                    builder.Append(id.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

                byte[] bytes = Utf8NoBom.GetBytes(builder.ToString());
                if (bytes.Length > MaxAutoEnableStateBytes)
                    throw new InvalidDataException("更新后的游戏 Mod 启用列表超过 1 MiB 限制。");
                WriteFileAtomically(path, bytes);
                addedCount = appendIds.Length;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool IsExistingReparsePoint(string path)
        {
            return (File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        private static void WriteFileAtomically(string path, byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("目标目录不存在: " + directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("目标目录不能是重解析点: " + directory);
            if (Directory.Exists(path))
                throw new InvalidDataException("目标文件路径被目录占用: " + path);
            if (File.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("目标文件不能是重解析点: " + path);

            var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." +
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                    File.Replace(temp, path, null, true);
                else
                    File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static bool TryValidateManifest(string path, out string error)
        {
            error = null;
            try
            {
                path = Path.GetFullPath(path);
                if (!File.Exists(path))
                {
                    error = "文件不存在。";
                    return false;
                }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "声明文件不能是重解析点。";
                    return false;
                }
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length <= 0)
                {
                    error = "文件为空。";
                    return false;
                }
                if (fileInfo.Length > MaxManifestBytes)
                {
                    error = "文件超过 64 KiB 限制。";
                    return false;
                }

                WorkshopPluginManifest manifest;
                var serializer = new DataContractJsonSerializer(typeof(WorkshopPluginManifest));
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    manifest = serializer.ReadObject(stream) as WorkshopPluginManifest;

                if (manifest == null)
                {
                    error = "无法读取声明对象。";
                    return false;
                }
                if (manifest.SchemaVersion != 1)
                {
                    error = "仅支持 schemaVersion=1。";
                    return false;
                }
                if (!string.Equals(manifest.Type, ManifestType, StringComparison.Ordinal))
                {
                    error = "type 必须为 bepinex-plugin。";
                    return false;
                }
                if (!string.Equals(manifest.PluginRoot, ManifestPluginRoot, StringComparison.Ordinal))
                {
                    error = "pluginRoot 必须为固定路径 BepInEx/plugins。";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static HashSet<ulong> ReadEnabledIds(string path, BridgeResult result)
        {
            var ids = new HashSet<ulong>();
            path = Path.GetFullPath(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("游戏 Mod 启用列表不能是重解析点。");
            if (new FileInfo(path).Length > MaxAutoEnableStateBytes)
                throw new InvalidDataException("游戏 Mod 启用列表超过 1 MiB 限制。");

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0) continue;

                ulong id;
                if (ulong.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
                    string.Equals(line, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    if (id != 0) ids.Add(id); else result.Warning("忽略无效的 Mod ID: 0");
                else
                    result.Warning("忽略无法解析的 Mod ID: " + line);
            }
            return ids;
        }

        private static void RemoveExistingLinks(string linkRoot, BridgeResult result)
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(linkRoot);
            }
            catch (Exception ex)
            {
                result.Error("无法枚举已有工坊链接: " + ex.Message);
                return;
            }

            foreach (var entry in entries)
            {
                try
                {
                    ulong bridgeId;
                    var entryName = Path.GetFileName(entry);
                    if (!ulong.TryParse(entryName, NumberStyles.None,
                        CultureInfo.InvariantCulture, out bridgeId) || bridgeId == 0 ||
                        !string.Equals(entryName, bridgeId.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal))
                    {
                        result.Warning("桥接目录中存在非标准 ID 项目，出于安全考虑保留: " + entry);
                        continue;
                    }

                    var attributes = File.GetAttributes(entry);
                    bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                    bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (!isDirectory || !isReparsePoint)
                    {
                        result.Warning("桥接目录中存在非链接项目，出于安全考虑保留: " + entry);
                        continue;
                    }

                    Directory.Delete(entry, false);
                    result.RemovedLinkCount++;
                }
                catch (Exception ex)
                {
                    result.Error("清理旧工坊链接失败 " + entry + ": " + ex.Message);
                }
            }
        }
    }

    internal static class JunctionManager
    {
        public static bool TryCreate(string junctionPath, string targetPath, out string error)
        {
            error = null;
            try
            {
                junctionPath = Path.GetFullPath(junctionPath);
                targetPath = Path.GetFullPath(targetPath);
                if (ContainsUnsafeCommandCharacter(junctionPath) || ContainsUnsafeCommandCharacter(targetPath))
                {
                    error = "路径包含目录联接命令不支持的字符。";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(junctionPath));
                var commandInterpreter = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

                var startInfo = new ProcessStartInfo
                {
                    FileName = commandInterpreter,
                    Arguments = "/D /V:OFF /C mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "无法启动系统目录联接命令。";
                        return false;
                    }

                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        error = "创建目录联接超时。";
                        return false;
                    }

                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    if (process.ExitCode != 0)
                    {
                        error = (stderr + " " + stdout).Trim();
                        if (error.Length == 0) error = "mklink 返回代码 " + process.ExitCode;
                        return false;
                    }
                }

                if (!Directory.Exists(junctionPath))
                {
                    error = "系统报告成功，但目录联接不存在。";
                    return false;
                }

                var attributes = File.GetAttributes(junctionPath);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    error = "创建出的目录不是重解析点，已拒绝使用。";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool ContainsUnsafeCommandCharacter(string path)
        {
            return path.IndexOfAny(new[]
            {
                '"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n'
            }) >= 0;
        }
    }

    internal sealed class VdfValue
    {
        private VdfValue(string scalar, Dictionary<string, VdfValue> children)
        {
            Scalar = scalar;
            Children = children;
        }

        public string Scalar { get; private set; }
        public Dictionary<string, VdfValue> Children { get; private set; }
        public bool IsObject { get { return Children != null; } }

        public static VdfValue FromScalar(string value)
        {
            return new VdfValue(value, null);
        }

        public static VdfValue FromObject(Dictionary<string, VdfValue> children)
        {
            return new VdfValue(null, children);
        }

        public bool TryGetValue(string key, out VdfValue value)
        {
            value = null;
            return IsObject && Children.TryGetValue(key, out value);
        }
    }

    internal static class SteamVdf
    {
        private enum TokenKind
        {
            String,
            OpenBrace,
            CloseBrace,
            End,
        }

        private sealed class Token
        {
            public TokenKind Kind;
            public string Value;
            public int Position;
        }

        private sealed class Parser
        {
            private const int MaxDepth = 16;
            private const int MaxTokens = 500000;
            private readonly string _text;
            private int _index;
            private int _tokenCount;
            private Token _current;

            public Parser(string text)
            {
                _text = text ?? throw new ArgumentNullException(nameof(text));
                _current = ReadToken();
            }

            public VdfValue ParseDocument()
            {
                var values = ParsePairs(expectClosingBrace: false, depth: 0);
                if (_current.Kind != TokenKind.End)
                    throw new InvalidDataException("VDF 文档结尾无效。");
                return VdfValue.FromObject(values);
            }

            private Dictionary<string, VdfValue> ParsePairs(bool expectClosingBrace, int depth)
            {
                if (depth > MaxDepth)
                    throw new InvalidDataException("VDF 嵌套层级过深。");

                var values = new Dictionary<string, VdfValue>(StringComparer.OrdinalIgnoreCase);
                while (_current.Kind != TokenKind.End && _current.Kind != TokenKind.CloseBrace)
                {
                    if (_current.Kind != TokenKind.String)
                        throw new InvalidDataException("VDF 键必须是引号字符串，位置 " +
                            _current.Position + "。");
                    string key = _current.Value;
                    Advance();

                    VdfValue value;
                    if (_current.Kind == TokenKind.String)
                    {
                        value = VdfValue.FromScalar(_current.Value);
                        Advance();
                    }
                    else if (_current.Kind == TokenKind.OpenBrace)
                    {
                        Advance();
                        value = VdfValue.FromObject(ParsePairs(expectClosingBrace: true, depth: depth + 1));
                    }
                    else
                    {
                        throw new InvalidDataException("VDF 键缺少字符串或对象值: " + key);
                    }

                    if (values.ContainsKey(key))
                        throw new InvalidDataException("VDF 对象含重复键: " + key);
                    values.Add(key, value);
                }

                if (expectClosingBrace)
                {
                    if (_current.Kind != TokenKind.CloseBrace)
                        throw new InvalidDataException("VDF 对象缺少右花括号。");
                    Advance();
                }
                else if (_current.Kind == TokenKind.CloseBrace)
                {
                    throw new InvalidDataException("VDF 文档含多余右花括号。");
                }
                return values;
            }

            private void Advance()
            {
                _current = ReadToken();
            }

            private Token ReadToken()
            {
                SkipWhitespaceAndComments();
                if (++_tokenCount > MaxTokens)
                    throw new InvalidDataException("VDF token 数超过安全限制。");
                if (_index >= _text.Length)
                    return new Token { Kind = TokenKind.End, Position = _index };

                int position = _index;
                char current = _text[_index++];
                if (current == '{')
                    return new Token { Kind = TokenKind.OpenBrace, Position = position };
                if (current == '}')
                    return new Token { Kind = TokenKind.CloseBrace, Position = position };
                if (current != '"')
                    throw new InvalidDataException("VDF 仅接受引号字符串，位置 " + position + "。");

                var builder = new StringBuilder();
                while (_index < _text.Length)
                {
                    char value = _text[_index++];
                    if (value == '"')
                    {
                        return new Token
                        {
                            Kind = TokenKind.String,
                            Value = builder.ToString(),
                            Position = position,
                        };
                    }
                    if (value == '\\')
                    {
                        if (_index >= _text.Length)
                            throw new InvalidDataException("VDF 字符串以不完整转义结尾。");
                        char escaped = _text[_index++];
                        if (escaped == '\\' || escaped == '"')
                            builder.Append(escaped);
                        else
                            builder.Append('\\').Append(escaped);
                        continue;
                    }
                    if (value == '\0' || value == '\r' || value == '\n')
                        throw new InvalidDataException("VDF 引号字符串含无效控制字符。");
                    builder.Append(value);
                }
                throw new InvalidDataException("VDF 引号字符串未闭合。");
            }

            private void SkipWhitespaceAndComments()
            {
                while (_index < _text.Length)
                {
                    char value = _text[_index];
                    if (char.IsWhiteSpace(value) || value == '\uFEFF')
                    {
                        _index++;
                        continue;
                    }
                    if (value == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                    {
                        _index += 2;
                        while (_index < _text.Length && _text[_index] != '\r' && _text[_index] != '\n')
                            _index++;
                        continue;
                    }
                    break;
                }
            }
        }

        public static VdfValue ReadFile(string path, int maxBytes)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                throw new FileNotFoundException("VDF 文件不存在。", path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("VDF 文件不能是重解析点。");

            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > maxBytes)
                    throw new InvalidDataException("VDF 文件为空或超过大小限制。");
                bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("读取 VDF 文件时提前结束。");
                    offset += read;
                }
                if (stream.ReadByte() != -1)
                    throw new InvalidDataException("读取期间 VDF 文件大小发生变化。");
            }

            string text = new UTF8Encoding(false, true).GetString(bytes);
            return new Parser(text).ParseDocument();
        }

        public static VdfValue RequireObject(VdfValue parent, string key)
        {
            VdfValue value;
            if (parent == null || !parent.TryGetValue(key, out value) || !value.IsObject)
                throw new InvalidDataException("VDF 缺少对象: " + key);
            return value;
        }

        public static string RequireScalar(VdfValue parent, string key)
        {
            VdfValue value;
            if (parent == null || !parent.TryGetValue(key, out value) || value.IsObject)
                throw new InvalidDataException("VDF 缺少字符串: " + key);
            return value.Scalar;
        }

        public static bool TryGetScalar(VdfValue parent, string key, out string scalar)
        {
            scalar = null;
            VdfValue value;
            if (parent == null || !parent.TryGetValue(key, out value)) return false;
            if (value.IsObject)
                throw new InvalidDataException("VDF 字段必须是字符串: " + key);
            scalar = value.Scalar;
            return true;
        }
    }

    internal static class SteamWorkshopMetadata
    {
        private const int MaxWorkshopMetadataBytes = 16 * 1024 * 1024;

        public static bool TryRead(string path, uint accountId,
            out WorkshopSubscriptionSnapshot snapshot, out string error)
        {
            snapshot = new WorkshopSubscriptionSnapshot();
            error = null;
            try
            {
                path = Path.GetFullPath(path);
                string expectedName = "appworkshop_" + BridgeOptions.WorkshopAppId + ".acf";
                if (!string.Equals(Path.GetFileName(path), expectedName, StringComparison.Ordinal))
                    throw new InvalidDataException("Workshop ACF 文件名无效。");

                VdfValue document = SteamVdf.ReadFile(path, MaxWorkshopMetadataBytes);
                VdfValue appWorkshop = SteamVdf.RequireObject(document, "AppWorkshop");
                if (!string.Equals(SteamVdf.RequireScalar(appWorkshop, "appid"),
                    BridgeOptions.WorkshopAppId, StringComparison.Ordinal))
                    throw new InvalidDataException("Workshop ACF 的 appid 不匹配。");

                VdfValue installed = SteamVdf.RequireObject(appWorkshop, "WorkshopItemsInstalled");
                VdfValue details = SteamVdf.RequireObject(appWorkshop, "WorkshopItemDetails");
                var installedManifests = new Dictionary<ulong, string>();
                foreach (var pair in installed.Children)
                {
                    ulong workshopId = ParseCanonicalWorkshopId(pair.Key, "WorkshopItemsInstalled");
                    if (!pair.Value.IsObject)
                        throw new InvalidDataException("WorkshopItemsInstalled 项目必须是对象。");
                    string manifest = SteamVdf.RequireScalar(pair.Value, "manifest");
                    ParseCanonicalNonZeroNumber(manifest, "已安装 manifest");
                    installedManifests.Add(workshopId, manifest);
                }

                string expectedAccountId = accountId.ToString(CultureInfo.InvariantCulture);
                foreach (var pair in details.Children)
                {
                    ulong workshopId = ParseCanonicalWorkshopId(pair.Key, "WorkshopItemDetails");
                    if (!pair.Value.IsObject)
                        throw new InvalidDataException("WorkshopItemDetails 项目必须是对象。");

                    string subscribedBy;
                    if (!SteamVdf.TryGetScalar(pair.Value, "subscribedby", out subscribedBy) ||
                        !string.Equals(subscribedBy, expectedAccountId, StringComparison.Ordinal))
                        continue;

                    snapshot.SubscribedIds.Add(workshopId);
                    string installedManifest;
                    if (!installedManifests.TryGetValue(workshopId, out installedManifest))
                        continue;

                    string detailManifest = SteamVdf.RequireScalar(pair.Value, "manifest");
                    ParseCanonicalNonZeroNumber(detailManifest, "订阅详情 manifest");
                    if (!string.Equals(detailManifest, installedManifest, StringComparison.Ordinal))
                        continue;

                    string latestManifest;
                    if (SteamVdf.TryGetScalar(pair.Value, "latest_manifest", out latestManifest))
                    {
                        ParseCanonicalNonZeroNumber(latestManifest, "latest_manifest");
                        if (!string.Equals(latestManifest, installedManifest, StringComparison.Ordinal))
                            continue;
                    }
                    snapshot.DownloadedIds.Add(workshopId);
                }
                return true;
            }
            catch (Exception ex)
            {
                snapshot = new WorkshopSubscriptionSnapshot();
                error = ex.Message;
                return false;
            }
        }

        private static ulong ParseCanonicalWorkshopId(string raw, string section)
        {
            ulong value;
            if (string.IsNullOrEmpty(raw) ||
                !ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                value == 0 || !string.Equals(raw,
                    value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidDataException(section + " 含非规范 Workshop ID。");
            return value;
        }

        private static void ParseCanonicalNonZeroNumber(string raw, string field)
        {
            ulong value;
            if (string.IsNullOrEmpty(raw) ||
                !ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                value == 0 || !string.Equals(raw,
                    value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new InvalidDataException(field + " 不是规范非零数字。");
        }
    }

    internal sealed class ActiveSteamUser
    {
        public string AccountId { get; set; }
        public string SteamId64 { get; set; }
        public string ActiveModListPath { get; set; }
        public string AutoEnableStatePath { get; set; }
    }

    internal static class SteamPathLocator
    {
        private const ulong SteamId64Base = 76561197960265728UL;
        private const int MaxAppManifestBytes = 4 * 1024 * 1024;

        public static string FindWorkshopRoot(string gameRootPath)
        {
            var steamAppsCandidates = new List<string>();
            try
            {
                AddCandidate(steamAppsCandidates, FindGameSteamAppsDirectory(gameRootPath));
                AddSteamInstallCandidate(steamAppsCandidates,
                    Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null));
                AddSteamInstallCandidate(steamAppsCandidates,
                    Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null));
                AddSteamInstallCandidate(steamAppsCandidates,
                    Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null));
            }
            catch
            {
                // The game library candidate is normally enough; continue with what was found.
            }

            // libraryfolders.vdf normally lives in the main Steam installation. Also
            // inspect a copy beside the game when present, which helps portable setups.
            foreach (var steamApps in steamAppsCandidates.ToArray())
            {
                try
                {
                    var libraryFolders = Path.Combine(steamApps, "libraryfolders.vdf");
                    if (!File.Exists(libraryFolders)) continue;
                    var text = File.ReadAllText(libraryFolders);
                    foreach (Match match in Regex.Matches(text,
                        @"""path""\s+""(?<path>[^""]+)""", RegexOptions.IgnoreCase))
                    {
                        var libraryRoot = DecodeVdfPath(match.Groups["path"].Value);
                        AddCandidate(steamAppsCandidates, Path.Combine(libraryRoot, "steamapps"));
                    }

                    // Compatibility with the old flat libraryfolders.vdf format.
                    foreach (Match match in Regex.Matches(text,
                        @"""[0-9]+""\s+""(?<path>[^""]+)""", RegexOptions.IgnoreCase))
                    {
                        var libraryRoot = DecodeVdfPath(match.Groups["path"].Value);
                        AddCandidate(steamAppsCandidates, Path.Combine(libraryRoot, "steamapps"));
                    }
                }
                catch
                {
                    // Ignore a malformed/unreadable VDF and continue with other libraries.
                }
            }

            string fallback = null;
            foreach (var steamApps in steamAppsCandidates)
            {
                try
                {
                    var workshopRoot = Path.Combine(steamApps, "workshop", "content",
                        BridgeOptions.WorkshopAppId);
                    if (fallback == null) fallback = workshopRoot;
                    if (Directory.Exists(workshopRoot)) return workshopRoot;
                }
                catch
                {
                    // Ignore this candidate.
                }
            }
            return fallback;
        }

        public static string FindWorkshopMetadata(string workshopRootPath)
        {
            if (string.IsNullOrWhiteSpace(workshopRootPath)) return null;
            try
            {
                var appDirectory = new DirectoryInfo(Path.GetFullPath(workshopRootPath));
                var contentDirectory = appDirectory.Parent;
                var workshopDirectory = contentDirectory == null ? null : contentDirectory.Parent;
                if (!string.Equals(appDirectory.Name, BridgeOptions.WorkshopAppId,
                        StringComparison.Ordinal) ||
                    contentDirectory == null || !string.Equals(contentDirectory.Name, "content",
                        StringComparison.OrdinalIgnoreCase) ||
                    workshopDirectory == null || !string.Equals(workshopDirectory.Name, "workshop",
                        StringComparison.OrdinalIgnoreCase))
                    return null;

                return Path.GetFullPath(Path.Combine(workshopDirectory.FullName,
                    "appworkshop_" + BridgeOptions.WorkshopAppId + ".acf"));
            }
            catch
            {
                return null;
            }
        }

        public static ActiveSteamUser FindActiveUser(string gameRootPath)
        {
            uint accountId;
            if (!TryReadRegistryAccountId(out accountId) &&
                !TryReadLastOwnerAccountId(gameRootPath, out accountId))
                return null;

            string savesRoot = FindSavesRoot();
            if (string.IsNullOrWhiteSpace(savesRoot)) return null;
            try
            {
                string steamId64 = (SteamId64Base + accountId)
                    .ToString(CultureInfo.InvariantCulture);
                savesRoot = Path.GetFullPath(savesRoot);
                string profileDirectory = Path.GetFullPath(Path.Combine(savesRoot, steamId64));
                if (!string.Equals(Path.GetDirectoryName(profileDirectory), savesRoot,
                    StringComparison.OrdinalIgnoreCase) || !Directory.Exists(profileDirectory))
                    return null;

                return new ActiveSteamUser
                {
                    AccountId = accountId.ToString(CultureInfo.InvariantCulture),
                    SteamId64 = steamId64,
                    ActiveModListPath = Path.Combine(profileDirectory, "_mod"),
                    AutoEnableStatePath = Path.Combine(profileDirectory,
                        BridgeOptions.AutoEnableStateFileName),
                };
            }
            catch
            {
                return null;
            }
        }

        public static string FindActiveModList(string gameRootPath)
        {
            var activeUser = FindActiveUser(gameRootPath);
            return activeUser != null && File.Exists(activeUser.ActiveModListPath)
                ? activeUser.ActiveModListPath
                : null;
        }

        internal static bool IsMatchingSteamIdentity(uint accountId, string steamId64)
        {
            if (accountId == 0 || string.IsNullOrEmpty(steamId64)) return false;
            ulong parsed;
            return ulong.TryParse(steamId64, NumberStyles.None, CultureInfo.InvariantCulture,
                       out parsed) &&
                   string.Equals(steamId64, parsed.ToString(CultureInfo.InvariantCulture),
                       StringComparison.Ordinal) &&
                   parsed == SteamId64Base + accountId;
        }

        private static bool TryReadRegistryAccountId(out uint accountId)
        {
            accountId = 0;
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess",
                    "ActiveUser", null);
                if (value == null) return false;

                ulong parsed;
                if (value is int)
                    parsed = unchecked((uint)(int)value);
                else if (value is uint)
                    parsed = (uint)value;
                else if (!ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                    return false;

                if (parsed == 0 || parsed > uint.MaxValue) return false;
                accountId = (uint)parsed;
                return true;
            }
            catch
            {
                accountId = 0;
                return false;
            }
        }

        private static bool TryReadLastOwnerAccountId(string gameRootPath, out uint accountId)
        {
            accountId = 0;
            try
            {
                var steamAppsDirectory = FindGameSteamAppsDirectory(gameRootPath);
                if (string.IsNullOrEmpty(steamAppsDirectory)) return false;
                var appManifest = Path.Combine(steamAppsDirectory,
                    "appmanifest_" + BridgeOptions.WorkshopAppId + ".acf");
                VdfValue document = SteamVdf.ReadFile(appManifest, MaxAppManifestBytes);
                VdfValue appState = SteamVdf.RequireObject(document, "AppState");
                if (!string.Equals(SteamVdf.RequireScalar(appState, "appid"),
                    BridgeOptions.WorkshopAppId, StringComparison.Ordinal))
                    return false;

                string rawLastOwner = SteamVdf.RequireScalar(appState, "LastOwner");
                ulong steamId64;
                if (!ulong.TryParse(rawLastOwner, NumberStyles.None,
                        CultureInfo.InvariantCulture, out steamId64) ||
                    !string.Equals(rawLastOwner, steamId64.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal) || steamId64 <= SteamId64Base)
                    return false;

                ulong difference = steamId64 - SteamId64Base;
                if (difference == 0 || difference > uint.MaxValue) return false;
                accountId = (uint)difference;
                return true;
            }
            catch
            {
                accountId = 0;
                return false;
            }
        }

        private static string FindSavesRoot()
        {
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var appData = Directory.GetParent(local);
                if (appData == null) return null;
                return Path.Combine(appData.FullName, "LocalLow", "PakyiGame", "StudentAge", "Saves");
            }
            catch
            {
                return null;
            }
        }

        private static void AddSteamInstallCandidate(List<string> candidates, object registryValue)
        {
            var steamRoot = registryValue as string;
            if (string.IsNullOrWhiteSpace(steamRoot)) return;
            AddCandidate(candidates, Path.Combine(
                steamRoot.Replace('/', Path.DirectorySeparatorChar), "steamapps"));
        }

        private static void AddCandidate(List<string> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (!Path.IsPathRooted(path)) return;
                path = Path.GetFullPath(path.Trim());
                if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(path);
            }
            catch
            {
                // Ignore malformed candidate paths.
            }
        }

        private static string DecodeVdfPath(string path)
        {
            return (path ?? string.Empty).Replace(@"\\", @"\")
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static string FindGameSteamAppsDirectory(string gameRootPath)
        {
            try
            {
                var commonDirectory = Directory.GetParent(Path.GetFullPath(gameRootPath));
                var steamAppsDirectory = commonDirectory == null ? null : commonDirectory.Parent;
                return steamAppsDirectory == null ? null : steamAppsDirectory.FullName;
            }
            catch
            {
                return null;
            }
        }
    }

}
