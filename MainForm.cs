using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using StudentAge.WorkshopBridge;
using StudentAgeModManager.Core;

namespace StudentAgeModManager
{
    public class MainForm : Form
    {
        private const string ContributionUrl =
            "https://github.com/white12666/StudentAgeModManager/blob/main/CONTRIBUTING.md";
        private const string ManagerReleaseUrl =
            "https://github.com/white12666/StudentAgeModManager/releases/latest";
        private static readonly Font SectionFont =
            new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        private static readonly Font SubmissionFont =
            new Font("Microsoft YaHei UI", 8f);

        private readonly Label _lblGameDir = new Label();
        private readonly Label _lblBepInEx = new Label();
        private readonly Button _btnRefresh = new Button();
        private readonly Button _btnInstallBep = new Button();
        private readonly WheelFlowLayoutPanel _flow = new WheelFlowLayoutPanel();
        private readonly Label _lblStatus = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Panel _banner = new Panel();
        private readonly Label _bannerText = new Label();
        private readonly Panel _workshopGuide = new Panel();
        private readonly Label _workshopSetupTitle = new Label();
        private readonly Label _workshopSetupText = new Label();
        private readonly Label _workshopManageTitle = new Label();
        private readonly Label _workshopManageText = new Label();
        private readonly Panel _submissionFooter = new Panel();
        private readonly LinkLabel _workshopSubmissionLink = new LinkLabel();

        private string _gameDir;
        private LocalState _state;
        private ModInstaller _installer;
        private LocalPluginScanner _pluginScanner;
        private LocalPluginManager _localPluginManager;
        private readonly Downloader _downloader = new Downloader();
        private readonly WorkshopPageLauncher _workshopPageLauncher =
            new WorkshopPageLauncher();
        private Func<string, BridgeResult> _workshopSynchronizer = gameDir =>
            WorkshopBridgeSynchronizer.Synchronize(gameDir);
        private Func<string, WorkshopDiscoveryResult> _workshopDiscoverer = gameDir =>
            WorkshopBridgeManagement.Discover(gameDir);
        private Func<string, string, bool, WorkshopToggleResult> _workshopToggler =
            (gameDir, workshopId, enabled) => WorkshopBridgeManagement.SetEnabled(
                gameDir, workshopId, enabled);
        private Func<bool> _isGameRunning = ModInstaller.IsGameRunning;
        private Action<string> _workshopToggleErrorPresenter;
        private IndexClient _indexClient;
        private ModIndex _index;
        private WorkshopDiscoveryResult _workshopDiscovery;
        private List<LocalPluginUnit> _localUnits = new List<LocalPluginUnit>();
        private int _localPluginCount;
        private bool _busy;
        private bool _initializeOnShown = true;
        private bool _closePending;
        private bool _allowClose;

        public MainForm()
        {
            Text = "StudentAge Mod 管理器 v" + CurrentVersion();
            try
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    typeof(MainForm).Assembly.Location);
            }
            catch { }
            ClientSize = new Size(620, 620);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(245, 245, 248);
            _workshopToggleErrorPresenter = message => MessageBox.Show(this, message,
                "工坊操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            BuildLayout();
            Shown += async (s, e) =>
            {
                if (_initializeOnShown) await InitializeAsync();
            };
            FormClosing += MainForm_FormClosing;
        }

