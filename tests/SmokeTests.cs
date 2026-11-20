using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;

namespace WaitingGaming
{
    internal static class SmokeTests
    {
        private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

        private static void Main()
        {
            Run("2048 merges once per tile and restores its saved state", MergeAndRestore);
            Run("2048 accepts every direction and leaves a no-op untouched", Directions);
            Run("2048 can continue after winning and detects a full board", WinAndGameOver);
            Run("2048 recovers from a corrupt save", CorruptSave);
            Run("Fast, late and duplicate hooks do not show the game", ShortAndReorderedTurns);
            Run("Offers are acknowledged per turn across concurrent sessions", LongAndConcurrentTurns);
            Run("Window metadata supports legacy and malformed event receipts", LegacyWindowEvents);
            Run("Game position persists and follows available screens", WindowPositions);
            Run("Hook process accepts stdin and omits conversation content", HookInput);
        }

        private static void Run(string name, Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "waiting-gaming-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                test(directory);
                Console.WriteLine("PASS " + name);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void MergeAndRestore(string directory)
        {
            SeedBoard(directory, 0, 0, 2, 2, 2, 2, 4, 4, 8, 0, 0, 2, 2, 4);
            GameBoard board = GameBoard.Load(directory, new LastCellRandom());
            Check(board.Move(Direction.Left), "move should change the board");
            Check(board[0, 0] == 4 && board[0, 1] == 4 && board[0, 2] == 0, "four equal tiles should produce two equal tiles");
            Check(board[1, 0] == 8 && board[1, 1] == 8 && board[1, 2] == 0, "merged tiles should not chain");
            Check(board[2, 0] == 4 && board[2, 1] == 4, "zeros should be removed before merging");
            Check(board.Score == 20 && board.BestScore == 20, "score must add each merged value");
            GameBoard saved = GameBoard.Load(directory);
            Check(saved.Score == 20 && saved.BestScore == 20 && saved[0, 1] == 4 && saved[3, 3] == 2,
                "the saved board must include the newly spawned tile");
            saved.Reset();
            GameBoard reset = GameBoard.Load(directory);
            Check(reset.Score == 0 && reset.BestScore == 20 && TileCount(reset) == 2, "reset should keep best score");
        }

        private static void Directions(string directory)
        {
            CheckDirection(directory, Direction.Left, 0, 0);
            CheckDirection(directory, Direction.Right, 0, 3);
            CheckDirection(directory, Direction.Up, 0, 1);
            CheckDirection(directory, Direction.Down, 3, 1);

            SeedBoard(directory, 0, 0, 2);
            GameBoard board = GameBoard.Load(directory, new LastCellRandom());
            Check(!board.Move(Direction.Left) && board.Score == 0 && TileCount(board) == 1,
                "an unchanged move cannot spawn a tile");
        }

        private static void CheckDirection(string directory, Direction direction, int row, int column)
        {
            SeedBoard(directory, 0, 0, 0, 8, 0, 0, 0, 8);
            GameBoard board = GameBoard.Load(directory, new LastCellRandom());
            int value = direction == Direction.Up || direction == Direction.Down ? 16 : 8;
            Check(board.Move(direction) && board[row, column] == value, "incorrect result for " + direction);
        }

        private static void WinAndGameOver(string directory)
        {
            SeedBoard(directory, 0, 0, 1024, 1024);
            GameBoard board = GameBoard.Load(directory, new LastCellRandom());
            Check(board.Move(Direction.Left) && board.HasWon, "joining two 1024s must win");
            Check(board.Move(Direction.Right) && board.HasWon, "play should continue after winning");
            SeedBoard(directory, 0, 0, 2, 4, 2, 4, 4, 2, 4, 2, 2, 4, 2, 4, 4, 2, 4, 2);
            Check(GameBoard.Load(directory).IsGameOver, "a full board without neighbors to merge is over");
            SeedBoard(directory, 0, 0, 2, 4, 2, 4, 4, 2, 4, 2, 2, 4, 2, 4, 4, 2, 2, 2);
            Check(!GameBoard.Load(directory).IsGameOver, "a full board can have a legal move");
        }

        private static void CorruptSave(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "board.dat"), "corrupt");
            GameBoard board = GameBoard.Load(directory);
            Check(!board.SaveFailed && TileCount(board) == 2, "invalid save should recover to a new game");
            Check(TileCount(GameBoard.Load(directory)) == 2, "recovered state should be readable");
        }

