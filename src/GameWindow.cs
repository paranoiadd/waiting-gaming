using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WaitingGaming
{
    internal sealed class GameWindow : Form
    {
        private static readonly Color Paper = Color.FromArgb(248, 245, 238);
        private static readonly Color Ink = Color.FromArgb(39, 45, 38);
        private static readonly Color Muted = Color.FromArgb(93, 99, 87);
        private static readonly Color Green = Color.FromArgb(52, 100, 72);
        private static readonly Color ScoreBackground = Color.FromArgb(235, 234, 223);
        private static readonly Font HeadingFont = new Font("Segoe UI", 28, FontStyle.Bold);
        private static readonly Font ScoreFont = new Font("Segoe UI", 14, FontStyle.Bold);
        private static readonly Font TextFont = new Font("Microsoft YaHei UI", 9);

        private readonly GameBoard game;
        private readonly bool showWithoutActivation;
        private readonly Label status;
        private readonly Label score;
        private readonly Label best;
        private readonly Label gameHint;
        private readonly BoardView board;

        public GameWindow(GameBoard game, bool showWithoutActivation)
        {
            this.game = game;
            this.showWithoutActivation = showWithoutActivation;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(330, 516);
            Font = TextFont;
            BackColor = Paper;
            ForeColor = Ink;
            Text = "等待小游戏 · 2048";
            StartPosition = showWithoutActivation ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;

            Controls.Add(MakeLabel("2048", HeadingFont, Ink, new Rectangle(18, 10, 290, 48)));
            status = MakeLabel("", TextFont, Muted, new Rectangle(20, 63, 290, 24));
            Controls.Add(status);

            score = AddScoreCard("分数", 20);
            best = AddScoreCard("最高分", 169);
            board = new BoardView(game) { Bounds = new Rectangle(20, 166, 290, 290) };
            Controls.Add(board);

            gameHint = MakeLabel("", TextFont, Muted, new Rectangle(20, 470, 193, 30));
            Controls.Add(gameHint);
            var reset = new Button
            {
                Text = "重新开局",
                Bounds = new Rectangle(220, 468, 90, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Paper,
                ForeColor = Ink,
                Cursor = Cursors.Hand
            };
            reset.FlatAppearance.BorderColor = Color.FromArgb(179, 181, 162);
            reset.FlatAppearance.MouseOverBackColor = ScoreBackground;
            reset.Click += delegate
            {
                game.Reset();
                RenderGame();
            };
            Controls.Add(reset);
            RenderGame();
            ResumeLayout(false);
        }

        protected override bool ShowWithoutActivation { get { return showWithoutActivation; } }

        public void PlaceAtCorner()
        {
            IntPtr foreground = GetForegroundWindow();
            Screen screen = foreground == IntPtr.Zero ? Screen.FromPoint(Cursor.Position) : Screen.FromHandle(foreground);
            Rectangle area = screen.WorkingArea;
            PerformLayout();
            const int margin = 16;
            Location = new Point(Math.Max(area.Left, area.Right - Width - margin),
                                 Math.Max(area.Top, area.Bottom - Height - margin));
        }

        public void SetStatus(string message, bool active)
        {
            status.Text = message;
            status.ForeColor = active ? Green : Muted;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            Direction direction;
            switch (keyData)
            {
                case Keys.Left: direction = Direction.Left; break;
                case Keys.Right: direction = Direction.Right; break;
                case Keys.Up: direction = Direction.Up; break;
                case Keys.Down: direction = Direction.Down; break;
                default: return base.ProcessCmdKey(ref message, keyData);
            }

            if (game.Move(direction))
            {
                RenderGame();
            }
            return true;
        }

        private Label AddScoreCard(string caption, int left)
        {
            var card = new Panel
            {
                Bounds = new Rectangle(left, 96, 141, 58),
                BackColor = ScoreBackground
            };
            card.Controls.Add(MakeLabel(caption, TextFont, Muted, new Rectangle(10, 4, 121, 20)));
            var value = MakeLabel("", ScoreFont, Ink, new Rectangle(10, 23, 121, 31));
            value.AccessibleName = caption;
            card.Controls.Add(value);
            Controls.Add(card);
            return value;
        }

        private static Label MakeLabel(string text, Font font, Color color, Rectangle bounds)
        {
            return new Label
            {
                Text = text,
                Bounds = bounds,
                Font = font,
                ForeColor = color,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private void RenderGame()
        {
            score.Text = game.Score.ToString(CultureInfo.InvariantCulture);
            best.Text = game.BestScore.ToString(CultureInfo.InvariantCulture);
            if (game.SaveFailed)
            {
                gameHint.Text = "本地存档写入失败";
            }
            else if (game.IsGameOver)
            {
                gameHint.Text = "游戏结束";
            }
            else
            {
                gameHint.Text = game.HasWon ? "已达成 2048，可继续挑战" : "方向键移动方块";
            }
            board.RefreshBoard();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }

    internal sealed class BoardView : Control
    {
        private static readonly Color BoardColor = Color.FromArgb(177, 178, 151);
        private static readonly Color TileInk = Color.FromArgb(43, 49, 38);
        private static readonly Color[] TileColors =
        {
            Color.FromArgb(199, 198, 173), Color.FromArgb(246, 238, 219),
            Color.FromArgb(229, 229, 196), Color.FromArgb(208, 218, 177),
            Color.FromArgb(183, 200, 151), Color.FromArgb(232, 190, 125),
            Color.FromArgb(218, 164, 96), Color.FromArgb(144, 82, 54),
            Color.FromArgb(122, 72, 54), Color.FromArgb(89, 104, 67),
            Color.FromArgb(68, 88, 60), Color.FromArgb(44, 75, 56)
        };

        private readonly GameBoard game;

        public BoardView(GameBoard game)
        {
            this.game = game;
            DoubleBuffered = true;
            AccessibleName = "2048 棋盘";
            AccessibleRole = AccessibleRole.Graphic;
            TabStop = false;
        }

        public void RefreshBoard()
        {
            var rows = new string[GameBoard.Size];
            var cells = new string[GameBoard.Size];
            for (int row = 0; row < GameBoard.Size; row++)
            {
                for (int column = 0; column < GameBoard.Size; column++)
                {
                    int value = game[row, column];
                    cells[column] = value == 0 ? "空" : value.ToString(CultureInfo.InvariantCulture);
                }
                rows[row] = string.Join("、", cells);
            }
            AccessibleDescription = string.Join("。", rows);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            Graphics graphics = args.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float gap = Width * 0.0276f;
            float cell = (Width - gap * (GameBoard.Size + 1)) / GameBoard.Size;
            FillRounded(graphics, BoardColor, ClientRectangle, gap * 1.2f);

            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int row = 0; row < GameBoard.Size; row++)
                {
                    for (int column = 0; column < GameBoard.Size; column++)
                    {
                        int value = game[row, column];
                        int colorIndex = value == 0 ? 0 : Math.Min(TileColors.Length - 1, (int)Math.Log(value, 2));
                        var bounds = new RectangleF(gap + column * (cell + gap), gap + row * (cell + gap), cell, cell);
                        FillRounded(graphics, TileColors[colorIndex], bounds, gap * 0.74f);
                        if (value == 0)
                        {
                            continue;
                        }
                        string number = value.ToString(CultureInfo.InvariantCulture);
                        float fontSize = cell * Math.Min(0.41f, 1.24f / number.Length);
                        using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var brush = new SolidBrush(colorIndex >= 7 ? Color.White : TileInk))
                        {
                            graphics.DrawString(number, font, brush, bounds, format);
                        }
                    }
                }
            }
        }

        private static void FillRounded(Graphics graphics, Color color, RectangleF rectangle, float radius)
        {
            float diameter = radius * 2;
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(color))
            {
                path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
                path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
            }
        }
    }
}