        private static string CurrentVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }

        private void BuildLayout()
        {
            // ── 顶部信息区 ──
            _lblGameDir.Location = new Point(14, 12);
            _lblGameDir.Size = new Size(590, 18);
            _lblGameDir.AutoEllipsis = true;

            _lblBepInEx.Location = new Point(14, 34);
            _lblBepInEx.Size = new Size(500, 22);
            _lblBepInEx.AutoEllipsis = true;

            _btnRefresh.Text = "刷新";
            _btnRefresh.Location = new Point(531, 30);
            _btnRefresh.Size = new Size(75, 26);
            _btnRefresh.Click += async (s, e) => await RefreshIndexAsync(true);

            // ── 工坊操作说明（始终显示） ──
            _workshopGuide.Location = new Point(0, 62);
            _workshopGuide.Size = new Size(620, 130);
            _workshopGuide.BackColor = Color.FromArgb(231, 243, 255);
            _workshopSetupTitle.Location = new Point(14, 6);
            _workshopSetupTitle.Size = new Size(592, 19);
            _workshopSetupTitle.Font = new Font(Font, FontStyle.Bold);
            _workshopSetupTitle.ForeColor = Color.FromArgb(25, 64, 104);
            _workshopSetupTitle.Text = "第一次使用前的准备";
            _workshopSetupText.Location = new Point(14, 27);
            _workshopSetupText.Size = new Size(592, 36);
            _workshopSetupText.ForeColor = Color.FromArgb(35, 78, 121);
            _workshopSetupText.Text =
                "点击“一键安装完整前置”即可完成安装（内置包，无需联网）。之后在 Steam 创意工坊订阅 Mod，\r\n" +
                "下载完成后点“刷新”，或下次启动游戏时自动启用。";

            _workshopManageTitle.Location = new Point(14, 68);
            _workshopManageTitle.Size = new Size(592, 19);
            _workshopManageTitle.Font = new Font(Font, FontStyle.Bold);
            _workshopManageTitle.ForeColor = Color.FromArgb(25, 64, 104);
            _workshopManageTitle.Text = "如何管理 Mod";
            _workshopManageText.Location = new Point(14, 89);
            _workshopManageText.Size = new Size(592, 36);
            _workshopManageText.ForeColor = Color.FromArgb(35, 78, 121);
            _workshopManageText.Text =
                "订阅和取消订阅请在 Steam 中操作；游戏关闭时，可在下方列表或游戏内“本地”页开关 Mod。\r\n" +
                "不在推荐列表中的工坊 Mod 也能正常使用；已禁用的 Mod 仍会随 Steam 更新。";

            _workshopGuide.Controls.AddRange(new Control[]
            {
                _workshopSetupTitle, _workshopSetupText, _workshopManageTitle,
                _workshopManageText,
            });

            // ── BepInEx 缺失提示条 ──
            _banner.Location = new Point(0, 196);
            _banner.Size = new Size(620, 34);
            _banner.BackColor = Color.FromArgb(255, 243, 205);
            _banner.Visible = false;
            _bannerText.Text = "未检测到运行 Mod 所需的前置组件。";
            _bannerText.Location = new Point(14, 8);
            _bannerText.AutoSize = true;
            _bannerText.ForeColor = Color.FromArgb(133, 100, 4);
            _btnInstallBep.Text = "一键安装完整前置";
            _btnInstallBep.Location = new Point(450, 4);
            _btnInstallBep.Size = new Size(150, 26);
            _btnInstallBep.Click += async (s, e) => await InstallBepInExAsync();
            _banner.Controls.Add(_bannerText);
            _banner.Controls.Add(_btnInstallBep);

            // ── 卡片列表 ──
            _flow.Location = new Point(14, 196);
            _flow.Size = new Size(592, 548 - _flow.Top);
            _flow.AutoScroll = true;
            _flow.FlowDirection = FlowDirection.TopDown;
            _flow.WrapContents = false;

            // ── 固定投稿页脚（位于滚动列表下方） ──
            _submissionFooter.Location = new Point(0, 552);
            _submissionFooter.Size = new Size(620, 24);
            _submissionFooter.BackColor = Color.FromArgb(238, 240, 244);
            const string submissionText =
                "“已收录”表示 Mod 已进入官方推荐列表，可自定义显示名称与简介。欢迎 Mod 作者在 GitHub 提交收录。";
            const string submissionLinkText = "GitHub 提交收录";
            _workshopSubmissionLink.Text = submissionText;
            _workshopSubmissionLink.Location = new Point(14, 3);
            _workshopSubmissionLink.Size = new Size(592, 18);
            _workshopSubmissionLink.Font = SubmissionFont;
            _workshopSubmissionLink.TextAlign = ContentAlignment.MiddleLeft;
            _workshopSubmissionLink.LinkColor = Color.FromArgb(24, 91, 168);
            _workshopSubmissionLink.ActiveLinkColor = Color.FromArgb(170, 50, 50);
            _workshopSubmissionLink.VisitedLinkColor = _workshopSubmissionLink.LinkColor;
            _workshopSubmissionLink.LinkBehavior = LinkBehavior.HoverUnderline;
            _workshopSubmissionLink.Links.Add(
                submissionText.IndexOf(submissionLinkText, StringComparison.Ordinal),
                submissionLinkText.Length, ContributionUrl);
            _workshopSubmissionLink.LinkClicked += (s, e) =>
                OpenContributionPage(e.Link.LinkData as string);
            _submissionFooter.Controls.Add(_workshopSubmissionLink);

            // ── 底部状态栏 ──
            _lblStatus.Location = new Point(14, 580);
            _lblStatus.Size = new Size(430, 18);
            _lblStatus.AutoEllipsis = true;
            _lblStatus.Text = "就绪";

            _progress.Location = new Point(450, 578);
            _progress.Size = new Size(156, 18);
            _progress.Visible = false;

            Controls.AddRange(new Control[]
            {
                _lblGameDir, _lblBepInEx, _btnRefresh, _workshopGuide,
                _banner, _flow, _submissionFooter, _lblStatus, _progress,
            });
        }

        // ═══════════════ 初始化 ═══════════════

        private async Task InitializeAsync()
        {
            _gameDir = GameLocator.Locate();
            while (!GameLocator.IsValidGameDir(_gameDir))
            {
                MessageBox.Show(this,
                    "未能自动找到 StudentAge 游戏目录。\n请在接下来的窗口中手动选择游戏安装目录（包含 StudentAge.exe）。",
                    "选择游戏目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                using (var dlg = new FolderBrowserDialog { Description = "选择 StudentAge 游戏目录" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) { Close(); return; }
                    _gameDir = dlg.SelectedPath;
                }
            }

            _lblGameDir.Text = "游戏目录: " + _gameDir;
            _state = new LocalState(_gameDir);
            _installer = new ModInstaller(_state);
            _pluginScanner = new LocalPluginScanner();
            _localPluginManager = new LocalPluginManager(_state);
            var workshopMetadata = new WorkshopMetadataService(
                new SteamWorkshopMetadataProvider(), _state.WorkshopMetadataCachePath);
            _indexClient = new IndexClient(_downloader, workshopMetadata);

            UpdateBepInExUi();
            await RefreshIndexAsync();
        }

        private void UpdateBepInExUi()
        {
            bool bepinExInstalled = _installer.IsBepInExInstalled();
            bool bridgeCurrent = bepinExInstalled && _installer.IsWorkshopBridgeCurrent();
            if (!bepinExInstalled)
            {
                _lblBepInEx.Text = "✘ 未安装 BepInEx 与工坊 Mod 支持";
                _lblBepInEx.ForeColor = Color.Firebrick;
                _bannerText.Text = "未检测到运行 Mod 所需的前置组件。";
                _btnInstallBep.Text = "一键安装完整前置";
            }
            else if (!bridgeCurrent)
            {
                _lblBepInEx.Text = "⚠ 工坊 Mod 支持缺失或需更新";
                _lblBepInEx.ForeColor = Color.DarkOrange;
                _bannerText.Text = "工坊 Mod 支持组件缺失或需更新，请点右侧按钮安装。";
                _btnInstallBep.Text = "安装工坊 Mod 支持";
            }
            else
            {
                _lblBepInEx.Text = "✔ BepInEx 与工坊 Mod 支持已安装";
                _lblBepInEx.ForeColor = Color.Green;
            }
            bool showBanner = !bepinExInstalled || !bridgeCurrent;
            _banner.Visible = showBanner;
            int normalListTop = _workshopGuide.Bottom + 4;
            _flow.Location = new Point(14,
                showBanner ? _banner.Bottom + 4 : normalListTop);
            _flow.Height = _submissionFooter.Top - 4 - _flow.Top;
        }

        // ═══════════════ 索引拉取与列表渲染 ═══════════════

        private async Task RefreshIndexAsync(bool synchronizeWorkshop = false)
        {
            if (_busy) return;
            int listScrollOffset = CurrentListScrollOffset();
            SetBusy(true, synchronizeWorkshop
                ? "正在同步工坊并刷新列表…"
                : "正在获取工坊列表并扫描本地插件…");
            Task<WorkshopRefreshOutcome> workshopSync =
                SynchronizeWorkshopForRefreshAsync(synchronizeWorkshop);
            // Manual refresh discovers state only after synchronization; startup discovery can
            // run alongside the online index fetch because it does not mutate anything.
            Task<WorkshopDiscoveryResult> workshopDiscovery = synchronizeWorkshop
                ? null
                : DiscoverWorkshopItemsAsync();
            Exception indexError = null;
            bool keptPreviousIndex = _index != null;
            try
            {
                try
                {
                    _index = await _indexClient.FetchAsync();
                }
                catch (Exception ex)
                {
                    indexError = ex;
                    if (_index == null)
                        _index = new ModIndex { mods = new List<ModEntry>() };
                }

                WorkshopRefreshOutcome workshopOutcome = await workshopSync;
                if (workshopDiscovery == null)
                    workshopDiscovery = DiscoverWorkshopItemsAsync();
                _workshopDiscovery = await workshopDiscovery;
                _localUnits = await ScanLocalPluginsAsync(_workshopDiscovery != null &&
                    _workshopDiscovery.Succeeded ? _workshopDiscovery.Items : null);
                RenderListAtScrollOffset(listScrollOffset);
                string workshopNote = DescribeWorkshopRefresh(workshopOutcome) +
                    DescribeWorkshopDiscovery(_workshopDiscovery);
                if (indexError == null)
                {
                    CheckSelfUpdate();
                    SetStatus(workshopNote + "列表已更新（工坊 Mod " + _index.mods.Count +
                        " 个，已安装 Mod " + _localPluginCount + " 个，推荐列表更新于 " +
                        (_index.updatedAt ?? "?") + "）");
                }
                else
                {
                    string previousNote = keptPreviousIndex ? "保留上次推荐列表；" : string.Empty;
                    SetStatus(workshopNote + "工坊列表获取失败；" + previousNote + "已显示 " +
                        _localPluginCount + " 个已安装 Mod。" + indexError.Message);
                    MessageBox.Show(this,
                        "无法获取工坊列表，" +
                        (keptPreviousIndex ? "当前保留上次列表；" : string.Empty) +
                        "已安装的 Mod 仍可管理。\n\n" + indexError.Message,
                        "网络错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                SetStatus("刷新失败: " + ex.Message);
                MessageBox.Show(this,
                    "扫描或渲染 Mod 列表时发生错误，已保留当前可用界面。\n\n" + ex.Message,
                    "刷新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private Task<WorkshopDiscoveryResult> DiscoverWorkshopItemsAsync()
        {
            if (string.IsNullOrWhiteSpace(_gameDir))
                return Task.FromResult(new WorkshopDiscoveryResult());
            string gameDir = _gameDir;
            return Task.Run(() => _workshopDiscoverer(gameDir));
        }

        private static string DescribeWorkshopDiscovery(WorkshopDiscoveryResult discovery)
        {
            return discovery != null && !discovery.Succeeded &&
                   !string.IsNullOrWhiteSpace(discovery.Error)
                ? "工坊本地状态不可用；"
                : string.Empty;
        }

        private Task<WorkshopRefreshOutcome> SynchronizeWorkshopForRefreshAsync(bool requested)
        {
            if (!requested)
                return Task.FromResult(new WorkshopRefreshOutcome());

            string gameDir = _gameDir;
            ModInstaller installer = _installer;
            return Task.Run(() =>
            {
                if (installer == null || !installer.IsBepInExInstalled() ||
                    !installer.IsWorkshopBridgeInstalled())
                    return new WorkshopRefreshOutcome
                    {
                        State = WorkshopRefreshState.PrerequisiteMissing,
                    };

                if (_isGameRunning())
                    return new WorkshopRefreshOutcome
                    {
                        State = WorkshopRefreshState.GameRunning,
                    };

                try
                {
                    BridgeResult result = _workshopSynchronizer(gameDir);
                    WriteWorkshopRefreshLog(gameDir, result, null);
                    return new WorkshopRefreshOutcome
                    {
                        State = WorkshopRefreshState.Completed,
                        Result = result,
                    };
                }
                catch (Exception ex)
                {
                    WriteWorkshopRefreshLog(gameDir, null, ex);
                    return new WorkshopRefreshOutcome
                    {
                        State = WorkshopRefreshState.Failed,
                    };
                }
            });
        }

        private static string DescribeWorkshopRefresh(WorkshopRefreshOutcome outcome)
        {
            if (outcome == null || outcome.State == WorkshopRefreshState.NotRequested ||
                outcome.State == WorkshopRefreshState.PrerequisiteMissing)
                return string.Empty;
            if (outcome.State == WorkshopRefreshState.GameRunning)
                return "游戏运行中，已跳过工坊同步；";
            if (outcome.State == WorkshopRefreshState.Failed)
                return "工坊同步失败（见 BepInEx/ModManager/WorkshopRefresh.log）；";

            BridgeResult result = outcome.Result;
            if (result == null || result.ErrorCount > 0 || !result.Synchronized)
                return "工坊同步未完全成功（见 BepInEx/ModManager/WorkshopRefresh.log）；";

            var changes = new List<string>();
            if (result.BaselineIdCount > 0)
                changes.Add("已记录现有订阅 " + result.BaselineIdCount + " 个");
            if (result.AutoEnabledIdCount > 0)
                changes.Add("自动启用 " + result.AutoEnabledIdCount + " 个");
            if (result.LinkedCount > 0)
                changes.Add("新启用 " + result.LinkedCount + " 个");
            if (result.RemovedLinkCount > 0)
                changes.Add("清理失效 " + result.RemovedLinkCount + " 个");
            return changes.Count == 0
                ? "工坊已同步；"
                : "工坊已同步（" + string.Join("，", changes) + "）；";
        }

        private static void WriteWorkshopRefreshLog(string gameDir, BridgeResult result,
            Exception error)
        {
            try
            {
                string path = Path.Combine(gameDir, "BepInEx", "ModManager",
                    "WorkshopRefresh.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var writer = new StreamWriter(path, false))
                {
                    writer.WriteLine("StudentAge Workshop synchronization from Mod Manager");
                    writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    if (error != null)
                    {
                        writer.WriteLine("Failed before synchronization.");
                        writer.WriteLine();
                        writer.WriteLine(error);
                        return;
                    }

                    writer.WriteLine("Synchronized: " + result.Synchronized);
                    writer.WriteLine("Enabled IDs: " + result.EnabledIdCount);
                    writer.WriteLine("Baseline IDs: " + result.BaselineIdCount);
                    writer.WriteLine("Auto-enabled IDs: " + result.AutoEnabledIdCount);
                    writer.WriteLine("Linked: " + result.LinkedCount);
                    writer.WriteLine("Removed stale links: " + result.RemovedLinkCount);
                    writer.WriteLine("Skipped: " + result.SkippedCount);
                    writer.WriteLine("Errors: " + result.ErrorCount);
                    writer.WriteLine();
                    foreach (string message in result.Messages) writer.WriteLine(message);
                }
            }
            catch
            {
                // Refresh remains usable even if diagnostics cannot be written.
            }
        }

        private enum WorkshopRefreshState
        {
            NotRequested,
            PrerequisiteMissing,
            GameRunning,
            Completed,
            Failed,
        }

        private sealed class WorkshopRefreshOutcome
        {
            public WorkshopRefreshState State { get; set; }
            public BridgeResult Result { get; set; }
        }

        private Task<List<LocalPluginUnit>> ScanLocalPluginsAsync(
            IEnumerable<WorkshopManagedItem> workshopItems = null)
        {
            if (_pluginScanner == null || string.IsNullOrEmpty(_gameDir))
                return Task.FromResult(new List<LocalPluginUnit>());
            string gameDir = _gameDir;
            List<WorkshopManagedItem> items = workshopItems?.ToList();
            return Task.Run(() => _pluginScanner.Scan(gameDir, items));
        }

        private void RenderList()
        {
            RenderListAtScrollOffset(CurrentListScrollOffset());
        }

        private void RenderListAtScrollOffset(int requestedScrollOffset)
        {
            _flow.SuspendLayout();
            ClearRenderedControls();

            var indexedMods = _index?.mods ?? new List<ModEntry>();
            var localUnits = _localUnits ?? new List<LocalPluginUnit>();
            var indexedWorkshopIds = new HashSet<string>(indexedMods
                .Select(mod =>
                {
                    string id;
                    return WorkshopItem.TryGetId(mod, out id) ? id : null;
                })
                .Where(id => id != null), StringComparer.Ordinal);
            _localPluginCount = localUnits.Count(unit =>
                unit.Source != LocalPluginSource.SteamWorkshop ||
                unit.HasWorkshopManifest || indexedWorkshopIds.Contains(unit.WorkshopId));
            var consumedWorkshopUnits = new HashSet<LocalPluginUnit>();
            var visibleWorkshopUnits = localUnits.Where(unit =>
                unit.Source == LocalPluginSource.SteamWorkshop &&
                unit.HasWorkshopManifest).ToList();
            bool hasWorkshopSection = indexedMods.Count > 0 || visibleWorkshopUnits.Count > 0;

            if (hasWorkshopSection)
            {
                _flow.Controls.Add(CreateSectionLabel("Steam 创意工坊目录"));
                foreach (var mod in indexedMods)
                {
                    string workshopId;
                    LocalPluginUnit installedUnit = null;
                    if (WorkshopItem.TryGetId(mod, out workshopId))
                    {
                        installedUnit = localUnits.FirstOrDefault(unit =>
                            unit.Source == LocalPluginSource.SteamWorkshop &&
                            string.Equals(unit.WorkshopId, workshopId, StringComparison.Ordinal));
                        if (installedUnit != null) consumedWorkshopUnits.Add(installedUnit);
                    }

                    var card = new ModCard();
                    card.Bind(mod, installedUnit);
                    card.WorkshopPageClicked += OpenWorkshopPage;
                    card.ToggleLocalClicked += TogglePlugin;
                    card.SetBusy(_busy);
                    _flow.Controls.Add(card);
                }

                foreach (var unit in visibleWorkshopUnits.Where(unit =>
                    !consumedWorkshopUnits.Contains(unit)))
                {
                    var card = new ModCard();
                    card.BindLocal(unit);
                    card.WorkshopPageClicked += OpenWorkshopPage;
                    card.ToggleLocalClicked += TogglePlugin;
                    card.SetBusy(_busy);
                    _flow.Controls.Add(card);
                }
            }

            var localPluginUnits = localUnits.Where(unit =>
                unit.Source != LocalPluginSource.SteamWorkshop).ToList();
            if (localPluginUnits.Count > 0)
            {
                _flow.Controls.Add(CreateSectionLabel("本地已安装插件"));
                foreach (var unit in localPluginUnits)
                {
                    var card = new ModCard();
                    card.BindLocal(unit);
                    card.WorkshopPageClicked += OpenWorkshopPage;
                    card.ToggleLocalClicked += TogglePlugin;
                    card.SetBusy(_busy);
                    _flow.Controls.Add(card);
                }
            }

            if (!hasWorkshopSection && localPluginUnits.Count == 0)
                _flow.Controls.Add(CreateSectionLabel("未发现工坊 Mod 或本地插件。"));
            _flow.ResumeLayout(true);
            RestoreListScrollOffset(requestedScrollOffset);
        }

        private int CurrentListScrollOffset()
        {
            return Math.Max(0, -_flow.AutoScrollPosition.Y);
        }

        private void RestoreListScrollOffset(int requestedScrollOffset)
        {
            _flow.PerformLayout();
            int maximum = Math.Max(0,
                _flow.DisplayRectangle.Height - _flow.ClientSize.Height);
            int target = Math.Max(0, Math.Min(requestedScrollOffset, maximum));
            _flow.AutoScrollPosition = new Point(0, target);
        }

        private void ClearRenderedControls()
        {
            while (_flow.Controls.Count > 0)
            {
                Control control = _flow.Controls[0];
                _flow.Controls.RemoveAt(0);
                control.Dispose();
            }
        }

        private static Label CreateSectionLabel(string text)
        {
            return new Label
            {
                Text = text,
                Font = SectionFont,
                ForeColor = Color.FromArgb(65, 65, 75),
                Size = new Size(560, 25),
                Padding = new Padding(6, 4, 0, 0),
                Margin = new Padding(6, 5, 6, 0),
            };
        }

        private void CheckSelfUpdate()
        {
            if (_index.manager == null || string.IsNullOrEmpty(_index.manager.version)) return;
            if (LocalState.VersionCompare(CurrentVersion(), _index.manager.version) < 0)
            {
                var r = MessageBox.Show(this,
                    "管理器有新版本 " + _index.manager.version + "（当前 " + CurrentVersion() + "）。\n是否打开下载页面？",
                    "管理器更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes)
                {
                    // The text index may arrive through a third-party fallback mirror. Never
                    // allow it to choose an executable download destination.
                    try { Process.Start(ManagerReleaseUrl); } catch { }
                }
            }
        }

        // ═══════════════ 操作 ═══════════════

        private void OpenWorkshopPage(string workshopId)
        {
            if (_busy) return;
            try
            {
                WorkshopPageTarget target = _workshopPageLauncher.Open(workshopId);
                SetStatus((target == WorkshopPageTarget.SteamClient
                        ? "已在 Steam 客户端打开工坊页面。"
                        : "已在浏览器打开工坊页面。") +
                    "订阅后待下载完成，点“刷新”即可启用。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开创意工坊", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private async void TogglePlugin(LocalPluginUnit unit)
        {
            if (_busy) return;
            if (unit == null) return;
            if (unit.Source == LocalPluginSource.SteamWorkshop)
            {
                await ToggleWorkshopPluginAsync(unit);
                return;
            }
            if (unit.HasPathConflict && !ConfirmConflictResolution(unit)) return;

            int listScrollOffset = CurrentListScrollOffset();
            bool enabling = unit.IsDisabled;
            SetBusy(true, (enabling ? "正在启用 " : "正在禁用 ") + unit.DisplayName + "…");
            try
            {
                string archivedConflictCopy = enabling
                    ? _localPluginManager.Enable(unit)
                    : _localPluginManager.Disable(unit);

                _localUnits = await ScanLocalPluginsAsync(_workshopDiscovery != null &&
                    _workshopDiscovery.Succeeded ? _workshopDiscovery.Items : null);
                RenderListAtScrollOffset(listScrollOffset);
                SetStatus(unit.DisplayName + (enabling ? " 已启用" : " 已禁用") +
                    (archivedConflictCopy != null
                        ? "；冲突的另一副本已归档到 " + archivedConflictCopy
                        : "") +
                    "（重启游戏生效）");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                await RescanAfterMutationFailureAsync(listScrollOffset);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        /// <summary>
        /// 路径冲突下的启用/禁用会顶替另一份副本。操作前必须让作者明确知道
        /// 保留的是哪一份——冲突双方版本号往往相同（开发构建重新部署即复现），
        /// 点错方向会把新版归档、旧版上位，且界面上看不出任何异常。
        /// </summary>
        private bool ConfirmConflictResolution(LocalPluginUnit unit)
        {
            LocalPluginUnit other = _localUnits != null
                ? _localUnits.FirstOrDefault(candidate =>
                    !ReferenceEquals(candidate, unit) &&
                    candidate.Source == LocalPluginSource.Local &&
                    string.Equals(candidate.UnitKey, unit.UnitKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    candidate.IsDisabled != unit.IsDisabled)
                : null;

            string message =
                "“" + unit.DisplayName + "”存在路径冲突，两份副本：\n\n" +
                "保留并" + (unit.IsDisabled ? "启用" : "禁用") + "：\n  " +
                unit.RelativePath + DescribeWriteTime(unit) + "\n" +
                "归档到 ModManager\\conflict-backup：\n  " +
                (other != null ? other.RelativePath : "另一路径的同名副本") +
                DescribeWriteTime(other) + "\n";
            if (unit.LastWriteTimeUtc != null && other != null &&
                other.LastWriteTimeUtc != null &&
                unit.LastWriteTimeUtc < other.LastWriteTimeUtc)
            {
                message += "\n注意：你保留的这份比将被归档的那份更旧！\n" +
                           "若想保留较新的副本，请取消，改到另一张卡片上操作。";
            }
            return MessageBox.Show(this, message, "解决路径冲突",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        private static string DescribeWriteTime(LocalPluginUnit unit)
        {
            return unit != null && unit.LastWriteTimeUtc != null
                ? "（更新于 " + unit.LastWriteTimeUtc.Value.ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm") + "）"
                : string.Empty;
        }

        private async Task ToggleWorkshopPluginAsync(LocalPluginUnit unit)
        {
            int listScrollOffset = CurrentListScrollOffset();
            bool enabling = unit.IsDisabled;
            SetBusy(true, (enabling ? "正在启用工坊 Mod " : "正在禁用工坊 Mod ") +
                unit.DisplayName + "…");
            try
            {
                string gameDir = _gameDir;
                WorkshopToggleResult toggle = await SetWorkshopPluginEnabledAsync(
                    unit, enabling);
                if (toggle == null || !toggle.Succeeded)
                    throw new InvalidOperationException(toggle?.Error ?? "工坊启用状态修改失败。");

                if (toggle.Synchronization != null)
                    WriteWorkshopRefreshLog(gameDir, toggle.Synchronization, null);
                _workshopDiscovery = await DiscoverWorkshopItemsAsync();
                _localUnits = await ScanLocalPluginsAsync(_workshopDiscovery != null &&
                    _workshopDiscovery.Succeeded ? _workshopDiscovery.Items : null);
                RenderListAtScrollOffset(listScrollOffset);

                string syncWarning = toggle.Synchronization != null &&
                    (toggle.Synchronization.ErrorCount > 0 ||
                     !toggle.Synchronization.Synchronized)
                    ? "；同步未完全成功，请查看日志"
                    : string.Empty;
                SetStatus(unit.DisplayName + (enabling ? " 已启用" : " 已禁用") +
                    "（不影响 Steam 订阅和已下载文件）" + syncWarning);
            }
            catch (Exception ex)
            {
                _workshopToggleErrorPresenter(ex.Message);
                await RescanAfterMutationFailureAsync(listScrollOffset);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task<WorkshopToggleResult> SetWorkshopPluginEnabledAsync(
            LocalPluginUnit unit, bool enabled)
        {
            if (unit == null || unit.Source != LocalPluginSource.SteamWorkshop ||
                string.IsNullOrWhiteSpace(unit.WorkshopId))
                throw new InvalidOperationException("工坊插件信息无效，无法修改启用状态。");
            if (_isGameRunning())
                throw new InvalidOperationException("请先关闭游戏，再修改工坊启用状态。");
            if (_installer == null || !_installer.IsBepInExInstalled() ||
                !_installer.IsWorkshopBridgeInstalled())
                throw new InvalidOperationException("请先安装 BepInEx 与工坊 Mod 支持。");
            if (string.IsNullOrWhiteSpace(_gameDir))
                throw new InvalidOperationException("无法确定当前游戏目录。");

            string gameDir = _gameDir;
            return await Task.Run(() =>
                _workshopToggler(gameDir, unit.WorkshopId, enabled));
        }

        private async Task InstallBepInExAsync()
        {
            if (_busy) return;
            if (_installer.IsBepInExInstalled())
            {
                SetBusy(true, "正在安装工坊 Mod 支持…");
                try
                {
                    _installer.InstallWorkshopBridge();
                    SetStatus("工坊 Mod 支持安装完成。已有订阅不会被自动开启；" +
                        "新订阅的 Mod 下载完成后点“刷新”，或下次启动游戏时自动启用。");
                }
                catch (Exception ex)
                {
                    SetStatus("工坊 Mod 支持安装失败: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    SetBusy(false, null);
                    UpdateBepInExUi();
                }
                return;
            }

            SetBusy(true, "正在从内置包安装 BepInEx 与工坊 Mod 支持…");
            try
            {
                await _installer.InstallBepInExAsync(OnProgress);
                SetStatus("BepInEx 与工坊 Mod 支持安装完成。已有订阅不会被自动开启；" +
                    "新订阅的 Mod 下载完成后点“刷新”，或下次启动游戏时自动启用。");
            }
            catch (Exception ex)
            {
                SetStatus("完整前置安装失败: " + ex.Message);
                MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                SetBusy(false, null);
                UpdateBepInExUi();
            }
        }
        private async Task RescanAfterMutationFailureAsync(int listScrollOffset)
        {
            try
            {
                _workshopDiscovery = await DiscoverWorkshopItemsAsync();
                _localUnits = await ScanLocalPluginsAsync(_workshopDiscovery != null &&
                    _workshopDiscovery.Succeeded ? _workshopDiscovery.Items : null);
                RenderListAtScrollOffset(listScrollOffset);
            }
            catch
            {
                // Preserve the original mutation error. The next manual refresh retries discovery.
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_allowClose || !_busy) return;
            e.Cancel = true;
            _closePending = true;
            SetStatus("当前操作完成后将关闭管理器…");
        }

        // ═══════════════ 辅助 ═══════════════

        private void OnProgress(int percent, string source)
        {
            if (IsDisposed || Disposing || !IsHandleCreated || _allowClose) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<int, string>(OnProgress), percent, source);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }
            if (IsDisposed || Disposing || _allowClose) return;
            _progress.Visible = true;
            _progress.Value = Math.Max(0, Math.Min(100, percent));
            SetStatus("安装中 " + percent + "%（" + source + "）");
        }

        private void SetBusy(bool busy, string status)
        {
            // Disabling the focused card button makes WinForms repeatedly select the next
            // enabled button. Because every card is then disabled in sequence, focus walks to
            // the bottom and FlowLayoutPanel scrolls each newly focused button into view.
            // Clear the active child before disabling cards. Keeping focus on a card lets
            // WinForms walk it through every subsequently disabled button and scroll down.
            if (busy && _flow.ContainsFocus)
                ActiveControl = null;

            _busy = busy;
            _btnRefresh.Enabled = !busy;
            _btnInstallBep.Enabled = !busy;
            foreach (Control c in _flow.Controls) (c as ModCard)?.SetBusy(busy);
            if (!busy) _progress.Visible = false;
            if (status != null) SetStatus(status);
            if (!busy && _closePending && !_allowClose)
            {
                _allowClose = true;
                BeginInvoke(new Action(Close));
            }
        }

        private void SetStatus(string text)
        {
            _lblStatus.Text = text;
        }

        private void OpenContributionPage(string url)
        {
            try
            {
                Process.Start(string.IsNullOrEmpty(url) ? ContributionUrl : url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开投稿说明",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