        private static void ShortAndReorderedTurns(string directory)
        {
            var turns = new TurnEvents(directory);
            Check(TurnEvents.Record(directory, "session-A", "1", TurnEventKind.Start, Start, TestOwner()), "publish start");
            Check(turns.Read(Start.AddSeconds(3.9)).EligibleTurns.Count == 0, "threshold is 4 seconds");
            Check(!TurnEvents.Record(directory, "session-A", "1", TurnEventKind.Start, Start.AddSeconds(2)),
                "a duplicate cannot reset the waiting timer");
            TurnEvents.Record(directory, "session-A", "1", TurnEventKind.Stop, Start.AddSeconds(2));
            TurnSnapshot shortTurn = turns.Read(Start.AddSeconds(5));
            Check(shortTurn.EligibleTurns.Count == 0 && !shortTurn.HasActiveTurns && shortTurn.LatestTerminal == TurnEventKind.Stop,
                "a quick stop should suppress the popup");
            TurnEvents.Record(directory, "session-A", "2", TurnEventKind.Interrupt, Start.AddSeconds(6));
            TurnEvents.Record(directory, "session-A", "2", TurnEventKind.Start, Start.AddSeconds(7), TestOwner());
            TurnSnapshot reordered = turns.Read(Start.AddSeconds(12));
            Check(reordered.EligibleTurns.Count == 0 && !reordered.HasActiveTurns && reordered.LatestTerminal == TurnEventKind.Interrupt,
                "an interrupt received first should still win over a late start");
        }

        private static void LongAndConcurrentTurns(string directory)
        {
            var turns = new TurnEvents(directory);
            TurnEvents.Record(directory, "session-A", "same-turn-id", TurnEventKind.Start, Start, TestOwner());
            TurnEvents.Record(directory, "session-B", "same-turn-id", TurnEventKind.Start, Start.AddSeconds(2), TestOwner());
            TurnSnapshot first = turns.Read(Start.AddSeconds(4));
            Check(first.EligibleTurns.Count == 1 && first.ActiveTurnKeys.Count == 2, "only first turn has waited four seconds");
            TurnInvitation offered = first.EligibleTurns[0];
            Check(offered.Owner.ProcessId == 2 && offered.Owner.Handle == 1, "window identity survives receipt serialization");
            turns.MarkSeen(offered.Key);
            TurnEvents.Record(directory, "session-A", "same-turn-id", TurnEventKind.Stop, Start.AddSeconds(5));
            TurnSnapshot overlapping = turns.Read(Start.AddSeconds(7));
            Check(overlapping.HasActiveTurns && overlapping.EligibleTurns.Count == 1 &&
                overlapping.EligibleTurns[0].Key != offered.Key, "offering A must not consume the younger B turn");
            turns.MarkSeen(overlapping.EligibleTurns[0].Key);
            Check(new TurnEvents(directory).Read(Start.AddSeconds(8)).EligibleTurns.Count == 0,
                "an offered turn must not invite again after restart");
            TurnEvents.Record(directory, "session-B", "next", TurnEventKind.Start, Start.AddSeconds(8), TestOwner());
            Check(turns.Read(Start.AddSeconds(12)).EligibleTurns.Count == 1, "a later turn can invite");
            TurnEvents.Record(directory, "session-B", "next", TurnEventKind.Interrupt, Start.AddSeconds(13));
            Check(turns.Read(Start.AddSeconds(14)).EligibleTurns.Count == 0, "an interruption should not open another popup");
        }

        private static WindowOwner TestOwner()
        {
            return new WindowOwner(1, 2, Start.UtcDateTime.Ticks);
        }

        private static void LegacyWindowEvents(string directory)
        {
            TurnEvents.Record(directory, "legacy", "one", TurnEventKind.Start, Start);
            TurnEvents.Record(directory, "new", "two", TurnEventKind.Start, Start, TestOwner());
            string[] starts = Directory.GetFiles(Path.Combine(directory, "turn-events"), "*.Start.evt");
            foreach (string file in starts)
            {
                if (new FileInfo(file).Length != 0)
                {
                    File.WriteAllText(file, "{invalid-json");
                    File.SetLastWriteTimeUtc(file, Start.UtcDateTime);
                }
            }
            TurnSnapshot snapshot = new TurnEvents(directory).Read(Start.AddSeconds(5));
            Check(snapshot.ActiveTurnKeys.Count == 2 && snapshot.EligibleTurns.Count == 0,
                "legacy and malformed starts should remain active without a window invitation");
            TurnEvents.Record(directory, "new", "two", TurnEventKind.Stop, Start.AddSeconds(6));
            snapshot = new TurnEvents(directory).Read(Start.AddSeconds(7));
            Check(snapshot.ActiveTurnKeys.Count == 1 && snapshot.LatestTerminal == TurnEventKind.Stop,
                "terminal receipts must work with legacy start metadata");
            Check(!CodexWindow.IsValid(null) && !CodexWindow.IsValid(TestOwner()),
                "an arbitrary or missing window identity must not become a Codex owner");
        }

