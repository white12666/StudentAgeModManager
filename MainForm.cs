using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using StudentAgeModManager.Core;

namespace StudentAgeModManager
{
    public class MainForm : Form
    {
        private readonly Label _lblGameDir = new Label();
        private readonly Label _lblBepInEx = new Label();
        private readonly Button _btnRefresh = new Button();
        private readonly Button _btnInstallBep = new Button();
        private readonly ComboBox _cmbMirror = new ComboBox();
        private readonly FlowLayoutPanel _flow = new FlowLayoutPanel();
        private readonly Label _lblStatus = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Panel _banner = new Panel();
        private readonly Label _bannerText = new Label();
        private readonly Panel _workshopGuide = new Panel();
        private readonly Label _workshopGuideText = new Label();

        private string _gameDir;
        private LocalState _state;
        private ModInstaller _installer;
        private readonly Downloader _downloader = new Downloader();
        private IndexClient _indexClient;
        private ModIndex _index;
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
            _workshopGuide.Size = new Size(620, 64);
            _workshopGuide.BackColor = Color.FromArgb(231, 243, 255);
            _workshopGuideText.Location = new Point(14, 5);
            _workshopGuideText.Size = new Size(592, 54);
            _workshopGuideText.ForeColor = Color.FromArgb(35, 78, 121);
            _workshopGuideText.Text =
                "工坊 DLL 前置：先安装 BepInEx + Bridge；现有订阅只建基线，不会自动开启。\r\n" +
                "之后新订阅的合法 DLL：Steam 下载完成后的下一次游戏启动自动启用并直接加载。\r\n" +
                "Steam 管理订阅/更新/卸载；可在游戏“本地”页关闭，关闭后 Bridge 不会再次开启。";
            _workshopGuide.Controls.Add(_workshopGuideText);

            // ── BepInEx 缺失提示条 ──
            _banner.Location = new Point(0, 130);
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
            _flow.Location = new Point(14, 130);
            _flow.Size = new Size(592, 418);
            _flow.AutoScroll = true;
            _flow.FlowDirection = FlowDirection.TopDown;
            _flow.WrapContents = false;

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
                _banner, _flow, _lblStatus, _progress,
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
            _indexClient = new IndexClient(_downloader);

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
            _flow.Location = new Point(14, showBanner ? 168 : 130);
            _flow.Height = 570 - _flow.Top;
        }

        // ═══════════════ 索引拉取与列表渲染 ═══════════════

        private async Task RefreshIndexAsync()
        {
            if (_busy) return;
            SetBusy(true, "正在获取 mod 列表...");
            try
            {
                _index = await _indexClient.FetchAsync();
                _state.Load();
                RenderList();
                CheckSelfUpdate();
                SetStatus("列表已更新（" + _index.mods.Count + " 个 mod，索引更新于 " + (_index.updatedAt ?? "?") + "）");
            }
            catch (Exception ex)
            {
                SetStatus("获取列表失败: " + ex.Message);
                MessageBox.Show(this,
                    "无法获取 mod 列表，请检查网络连接。\n\n" + ex.Message,
                    "网络错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void RenderList()
        {
            _flow.SuspendLayout();
            _flow.Controls.Clear();
            foreach (var mod in _index.mods)
            {
                var card = new ModCard();
                var rec = _state.Get(mod.id);
                card.Bind(mod, _state.GetStatus(mod), rec != null ? rec.version : null);
                card.InstallClicked += async m => await InstallModAsync(m);
                card.ToggleClicked += m => ToggleMod(m);
                card.UninstallClicked += m => UninstallMod(m);
                card.SetBusy(_busy);
                _flow.Controls.Add(card);
            }
            _flow.ResumeLayout();
        }

        private void RebindCards()
        {
            foreach (Control c in _flow.Controls)
            {
                var card = c as ModCard;
                if (card == null || card.Entry == null) continue;
                var rec = _state.Get(card.Entry.id);
                card.Bind(card.Entry, _state.GetStatus(card.Entry), rec != null ? rec.version : null);
            }
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

        private async Task InstallModAsync(ModEntry mod)
        {
            if (_busy) return;
            if (WorkshopItem.IsDeclared(mod))
            {
                string workshopId;
                if (!WorkshopItem.TryGetId(mod, out workshopId))
                {
                    MessageBox.Show(this, "索引中的创意工坊 ID 无效，已拒绝回退为直接 DLL 安装。",
                        "工坊条目无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    Process.Start(WorkshopItem.PageUrl(workshopId));
                    SetStatus("请在 Steam 订阅；下载完成后的下一次游戏启动会自动启用合法 DLL 项目。" +
                        "现有基线项目仍需在游戏“本地”页手动开启。");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "无法打开创意工坊", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }
            if (!_installer.IsBepInExInstalled())
            {
                MessageBox.Show(this, "请先安装 BepInEx 前置（顶部黄条一键安装）。",
                    "缺少前置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SetBusy(true, "正在下载 " + mod.name + " ...");
            try
            {
                await _installer.InstallAsync(mod, OnProgress);
                SetStatus(mod.name + " 安装完成（" + mod.version + "）。改动需重启游戏生效。");
            }
            catch (Exception ex)
            {
                SetStatus("安装失败: " + ex.Message);
                ShowErrorWithManualLink(mod, ex);
            }
            finally
            {
                SetBusy(false, null);
                RebindCards();
            }
        }

        private void ToggleMod(ModEntry mod)
        {
            if (_busy) return;
            try
            {
                var rec = _state.Get(mod.id);
                if (rec != null && rec.enabled)
                {
                    _installer.Disable(mod);
                    SetStatus(mod.name + " 已禁用（重启游戏生效）");
                }
                else
                {
                    _installer.Enable(mod);
                    SetStatus(mod.name + " 已启用（重启游戏生效）");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RebindCards();
        }

        private void UninstallMod(ModEntry mod)
        {
            if (_busy) return;
            bool workshopItem = WorkshopItem.IsDeclared(mod);
            string prompt = workshopItem
                ? "确定清理 " + mod.name + " 的旧版直装文件吗？\n\n" +
                  "此操作不会取消 Steam 订阅，也不会删除 Steam 工坊目录。"
                : "确定卸载 " + mod.name + " 吗？";
            var r = MessageBox.Show(this, prompt, workshopItem ? "确认清理旧安装" : "确认卸载",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            try
            {
                _installer.Uninstall(mod);
                SetStatus(workshopItem
                    ? mod.name + " 的旧版直装文件已清理；Steam 订阅未受影响。"
                    : mod.name + " 已卸载");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, workshopItem ? "清理失败" : "卸载失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            RebindCards();
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

        private void ShowErrorWithManualLink(ModEntry mod, Exception ex)
        {
            var r = MessageBox.Show(this,
                "下载或安装失败：\n" + ex.Message +
                "\n\n是否打开该 mod 的发布页手动下载？",
                "安装失败", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes && !string.IsNullOrEmpty(mod.repo))
            {
                try { Process.Start("https://github.com/" + mod.repo + "/releases"); } catch { }
            }
        }
    }
}
