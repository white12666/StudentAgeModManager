using System;
using System.Drawing;
using System.Windows.Forms;

namespace StudentAgeModManager
{
    /// <summary>
    /// FlowLayoutPanel 的滚轮可靠版本。鼠标悬停在卡片或按钮上时也把滚轮焦点交给列表，
    /// 并按系统滚轮行数移动 AutoScrollPosition。
    /// </summary>
    public sealed class WheelFlowLayoutPanel : FlowLayoutPanel
    {
        public WheelFlowLayoutPanel()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            AttachMouseFocus(e.Control);
        }

        protected override void OnControlRemoved(ControlEventArgs e)
        {
            DetachMouseFocus(e.Control);
            base.OnControlRemoved(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!AutoScroll)
            {
                base.OnMouseWheel(e);
                return;
            }

            int maxOffset = Math.Max(0, DisplayRectangle.Height - ClientSize.Height);
            if (maxOffset == 0)
            {
                base.OnMouseWheel(e);
                return;
            }

            int wheelSteps = Math.Max(1,
                Math.Abs(e.Delta) / Math.Max(1, SystemInformation.MouseWheelScrollDelta));
            int configuredLines = SystemInformation.MouseWheelScrollLines;
            int pixelsPerLine = Math.Max(24, Font.Height + 8);
            int distance = configuredLines < 0
                ? ClientSize.Height
                : Math.Max(1, configuredLines) * pixelsPerLine;
            int currentOffset = -AutoScrollPosition.Y;
            int direction = e.Delta > 0 ? -1 : 1;
            int targetOffset = Math.Max(0,
                Math.Min(maxOffset, currentOffset + direction * distance * wheelSteps));
            AutoScrollPosition = new Point(-AutoScrollPosition.X, targetOffset);
        }

        private void AttachMouseFocus(Control control)
        {
            if (control == null) return;
            control.MouseEnter += ChildMouseEnter;
            control.ControlAdded += ChildControlAdded;
            control.ControlRemoved += ChildControlRemoved;
            foreach (Control child in control.Controls) AttachMouseFocus(child);
        }

        private void DetachMouseFocus(Control control)
        {
            if (control == null) return;
            control.MouseEnter -= ChildMouseEnter;
            control.ControlAdded -= ChildControlAdded;
            control.ControlRemoved -= ChildControlRemoved;
            foreach (Control child in control.Controls) DetachMouseFocus(child);
        }

        private void ChildMouseEnter(object sender, EventArgs e)
        {
            Focus();
        }

        private void ChildControlAdded(object sender, ControlEventArgs e)
        {
            AttachMouseFocus(e.Control);
        }

        private void ChildControlRemoved(object sender, ControlEventArgs e)
        {
            DetachMouseFocus(e.Control);
        }
    }
}