        private static void WindowPositions(string directory)
        {
            var size = new Size(330, 540);
            var primary = new Rectangle(0, 0, 1920, 1040);
            var left = new Rectangle(-1280, -100, 1280, 984);
            Rectangle[] areas = { primary, left };
            var settings = new GameWindowState(directory);
            Check(settings.Restore(size, primary, areas) == new Point(795, 250), "first launch should be centered");
            settings.Save(new Point(-1000, 90));
            Check(settings.Restore(size, primary, areas) == new Point(-1000, 90), "same-process position should be kept");
            settings = new GameWindowState(directory);
            Check(settings.Restore(size, primary, areas) == new Point(-1000, 90), "negative coordinates should restore across restarts");
            Check(settings.Restore(size, primary, new[] { primary }) == new Point(0, 90),
                "a disconnected monitor should restore into a live work area");
            settings.Save(new Point(1900, 1000));
            Check(settings.Restore(size, primary, areas) == new Point(1590, 500), "position must fit within taskbar work area");
            File.WriteAllText(Path.Combine(directory, "window.dat"), "broken");
            Check(new GameWindowState(directory).Restore(size, primary, areas) == new Point(795, 250),
                "invalid position settings should fall back to center");
            Point extreme = GameWindowState.Place(new Point(int.MinValue, int.MaxValue), size, primary, areas);
            Check(left.Contains(new Rectangle(extreme, size)), "extreme coordinates must be clamped without overflow");
            Rectangle tiny = new Rectangle(20, 30, 100, 100);
            Check(GameWindowState.Place(new Point(900, 900), size, tiny, new[] { tiny }) == tiny.Location,
                "a tiny display must keep the title bar reachable");
        }

        private static void HookInput(string directory)
        {
            string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WaitingGaming.Hook.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.EnvironmentVariables["PLUGIN_DATA"] = directory;
            start.EnvironmentVariables["PLUGIN_ROOT"] = directory;
            using (Process hook = Process.Start(start))
            {
                hook.StandardInput.Write("{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"private-session\"," +
                    "\"turn_id\":\"private-turn\",\"prompt\":\"private-contents\"}");
                hook.StandardInput.Close();
                Check(hook.WaitForExit(5000) && hook.ExitCode == 0, "hook should exit promptly");
                Check(hook.StandardOutput.ReadToEnd().Trim() == "{}" && hook.StandardError.ReadToEnd().Length == 0,
                    "hook should return only an empty JSON response");
            }
            string[] receipts = Directory.GetFiles(Path.Combine(directory, "turn-events"));
            Check(receipts.Length == 1 && Path.GetFileName(receipts[0]).IndexOf("private", StringComparison.Ordinal) == -1,
                "receipt filenames should contain no raw identifiers");
            string receipt = File.ReadAllText(receipts[0]);
            Check(!receipt.Contains("private") && !receipt.Contains("prompt") && !receipt.Contains("session"),
                "receipt should contain no conversation content");
            if (receipt.Length != 0)
            {
                var metadata = new JavaScriptSerializer().DeserializeObject(receipt) as System.Collections.Generic.Dictionary<string, object>;
                Check(metadata != null && metadata.Count == 4 && metadata.ContainsKey("hwnd") && metadata.ContainsKey("process_id") &&
                    metadata.ContainsKey("process_start_utc_ticks") && metadata.ContainsKey("version"),
                    "only native window identity may accompany a start receipt");
            }
        }

        private static void SeedBoard(string directory, long score, long best, params int[] tiles)
        {
            using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, "board.dat"))))
            {
                writer.Write(1);
                writer.Write(score);
                writer.Write(best);
                for (int index = 0; index < GameBoard.Size * GameBoard.Size; index++)
                {
                    writer.Write(index < tiles.Length ? tiles[index] : 0);
                }
            }
        }

        private static int TileCount(GameBoard board)
        {
            int count = 0;
            for (int row = 0; row < GameBoard.Size; row++)
            {
                for (int column = 0; column < GameBoard.Size; column++)
                {
                    if (board[row, column] != 0)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class LastCellRandom : Random
        {
            public override int Next(int maxValue)
            {
                return maxValue - 1;
            }
        }
    }
}
