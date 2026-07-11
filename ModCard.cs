using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using StudentAgeModManager.Core;

namespace StudentAgeModManager
{
    /// <summary>单个 mod 的卡片控件。</summary>
    public class ModCard : Panel
    {
        private readonly Label _title = new Label();
        private readonly Label _desc = new Label();
        private readonly Label _status = new Label();
        private readonly Button _btnMain = new Button();     // 安装/更新/已是最新
        private readonly Button _btnToggle = new Button();   // 启用/禁用
        private readonly Button _btnUninstall = new Button();
        private readonly Button _btnHome = new Button();

        private ModStatus _boundStatus;
        private string _installedVersion;
        private bool _isBound;
        private bool _busy;

        public ModEntry Entry { get; private set; }
        public event Action<ModEntry> InstallClicked;
        public event Action<ModEntry> ToggleClicked;
        public event Action<ModEntry> UninstallClicked;

        public ModCard()
        {
            Size = new Size(560, 96);
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = Color.White;
            Margin = new Padding(6);

            _title.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            _title.Location = new Point(12, 8);
            _title.Size = new Size(258, 24);
            _title.AutoEllipsis = true;

            _status.Font = new Font("Microsoft YaHei UI", 9f);
            _status.Location = new Point(276, 8);
            _status.Size = new Size(271, 24);
            _status.TextAlign = ContentAlignment.MiddleRight;
            _status.AutoEllipsis = true;

            _desc.Font = new Font("Microsoft YaHei UI", 9f);
            _desc.ForeColor = Color.FromArgb(90, 90, 90);
            _desc.Location = new Point(13, 34);
            _desc.Size = new Size(534, 20);
            _desc.AutoEllipsis = true;

            SetupButton(_btnMain, 12);
            SetupButton(_btnToggle, 140);
            SetupButton(_btnUninstall, 232);
            SetupButton(_btnHome, 324);
            _btnToggle.Width = 84;
            _btnUninstall.Width = 84;
            _btnHome.Width = 84;
            _btnHome.Text = "主页";

            _btnMain.Click += (s, e) => InstallClicked?.Invoke(Entry);
            _btnToggle.Click += (s, e) => ToggleClicked?.Invoke(Entry);
            _btnUninstall.Click += (s, e) => UninstallClicked?.Invoke(Entry);
            _btnHome.Click += (s, e) => OpenHome();

            Controls.AddRange(new Control[] { _title, _status, _desc, _btnMain, _btnToggle, _btnUninstall, _btnHome });
        }

        private void SetupButton(Button b, int x)
        {
            b.Location = new Point(x, 60);
            b.Size = new Size(120, 27);
            b.Font = new Font("Microsoft YaHei UI", 9f);
            b.UseVisualStyleBackColor = true;
        }

        private void OpenHome()
        {
            if (Entry == null) return;
            try
            {
                string workshopId;
                if (WorkshopItem.TryGetId(Entry, out workshopId))
                    Process.Start(WorkshopItem.PageUrl(workshopId));
                else if (WorkshopItem.IsDeclared(Entry))
                    return;
                else if (!string.IsNullOrEmpty(Entry.repo))
                    Process.Start("https://github.com/" + Entry.repo);
            }
            catch { }
        }

        public void Bind(ModEntry entry, ModStatus status, string installedVersion)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            Entry = entry;
            _boundStatus = status;
            _installedVersion = installedVersion;
            _isBound = true;
            ApplyVisualState();
        }

