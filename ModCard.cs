using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StudentAgeModManager.Core;

namespace StudentAgeModManager
{
    /// <summary>工坊目录条目或本地已安装插件的卡片控件。</summary>
    public class ModCard : Panel
    {
        private static readonly Color SourceColor = Color.RoyalBlue;
        private static readonly Color PositiveColor = Color.FromArgb(45, 135, 70);
        private static readonly Color NegativeColor = Color.Firebrick;
        private static readonly Font TitleFont =
            new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        private static readonly Font CardFont = new Font("Microsoft YaHei UI", 9f);

        private readonly Label _title = new Label();
        private readonly Label _desc = new Label();
        private readonly Panel _statusPanel = new Panel();
        private readonly Label _status = new Label();
        private readonly Label _statusRegistration = new Label();
        private readonly Label _statusState = new Label();
        private readonly Button _btnMain = new Button();
        private readonly Button _btnToggle = new Button();

        private BoundKind _boundKind;
        private bool _busy;

        public ModEntry Entry { get; private set; }
        public LocalPluginUnit LocalUnit { get; private set; }
        public event Action<string> WorkshopPageClicked;
        public event Action<LocalPluginUnit> ToggleLocalClicked;

        public string StatusText => string.Join(" · ",
            new[] { _status, _statusRegistration, _statusState }
                .Where(label => !string.IsNullOrEmpty(label.Text))
                .Select(label => label.Text.TrimStart('·', ' ')));

        private enum BoundKind
        {
            None,
            WorkshopIndex,
            LocalPlugin,
        }

        public ModCard()
        {
            Size = new Size(560, 96);
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = Color.White;
            Margin = new Padding(6);

            _title.Font = TitleFont;
            _title.Location = new Point(12, 8);
            _title.Size = new Size(258, 24);
            _title.AutoEllipsis = true;

            _statusPanel.Location = new Point(276, 8);
            _statusPanel.Size = new Size(271, 24);
            _statusPanel.BackColor = Color.White;
            SetupStatusLabel(_status);
            SetupStatusLabel(_statusRegistration);
            SetupStatusLabel(_statusState);
            _statusPanel.Controls.AddRange(new Control[]
                { _status, _statusRegistration, _statusState });

            _desc.Font = CardFont;
            _desc.ForeColor = Color.FromArgb(90, 90, 90);
            _desc.Location = new Point(13, 34);
            _desc.Size = new Size(534, 20);
            _desc.AutoEllipsis = true;

            SetupButton(_btnMain, 12, 120);
            SetupButton(_btnToggle, 12, 84);

            _btnMain.Click += (s, e) =>
            {
                string workshopId;
                if (TryGetWorkshopId(out workshopId))
                    WorkshopPageClicked?.Invoke(workshopId);
            };
            _btnToggle.Click += (s, e) =>
            {
                if (LocalUnit != null) ToggleLocalClicked?.Invoke(LocalUnit);
            };

            Controls.AddRange(new Control[]
            {
                _title, _statusPanel, _desc, _btnMain, _btnToggle,
            });
        }

        private static void SetupStatusLabel(Label label)
        {
            label.Font = CardFont;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.AutoEllipsis = false;
            label.Visible = false;
        }

        private static void SetupButton(Button button, int x, int width)
        {
            button.Location = new Point(x, 60);
            button.Size = new Size(width, 27);
            button.Font = CardFont;
            button.UseVisualStyleBackColor = true;
        }

        public void Bind(ModEntry entry, LocalPluginUnit installedUnit = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (installedUnit != null &&
                installedUnit.Source != LocalPluginSource.SteamWorkshop)
                throw new ArgumentException("工坊索引只能与 Steam 工坊插件单元合并。",
                    nameof(installedUnit));
            Entry = entry;
            LocalUnit = installedUnit;
            _boundKind = BoundKind.WorkshopIndex;
            ApplyVisualState();
        }

        public void BindLocal(LocalPluginUnit unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            Entry = null;
            LocalUnit = unit;
            _boundKind = BoundKind.LocalPlugin;
            ApplyVisualState();
        }

        private void ApplyVisualState()
        {
            ResetButtons();
            if (_boundKind == BoundKind.WorkshopIndex)
                ApplyWorkshopIndexState();
            else if (_boundKind == BoundKind.LocalPlugin)
                ApplyLocalPluginState();
            ApplyBusyState();
        }

