using System;
using System.Collections.Generic;
using System.IO;
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
                    using (var window = new GameWindow(GameBoard.Load(dataDirectory), new GameWindowState(dataDirectory)))
                    {
                        window.RestorePosition(IntPtr.Zero);
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
        private readonly List<TurnInvitation> pending = new List<TurnInvitation>();
        private GameWindow window;
        private GameInvitationWindow invitation;
        private TurnInvitation invitationTurn;
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
            pending.RemoveAll(turn => !snapshot.ActiveTurnKeys.Contains(turn.Key) || !CodexWindow.IsValid(turn.Owner));
            if (invitationTurn != null && !pending.Exists(turn => turn.Key == invitationTurn.Key))
            {
                CloseInvitation();
            }
            if (window != null && window.Visible)
            {
                foreach (TurnInvitation turn in snapshot.EligibleTurns)
                {
                    turns.MarkSeen(turn.Key);
                }
                pending.Clear();
                CloseInvitation();
                UpdateStatus(snapshot);
            }
            else
            {
                foreach (TurnInvitation turn in snapshot.EligibleTurns)
                {
                    if (!CodexWindow.IsValid(turn.Owner))
                    {
                        turns.MarkSeen(turn.Key);
                    }
                    else if (!pending.Exists(candidate => candidate.Key == turn.Key))
                    {
                        pending.Add(turn);
                    }
                }
                UpdateInvitation();
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

        private void UpdateInvitation()
        {
            TurnInvitation candidate = pending.Find(turn =>
                CodexWindow.IsForeground(turn.Owner) ||
                (invitationTurn == turn && invitation != null && invitation.Visible && invitation.ContainsFocus));
            WindowBounds bounds;
            if (candidate == null || !CodexWindow.TryGetVisibleBounds(candidate.Owner, out bounds))
            {
                if (invitation != null)
                {
                    invitation.Hide();
                }
                return;
            }
            if (invitationTurn != candidate)
            {
                CloseInvitation();
                invitationTurn = candidate;
                invitation = new GameInvitationWindow();
                invitation.StartRequested += StartGame;
                invitation.Declined += DeclineGame;
            }
            if (!invitation.FollowOwner(bounds))
            {
                return;
            }
            if (!invitation.Visible)
            {
                // Persist the offer before showing it so a process restart won't ask twice.
                turns.MarkSeen(candidate.Key);
                invitation.Show(new WindowReference(candidate.Owner.Handle));
            }
        }

        private void StartGame(object sender, EventArgs eventArgs)
        {
            TurnInvitation selected = invitationTurn;
            if (selected == null)
            {
                return;
            }
            TurnSnapshot snapshot = turns.Read(DateTimeOffset.UtcNow);
            bool active = snapshot.ActiveTurnKeys.Contains(selected.Key) && CodexWindow.IsValid(selected.Owner);
            pending.Remove(selected);
            CloseInvitation();
            if (!active)
            {
                return;
            }
            if (window == null)
            {
                window = new GameWindow(GameBoard.Load(dataDirectory), new GameWindowState(dataDirectory));
                window.FormClosing += HideGame;
                window.FormClosed += CloseApplication;
            }
            UpdateStatus(snapshot);
            window.RestorePosition(new IntPtr(selected.Owner.Handle));
            window.Show();
            window.Activate();
        }

        private void DeclineGame(object sender, EventArgs eventArgs)
        {
            pending.Remove(invitationTurn);
            CloseInvitation();
        }

        private void CloseInvitation()
        {
            if (invitation != null)
            {
                invitation.StartRequested -= StartGame;
                invitation.Declined -= DeclineGame;
                invitation.Dispose();
                invitation = null;
                invitationTurn = null;
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
                CloseInvitation();
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
