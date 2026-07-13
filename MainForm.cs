using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using StudentAgeModManager.Core;

namespace StudentAgeModManager
{
    public class MainForm : Form
    {
        private const string ContributionUrl =
            "https://github.com/white12666/StudentAgeModManager/blob/main/CONTRIBUTING.md";
        private static readonly Font SectionFont =
            new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        private static readonly Font SubmissionFont =
            new Font("Microsoft YaHei UI", 8f);

        private readonly Label _lblGameDir = new Label();
        private readonly Label _lblBepInEx = new Label();
        private readonly Button _btnRefresh = new Button();
        private readonly Button _btnInstallBep = new Button();
        private readonly ComboBox _cmbMirror = new ComboBox();
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
        private IndexClient _indexClient;
        private ModIndex _index;
        private List<LocalPluginUnit> _localUnits = new List<LocalPluginUnit>();
        private int _localPluginCount;
        private bool _busy;

        public MainForm()
        {
            Text = "StudentAge Mod 管理器 v" + CurrentVersion();
            ClientSize = new Size(620, 620);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(245, 245, 248);

            BuildLayout();
            Shown += async (s, e) => await InitializeAsync();
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
            _lblBepInEx.Size = new Size(286, 22);
            _lblBepInEx.AutoEllipsis = true;

            _btnRefresh.Text = "刷新";
            _btnRefresh.Location = new Point(440, 30);
            _btnRefresh.Size = new Size(75, 26);
            _btnRefresh.Click += async (s, e) => await RefreshIndexAsync();

            _cmbMirror.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMirror.Items.AddRange(new object[] { "自动(直连+镜像)", "强制镜像", "强制直连" });
            _cmbMirror.SelectedIndex = 0;
            _cmbMirror.Location = new Point(310, 31);
            _cmbMirror.Size = new Size(120, 24);
            _cmbMirror.SelectedIndexChanged += (s, e) =>
                _downloader.Mode = (MirrorMode)_cmbMirror.SelectedIndex;

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
                "点击“一键安装完整前置”。中央索引只是推荐目录，不是加载白名单；\r\n" +
                "任何合法工坊 DLL 均可接入，新订阅在下载完成后的下一次启动自动启用。";

            _workshopManageTitle.Location = new Point(14, 68);
            _workshopManageTitle.Size = new Size(592, 19);
            _workshopManageTitle.Font = new Font(Font, FontStyle.Bold);
            _workshopManageTitle.ForeColor = Color.FromArgb(25, 64, 104);
            _workshopManageTitle.Text = "如何管理 Mod";
            _workshopManageText.Location = new Point(14, 89);
            _workshopManageText.Size = new Size(592, 36);
            _workshopManageText.ForeColor = Color.FromArgb(35, 78, 121);
            _workshopManageText.Text =
                "工坊订阅/取消在 Steam，开关在游戏“本地”页；未收录但已接入的工坊也会显示。\r\n" +
                "手动 DLL 显示为“本地 · 未收录”，可在下方开关；所有改动重启后生效。";

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
            _bannerText.Text = "未检测到 BepInEx 前置与创意工坊 DLL 支持。";
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
                "“收录”只表示进入 Git 推荐目录，可自定义显示名称与简介。欢迎 Mod 作者在 GitHub 提交收录。";
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
                _lblGameDir, _lblBepInEx, _btnRefresh, _cmbMirror, _workshopGuide,
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
            _installer = new ModInstaller(_state, _downloader);
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
                _lblBepInEx.Text = "✘ 未安装 BepInEx + 工坊 DLL 支持";
                _lblBepInEx.ForeColor = Color.Firebrick;
                _bannerText.Text = "未检测到 BepInEx 前置与创意工坊 DLL 支持。";
                _btnInstallBep.Text = "一键安装完整前置";
            }
            else if (!bridgeCurrent)
            {
                _lblBepInEx.Text = "⚠ 工坊 DLL 支持缺失或需更新";
                _lblBepInEx.ForeColor = Color.DarkOrange;
                _bannerText.Text = "新订阅的合法 DLL：下载完成后的下次启动自动启用。";
                _btnInstallBep.Text = "安装工坊 DLL 支持";
            }
            else
            {
                _lblBepInEx.Text = "✔ BepInEx + 工坊 DLL 支持已安装";
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

        private async Task RefreshIndexAsync()
        {
            if (_busy) return;
            SetBusy(true, "正在获取工坊列表并扫描本地插件...");
            Task<List<LocalPluginUnit>> localScan = ScanLocalPluginsAsync();
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

                _localUnits = await localScan;
                RenderList();
                if (indexError == null)
                {
                    CheckSelfUpdate();
                    SetStatus("列表已更新（" + _index.mods.Count + " 个工坊条目，" +
                        _localPluginCount + " 个已安装插件单元，索引更新于 " +
                        (_index.updatedAt ?? "?") + "）");
                }
                else
                {
                    string previousNote = keptPreviousIndex ? "保留上次工坊目录；" : string.Empty;
                    SetStatus("工坊列表获取失败；" + previousNote + "已显示 " +
                        _localPluginCount + " 个本地插件单元。" + indexError.Message);
                    MessageBox.Show(this,
                        "无法获取工坊列表，" +
                        (keptPreviousIndex ? "当前保留上次目录；" : string.Empty) +
                        "本地插件仍可管理。\n\n" + indexError.Message,
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

        private Task<List<LocalPluginUnit>> ScanLocalPluginsAsync()
        {
            if (_pluginScanner == null || string.IsNullOrEmpty(_gameDir))
                return Task.FromResult(new List<LocalPluginUnit>());
            string gameDir = _gameDir;
            return Task.Run(() => _pluginScanner.Scan(gameDir));
        }

        private void RenderList()
        {
            _flow.SuspendLayout();
            ClearRenderedControls();

            var indexedMods = _index?.mods ?? new List<ModEntry>();
            var localUnits = _localUnits ?? new List<LocalPluginUnit>();
            _localPluginCount = localUnits.Count;
            var consumedWorkshopUnits = new HashSet<LocalPluginUnit>();

            if (indexedMods.Count > 0)
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
                    card.SetBusy(_busy);
                    _flow.Controls.Add(card);
                }
            }

            var remainingLocalUnits = localUnits
                .Where(unit => !consumedWorkshopUnits.Contains(unit)).ToList();
            if (remainingLocalUnits.Count > 0)
            {
                _flow.Controls.Add(CreateSectionLabel("本地已安装插件"));
                foreach (var unit in remainingLocalUnits)
                {
                    var card = new ModCard();
                    card.BindLocal(unit);
                    card.WorkshopPageClicked += OpenWorkshopPage;
                    card.ToggleLocalClicked += ToggleLocalPlugin;
                    card.SetBusy(_busy);
                    _flow.Controls.Add(card);
                }
            }

            if (indexedMods.Count == 0 && remainingLocalUnits.Count == 0)
                _flow.Controls.Add(CreateSectionLabel("未发现工坊目录条目或本地 BepInEx 插件。"));
            _flow.ResumeLayout();
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
                    try { Process.Start(_index.manager.downloadUrl); } catch { }
                }
            }
        }

