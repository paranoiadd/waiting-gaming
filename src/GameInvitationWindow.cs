using System;
using System.Drawing;
using System.Windows.Forms;

namespace WaitingGaming
{
    internal sealed class GameInvitationWindow : Form
    {
        internal event EventHandler StartRequested;
        internal event EventHandler Declined;

        internal GameInvitationWindow()
        {
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(290, 112);
            Font = new Font("Microsoft YaHei UI", 9);
            BackColor = Color.FromArgb(248, 245, 238);
            ForeColor = Color.FromArgb(39, 45, 38);
            Text = "等待小游戏";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;

            Controls.Add(new Label
            {
                Text = "是否开始 2048？",
                Bounds = new Rectangle(16, 12, 258, 28),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font, FontStyle.Bold)
            });
            var start = AddButton("开始游戏", new Rectangle(16, 57, 122, 34));
            start.BackColor = Color.FromArgb(52, 100, 72);
            start.ForeColor = Color.White;
            start.FlatAppearance.BorderColor = start.BackColor;
            start.Click += delegate { Raise(StartRequested); };
            var dismiss = AddButton("本次不玩", new Rectangle(152, 57, 122, 34));
            dismiss.Click += delegate { Raise(Declined); };
            AcceptButton = start;
            CancelButton = dismiss;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        internal bool FollowOwner(WindowBounds ownerBounds)
        {
            Rectangle owner = new Rectangle(ownerBounds.Left, ownerBounds.Top, ownerBounds.Width, ownerBounds.Height);
            Rectangle visible = Rectangle.Intersect(owner, Screen.FromRectangle(owner).WorkingArea);
            if (visible.Width < Width || visible.Height < Height)
            {
                Hide();
                return false;
            }
            const int margin = 12;
            Location = new Point(Math.Max(visible.Left, visible.Right - Width - margin),
                Math.Min(visible.Top + margin, Math.Max(visible.Top, visible.Bottom - Height)));
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs eventArgs)
        {
            if (eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                Raise(Declined);
            }
            base.OnFormClosing(eventArgs);
        }

        private Button AddButton(string text, Rectangle bounds)
        {
            var button = new Button
            {
                Text = text,
                Bounds = bounds,
                FlatStyle = FlatStyle.Flat,
                BackColor = BackColor,
                ForeColor = ForeColor,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(179, 181, 162);
            Controls.Add(button);
            return button;
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }

    internal sealed class WindowReference : IWin32Window
    {
        internal WindowReference(long handle)
        {
            Handle = new IntPtr(handle);
        }

        public IntPtr Handle { get; private set; }
    }
}