        private void ApplyWorkshopIndexState()
        {
            _title.Text = string.IsNullOrWhiteSpace(Entry.name) ? Entry.id : Entry.name;
            string workshopId;
            bool valid = WorkshopItem.TryGetId(Entry, out workshopId);
            bool connected = valid && LocalUnit != null;

            if (connected)
            {
                _desc.Text = "版本 " + (LocalUnit.DisplayVersion ?? "未知") + " · ID " +
                             workshopId + " · " + LocalUnit.RelativePath;
                SetStatus("Steam 工坊", SourceColor,
                    "已收录", PositiveColor, "已接入", PositiveColor);
            }
            else
            {
                _desc.Text = string.IsNullOrWhiteSpace(Entry.description)
                    ? WorkshopMetadataService.DefaultDescription
                    : Entry.description;
                if (valid)
                    SetStatus("Steam 工坊", SourceColor,
                        "已收录", PositiveColor, "未接入", NegativeColor);
                else
                    SetStatus("Steam 工坊", SourceColor,
                        "信息无效", NegativeColor, null, NegativeColor);
            }

            _btnMain.Visible = true;
            _btnMain.Enabled = valid;
            _btnMain.Text = valid ? "打开工坊页面" : "工坊信息无效";
        }

        private void ApplyLocalPluginState()
        {
            var unit = LocalUnit;
            _title.Text = string.IsNullOrWhiteSpace(unit.DisplayName)
                ? unit.UnitKey
                : unit.DisplayName;
            string pluginCount = unit.Plugins.Count + " 个插件";
            string dllCount = unit.DllCount == unit.Plugins.Count
                ? string.Empty
                : " / " + unit.DllCount + " 个 DLL";
            _desc.Text = "版本 " + (unit.DisplayVersion ?? "未知") + " · " + pluginCount +
                         dllCount + " · " + unit.RelativePath;

            if (unit.Source == LocalPluginSource.SteamWorkshop)
            {
                SetStatus("Steam 工坊", SourceColor,
                    "未收录", NegativeColor, "已接入", PositiveColor);
                _btnMain.Visible = true;
                _btnMain.Enabled = true;
                _btnMain.Text = "打开工坊页面";
                return;
            }

            if (unit.HasPathConflict)
            {
                SetStatus("本地", SourceColor,
                    "未收录", NegativeColor, "路径冲突", Color.DarkOrange);
                return;
            }

            SetStatus("本地", SourceColor,
                "未收录", NegativeColor,
                unit.IsDisabled ? "未启用" : "已启用",
                unit.IsDisabled ? NegativeColor : PositiveColor);
            _btnToggle.Visible = true;
            _btnToggle.Enabled = true;
            _btnToggle.Text = unit.IsDisabled ? "启用" : "禁用";
        }

        private void SetStatus(string source, Color sourceColor,
            string registration, Color registrationColor, string state, Color stateColor)
        {
            SetStatusLabel(_status, source, sourceColor);
            SetStatusLabel(_statusRegistration, registration, registrationColor);
            SetStatusLabel(_statusState, state, stateColor);

            Label[] labels = { _status, _statusRegistration, _statusState };
            int right = _statusPanel.ClientSize.Width;
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                Label label = labels[i];
                if (string.IsNullOrEmpty(label.Text)) continue;
                string displayText = i == 0 ? label.Text : "· " + label.Text;
                int width = TextRenderer.MeasureText(displayText, label.Font,
                    Size.Empty, TextFormatFlags.NoPadding).Width + 5;
                right -= width;
                label.Bounds = new Rectangle(Math.Max(0, right), 0,
                    Math.Min(width, _statusPanel.ClientSize.Width), _statusPanel.ClientSize.Height);
                label.Text = displayText;
            }
        }

        private static void SetStatusLabel(Label label, string text, Color color)
        {
            label.Text = text ?? string.Empty;
            label.ForeColor = color;
            label.Visible = !string.IsNullOrEmpty(text);
        }

        private void ResetButtons()
        {
            _btnMain.Visible = false;
            _btnMain.Enabled = false;
            _btnToggle.Visible = false;
            _btnToggle.Enabled = false;
        }

        private bool TryGetWorkshopId(out string workshopId)
        {
            workshopId = LocalUnit != null &&
                LocalUnit.Source == LocalPluginSource.SteamWorkshop
                ? LocalUnit.WorkshopId
                : null;
            return !string.IsNullOrEmpty(workshopId) ||
                   (Entry != null && WorkshopItem.TryGetId(Entry, out workshopId));
        }

        public void SetBusy(bool busy)
        {
            _busy = busy;
            if (_boundKind != BoundKind.None) ApplyVisualState();
        }

        private void ApplyBusyState()
        {
            if (!_busy) return;
            _btnMain.Enabled = false;
            _btnToggle.Enabled = false;
        }
    }
}