        // ═══════════════ 操作 ═══════════════

        private void OpenWorkshopPage(string workshopId)
        {
            if (_busy) return;
            try
            {
                Process.Start(WorkshopItem.PageUrl(workshopId));
                SetStatus("已打开工坊页面；订阅或取消订阅请在 Steam 中操作。" +
                    "合法 DLL 项目会在下载完成后的下一次游戏启动接入。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开创意工坊", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private async void ToggleLocalPlugin(LocalPluginUnit unit)
        {
            if (_busy) return;
            bool enabling = unit.IsDisabled;
            SetBusy(true, (enabling ? "正在启用 " : "正在禁用 ") + unit.DisplayName + " ...");
            try
            {
                if (enabling)
                    _localPluginManager.Enable(unit);
                else
                    _localPluginManager.Disable(unit);

                _localUnits = await ScanLocalPluginsAsync();
                RenderList();
                SetStatus(unit.DisplayName + (enabling ? " 已启用" : " 已禁用") +
                    "（重启游戏生效）");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async Task InstallBepInExAsync()
        {
            if (_busy) return;
            if (_installer.IsBepInExInstalled())
            {
                SetBusy(true, "正在安装创意工坊 DLL 支持...");
                try
                {
                    _installer.InstallWorkshopBridge();
                    SetStatus("工坊 DLL 支持安装完成。现有订阅不会自动开启；之后新订阅的合法 DLL 会在" +
                        "下载完成后的下一次游戏启动自动启用。");
                }
                catch (Exception ex)
                {
                    SetStatus("创意工坊 DLL 支持安装失败: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    SetBusy(false, null);
                    UpdateBepInExUi();
                }
                return;
            }

            if (_index == null || _index.bepinex == null || string.IsNullOrEmpty(_index.bepinex.downloadUrl))
            {
                MessageBox.Show(this, "索引中没有 BepInEx 下载信息，请先点击刷新。",
                    "无法安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SetBusy(true, "正在下载 BepInEx + 创意工坊 DLL 支持...");
            try
            {
                await _installer.InstallBepInExAsync(_index.bepinex, OnProgress);
                SetStatus("BepInEx + 工坊 DLL 支持安装完成；现有订阅只建基线，之后新订阅的合法 DLL " +
                    "会在下载完成后的下一次游戏启动自动启用。");
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

        // ═══════════════ 辅助 ═══════════════

        private void OnProgress(int percent, string source)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int, string>(OnProgress), percent, source); return; }
            _progress.Visible = true;
            _progress.Value = Math.Max(0, Math.Min(100, percent));
            SetStatus("下载中 " + percent + "%（源: " + source + "）");
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _btnRefresh.Enabled = !busy;
            _btnInstallBep.Enabled = !busy;
            foreach (Control c in _flow.Controls) (c as ModCard)?.SetBusy(busy);
            if (!busy) _progress.Visible = false;
            if (status != null) SetStatus(status);
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
