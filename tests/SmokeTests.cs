using System;
using System.Diagnostics;
using System.IO;

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
            Run("A long turn shows once, and later turns can reopen", LongAndConcurrentTurns);
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
            Check(TurnEvents.Record(directory, "session-A", "1", TurnEventKind.Start, Start), "publish start");
            Check(!turns.Read(Start.AddSeconds(3.9)).ShouldShow, "threshold is 4 seconds");
            Check(!TurnEvents.Record(directory, "session-A", "1", TurnEventKind.Start, Start.AddSeconds(2)),
                "a duplicate cannot reset the waiting timer");
            TurnEvents.Record(directory, "session-A", "1", TurnEventKind.Stop, Start.AddSeconds(2));
            TurnSnapshot shortTurn = turns.Read(Start.AddSeconds(5));
            Check(!shortTurn.ShouldShow && !shortTurn.HasActiveTurns && shortTurn.LatestTerminal == TurnEventKind.Stop,
                "a quick stop should suppress the popup");
            TurnEvents.Record(directory, "session-A", "2", TurnEventKind.Interrupt, Start.AddSeconds(6));
            TurnEvents.Record(directory, "session-A", "2", TurnEventKind.Start, Start.AddSeconds(7));
            TurnSnapshot reordered = turns.Read(Start.AddSeconds(12));
            Check(!reordered.ShouldShow && !reordered.HasActiveTurns && reordered.LatestTerminal == TurnEventKind.Interrupt,
                "an interrupt received first should still win over a late start");
        }

        private static void LongAndConcurrentTurns(string directory)
        {
            var turns = new TurnEvents(directory);
            TurnEvents.Record(directory, "session-A", "same-turn-id", TurnEventKind.Start, Start);
            TurnEvents.Record(directory, "session-B", "same-turn-id", TurnEventKind.Start, Start.AddSeconds(2));
            Check(turns.Read(Start.AddSeconds(4)).ShouldShow, "first turn should show");
            turns.MarkVisible();
            TurnEvents.Record(directory, "session-A", "same-turn-id", TurnEventKind.Stop, Start.AddSeconds(5));
            TurnSnapshot overlapping = turns.Read(Start.AddSeconds(7));
            Check(overlapping.HasActiveTurns && !overlapping.ShouldShow, "finishing A must leave B active");
            turns.DismissActive();
            Check(!new TurnEvents(directory).Read(Start.AddSeconds(8)).ShouldShow, "dismissed turns must survive a restart");
            TurnEvents.Record(directory, "session-B", "next", TurnEventKind.Start, Start.AddSeconds(8));
            Check(turns.Read(Start.AddSeconds(12)).ShouldShow, "a later turn can reopen");
            TurnEvents.Record(directory, "session-B", "next", TurnEventKind.Interrupt, Start.AddSeconds(13));
            Check(!turns.Read(Start.AddSeconds(14)).ShouldShow, "an interruption should not open another popup");
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
            Check(receipts.Length == 1 && new FileInfo(receipts[0]).Length == 0 &&
                Path.GetFileName(receipts[0]).IndexOf("private", StringComparison.Ordinal) == -1,
                "receipt should contain no prompt text or raw identifiers");
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
