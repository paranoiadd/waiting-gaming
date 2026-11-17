using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WaitingGaming
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool preview = args.Length == 1 && args[0] == "--preview";
            if (!preview && (args.Length != 2 || args[0] != "--data" || !Path.IsPathRooted(args[1])))
            {
                return;
            }

            string dataDirectory = preview
                ? Path.Combine(Path.GetTempPath(), "waiting-gaming-preview", Guid.NewGuid().ToString("N"))
                : Path.GetFullPath(args[1]);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (preview)
            {
                try
                {
                    using (var window = new GameWindow(GameBoard.Load(dataDirectory), false))
                    {
                        window.SetStatus("预览模式", false);
                        Application.Run(window);
                    }
                }
                finally
                {
                    TryDeletePreview(dataDirectory);
                }
                return;
            }

            using (var instance = new Mutex(false, MutexName(dataDirectory)))
            {
                bool acquired;
                try
                {
                    // A new turn can arrive just as the previous hidden instance is exiting.
                    acquired = instance.WaitOne(500);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (!acquired)
                {
                    return;
                }

                try
                {
                    using (var application = new GameApplication(dataDirectory))
                    {
                        Application.Run(application);
                    }
                }
                finally
                {
                    instance.ReleaseMutex();
                }
            }
        }

        private static string MutexName(string dataDirectory)
        {
            using (var hash = SHA256.Create())
            {
                var directory = dataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                byte[] key = hash.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()));
                return @"Local\WaitingGaming-" + BitConverter.ToString(key).Replace("-", "");
            }
        }

        private static void TryDeletePreview(string dataDirectory)
        {
            try
            {
                if (Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class GameApplication : ApplicationContext
    {
        private readonly string dataDirectory;
        private readonly TurnEvents turns;
        private readonly System.Windows.Forms.Timer timer;
        private GameWindow window;
        private DateTimeOffset? idleSince;

        public GameApplication(string dataDirectory)
        {
            this.dataDirectory = dataDirectory;
            turns = new TurnEvents(dataDirectory);
            timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += OnTick;
            timer.Start();
        }

        private void OnTick(object sender, EventArgs eventArgs)
        {
            var now = DateTimeOffset.UtcNow;
            TurnSnapshot snapshot = turns.Read(now);
            if (snapshot.ShouldShow)
            {
                if (window == null)
                {
                    window = new GameWindow(GameBoard.Load(dataDirectory), true);
                    window.FormClosing += HideGame;
                    window.FormClosed += CloseApplication;
                }
                UpdateStatus(snapshot);
                if (!window.Visible)
                {
                    window.PlaceAtCorner();
                    window.Show();
                }
                turns.MarkVisible();
            }
            else if (window != null && window.Visible)
            {
                UpdateStatus(snapshot);
            }

            if (snapshot.HasActiveTurns || (window != null && window.Visible))
            {
                idleSince = null;
                return;
            }
            if (!idleSince.HasValue)
            {
                idleSince = now;
            }
            if (now - idleSince.Value >= TimeSpan.FromSeconds(10))
            {
                ExitThread();
            }
        }

        private void UpdateStatus(TurnSnapshot snapshot)
        {
            if (snapshot.HasActiveTurns)
            {
                window.SetStatus("Codex 正在处理", true);
            }
            else if (snapshot.LatestTerminal == TurnEventKind.Interrupt)
            {
                window.SetStatus("回合已中断", false);
            }
            else if (snapshot.LatestTerminal == TurnEventKind.Stop)
            {
                window.SetStatus("回合收尾中", false);
            }
            else
            {
                window.SetStatus("等待下一个回合", false);
            }
        }

        private void HideGame(object sender, FormClosingEventArgs eventArgs)
        {
            if (eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                turns.Read(DateTimeOffset.UtcNow);
                turns.DismissActive();
                window.Hide();
            }
        }

        private void CloseApplication(object sender, FormClosedEventArgs eventArgs)
        {
            window.FormClosing -= HideGame;
            window.FormClosed -= CloseApplication;
            window = null;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (window != null)
                {
                    window.FormClosing -= HideGame;
                    window.FormClosed -= CloseApplication;
                    window.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