        private void ApplyVisualState()
        {
            _title.Text = Entry.name ?? Entry.id;
            _desc.Text = Entry.description ?? "";

            // Reset every property that varies by state. SetBusy(false) calls this
            // method, so a card always returns to its exact last bound state rather
            // than blindly enabling buttons that should remain disabled or hidden.
            _btnMain.Location = new Point(12, 60);
            _btnToggle.Location = new Point(140, 60);
            _btnUninstall.Location = new Point(232, 60);
            _btnHome.Location = new Point(324, 60);

            _btnMain.Visible = true;
            _btnMain.Enabled = true;
            _btnToggle.Enabled = true;
            _btnToggle.Visible = true;
            _btnUninstall.Enabled = true;
            _btnUninstall.Visible = true;
            _btnHome.Enabled = true;
            _btnHome.Visible = !string.IsNullOrWhiteSpace(Entry.repo);

            if (WorkshopItem.IsDeclared(Entry))
            {
                string workshopId;
                bool validWorkshopId = WorkshopItem.TryGetId(Entry, out workshopId);
                bool hasLegacyInstall = _boundStatus != ModStatus.NotInstalled;
                _status.Text = validWorkshopId
                    ? (hasLegacyInstall
                        ? "Steam 管理 · 检测到旧版直装文件"
                        : "Steam 管理")
                    : "Steam 条目 · 工坊 ID 无效";
                _status.ForeColor = validWorkshopId
                    ? (hasLegacyInstall ? Color.DarkOrange : Color.RoyalBlue)
                    : Color.Firebrick;
                _btnMain.Text = validWorkshopId ? "订阅 / 查看工坊" : "工坊 ID 无效";
                _btnMain.Enabled = validWorkshopId;
                _btnToggle.Visible = false;
                _btnUninstall.Visible = hasLegacyInstall;
                _btnUninstall.Text = "清理旧安装";
                _btnUninstall.Location = new Point(140, 60);
                _btnHome.Visible = false;
                DisableHiddenButtons();
                ApplyBusyState();
                return;
            }

            switch (_boundStatus)
            {
                case ModStatus.NotInstalled:
                    _status.Text = "旧版直装 · 未安装";
                    _status.ForeColor = Color.Gray;
                    _btnMain.Text = "安装 " + Entry.version;
                    _btnMain.Enabled = true;
                    _btnToggle.Visible = false;
                    _btnUninstall.Visible = false;
                    break;
                case ModStatus.UpdateAvailable:
                    _status.Text = "旧版直装 · 已装 " + _installedVersion + " → 新版 " + Entry.version;
                    _status.ForeColor = Color.FromArgb(200, 120, 0);
                    _btnMain.Text = "更新到 " + Entry.version;
                    _btnMain.Enabled = true;
                    _btnToggle.Visible = true;
                    _btnToggle.Text = "禁用";
                    _btnUninstall.Visible = true;
                    _btnUninstall.Text = "卸载";
                    break;
                case ModStatus.UpToDate:
                    _status.Text = "旧版直装 · 已装 " + _installedVersion + "（最新）";
                    _status.ForeColor = Color.Green;
                    _btnMain.Text = "已是最新";
                    _btnMain.Enabled = false;
                    _btnToggle.Visible = true;
                    _btnToggle.Text = "禁用";
                    _btnUninstall.Visible = true;
                    _btnUninstall.Text = "卸载";
                    break;
                case ModStatus.InstalledUnknown:
                    _status.Text = "旧版直装 · 已安装（版本未知）";
                    _status.ForeColor = Color.FromArgb(200, 120, 0);
                    _btnMain.Text = "覆盖更新 " + Entry.version;
                    _btnMain.Enabled = true;
                    _btnToggle.Visible = false;
                    _btnUninstall.Visible = true;
                    _btnUninstall.Text = "卸载";
                    break;
                case ModStatus.Disabled:
                    _status.Text = "旧版直装 · 已禁用（" + _installedVersion + "）";
                    _status.ForeColor = Color.Gray;
                    _btnMain.Text = "更新 " + Entry.version;
                    _btnMain.Enabled = false; // 禁用状态先启用再更新，避免路径歧义
                    _btnToggle.Visible = true;
                    _btnToggle.Text = "启用";
                    _btnUninstall.Visible = true;
                    _btnUninstall.Text = "卸载";
                    break;
            }

            DisableHiddenButtons();
            ApplyBusyState();
        }

        private void DisableHiddenButtons()
        {
            if (!_btnToggle.Visible) _btnToggle.Enabled = false;
            if (!_btnUninstall.Visible) _btnUninstall.Enabled = false;
            if (!_btnHome.Visible) _btnHome.Enabled = false;
        }

        public void SetBusy(bool busy)
        {
            _busy = busy;
            if (_isBound)
                ApplyVisualState();
            else
                ApplyBusyState();
        }

        private void ApplyBusyState()
        {
            if (!_busy) return;

            _btnMain.Enabled = false;
            _btnToggle.Enabled = false;
            _btnUninstall.Enabled = false;
            _btnHome.Enabled = false;
        }
    }
}
