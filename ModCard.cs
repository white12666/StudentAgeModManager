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
            _title.AutoSize = true;

            _status.Font = new Font("Microsoft YaHei UI", 9f);
            _status.AutoSize = true;
            _status.Location = new Point(400, 11);

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
            Entry = entry;
            _title.Text = entry.name ?? entry.id;
            _desc.Text = entry.description ?? "";
            _btnToggle.Enabled = true;
            _btnToggle.Visible = true;
            _btnUninstall.Enabled = true;
            _btnUninstall.Visible = true;
            _btnHome.Visible = true;

            if (WorkshopItem.IsDeclared(entry))
            {
                string workshopId;
                bool validWorkshopId = WorkshopItem.TryGetId(entry, out workshopId);
                bool hasLegacyInstall = status != ModStatus.NotInstalled;
                _status.Text = validWorkshopId
                    ? (hasLegacyInstall
                        ? "由 Steam 管理（检测到旧版直装文件）"
                        : "由 Steam 创意工坊管理")
                    : "索引中的创意工坊 ID 无效";
                _status.ForeColor = validWorkshopId
                    ? (hasLegacyInstall ? Color.DarkOrange : Color.RoyalBlue)
                    : Color.Firebrick;
                _btnMain.Text = validWorkshopId ? "订阅 / 查看工坊" : "工坊条目不可用";
                _btnMain.Enabled = validWorkshopId;
                _btnToggle.Visible = false;
                _btnUninstall.Visible = hasLegacyInstall;
                _btnUninstall.Text = "清理旧安装";
                _btnHome.Visible = false;
                return;
            }

            switch (status)
            {
                case ModStatus.NotInstalled:
                    _status.Text = "未安装";
                    _status.ForeColor = Color.Gray;
                    _btnMain.Text = "安装 " + entry.version;
                    _btnMain.Enabled = true;
                    _btnToggle.Visible = false;
                    _btnUninstall.Visible = false;
                    break;
                case ModStatus.UpdateAvailable:
                    _status.Text = "已装 " + installedVersion + " → 新版 " + entry.version;
                    _status.ForeColor = Color.FromArgb(200, 120, 0);
                    _btnMain.Text = "更新到 " + entry.version;
                    _btnMain.Enabled = true;
                    _btnToggle.Visible = true;
                    _btnToggle.Text = "禁用";
                    _btnUninstall.Visible = true;
                    _btnUninstall.Text = "卸载";
                    break;
                case ModStatus.UpToDate:
                    _status.Text = "已装 " + installedVersion + "（最新）";
                    _status.ForeColor = Color.Green;
                    _btnMain.Text = "已是最新";
                    _btnMain.Enabled = false;
                    _btnToggle.Visible = true;
                    _btnToggle.Text = "禁用";
                    _btnUninstall.Visible = true;
                    _btnUninstall.Text = "卸载";
                    break;
                case ModStatus.InstalledUnknown:
                    _status.Text = "已安装（版本未知）";
                    _status.ForeColor = Color.FromArgb(200, 120, 0);
                    _btnMain.Text = "覆盖更新 " + entry.version;
                    _btnMain.Enabled = true;
                    _btnToggle.Visible = false;
                    _btnUninstall.Visible = true;
                    _btnUninstall.Text = "卸载";
                    break;
                case ModStatus.Disabled:
                    _status.Text = "已禁用（" + installedVersion + "）";
                    _status.ForeColor = Color.Gray;
                    _btnMain.Text = "更新 " + entry.version;
                    _btnMain.Enabled = false; // 禁用状态先启用再更新，避免路径歧义
                    _btnToggle.Visible = true;
                    _btnToggle.Text = "启用";
                    _btnUninstall.Visible = true;
                    _btnUninstall.Text = "卸载";
                    break;
            }
        }

        public void SetBusy(bool busy)
        {
            // busy 时全部禁用；恢复由 MainForm 重新 Bind 完成
            if (busy)
            {
                _btnMain.Enabled = false;
                _btnToggle.Enabled = false;
                _btnUninstall.Enabled = false;
            }
        }
    }
}
