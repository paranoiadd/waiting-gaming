using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WaitingGaming
{
    internal enum Direction { Left, Right, Up, Down }

    internal sealed class GameBoard
    {
        public const int Size = 4;
        private const int VictoryTile = 2048;
        private const int SaveVersion = 1;
        private const int SaveLength = sizeof(int) + 2 * sizeof(long) + Size * Size * sizeof(int);
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;
        private readonly int[] tiles = new int[Size * Size];
        private readonly string savePath;
        private readonly Random random;

        private GameBoard(string dataDirectory, Random random)
        {
            savePath = Path.Combine(dataDirectory, "board.dat");
            this.random = random ?? new Random();
        }

        public int this[int row, int column] { get { return tiles[row * Size + column]; } }
        public long Score { get; private set; }
        public long BestScore { get; private set; }
        public bool SaveFailed { get; private set; }

        public bool HasWon
        {
            get { return Array.Exists(tiles, value => value >= VictoryTile); }
        }

        public bool IsGameOver
        {
            get
            {
                for (int row = 0; row < Size; row++)
                {
                    for (int column = 0; column < Size; column++)
                    {
                        int tile = this[row, column];
                        if (tile == 0 ||
                            (column + 1 < Size && CanMerge(tile, this[row, column + 1])) ||
                            (row + 1 < Size && CanMerge(tile, this[row + 1, column])))
                            return false;
                    }
                }
                return true;
            }
        }

        public static GameBoard Load(string dataDirectory, Random random = null)
        {
            var board = new GameBoard(dataDirectory, random);
            try
            {
                if (board.ReadSave())
                    return board;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { board.SaveFailed = true; }
            catch (UnauthorizedAccessException) { board.SaveFailed = true; }

            board.StartNewGame();
            // A transient read failure shouldn't replace a potentially valid save at startup.
            if (!board.SaveFailed)
                board.Save();
            return board;
        }

        public void Reset()
        {
            StartNewGame();
            Save();
        }

        public bool Move(Direction direction)
        {
            if (direction < Direction.Left || direction > Direction.Down)
                throw new ArgumentOutOfRangeException("direction");

            bool horizontal = direction == Direction.Left || direction == Direction.Right;
            bool reversed = direction == Direction.Right || direction == Direction.Down;
            int stride = horizontal ? 1 : Size;
            int step = reversed ? -stride : stride;
            int first = reversed ? (Size - 1) * stride : 0;
            int lineSpacing = horizontal ? Size : 1;

            bool changed = false;
            var compact = new int[Size];
            for (int line = 0; line < Size; line++)
            {
                int start = first + line * lineSpacing;
                int count = 0;
                for (int index = 0; index < Size; index++)
                {
                    int tile = tiles[start + index * step];
                    if (tile != 0)
                        compact[count++] = tile;
                }

                int next = 0;
                for (int index = 0; index < Size; index++)
                {
                    int value = next < count ? compact[next++] : 0;
                    // Read the original tiles so a newly merged tile can't merge again this move.
                    if (next < count && CanMerge(value, compact[next]))
                    {
                        value *= 2;
                        next++;
                        Score += Math.Min(value, long.MaxValue - Score);
                    }

                    int destination = start + index * step;
                    changed |= tiles[destination] != value;
                    tiles[destination] = value;
                }
            }

            if (!changed)
                return false;

            BestScore = Math.Max(Score, BestScore);
            SpawnTile();
            Save();
            return true;
        }

        private static bool CanMerge(int first, int second)
        {
            return first == second && first <= int.MaxValue / 2;
        }

        private void StartNewGame()
        {
            Array.Clear(tiles, 0, tiles.Length);
            Score = 0;
            SpawnTile();
            SpawnTile();
        }

        private void SpawnTile()
        {
            int count = 0;
            foreach (int tile in tiles)
            {
                if (tile == 0)
                    count++;
            }
            if (count == 0)
                return;

            int target = random.Next(count);
            for (int index = 0; index < tiles.Length; index++)
            {
                if (tiles[index] == 0 && target-- == 0)
                {
                    tiles[index] = random.Next(10) == 0 ? 4 : 2;
                    return;
                }
            }
        }

        private bool ReadSave()
        {
            using (var reader = new BinaryReader(File.OpenRead(savePath)))
            {
                if (reader.BaseStream.Length != SaveLength || reader.ReadInt32() != SaveVersion)
                    return false;

                long score = reader.ReadInt64();
                long bestScore = reader.ReadInt64();
                if (score < 0 || bestScore < score)
                    return false;

                bool hasTile = false;
                for (int index = 0; index < tiles.Length; index++)
                {
                    int tile = reader.ReadInt32();
                    if (tile < 0 || tile == 1 || (tile & (tile - 1)) != 0)
                        return false;
                    hasTile |= tile != 0;
                    tiles[index] = tile;
                }

                if (!hasTile)
                    return false;

                Score = score;
                BestScore = bestScore;
                return true;
            }
        }

        private void Save()
        {
            string temporaryPath = savePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(SaveVersion);
                    writer.Write(Score);
                    writer.Write(BestScore);
                    foreach (int tile in tiles)
                        writer.Write(tile);
                    writer.Flush();
                    stream.Flush(true);
                }

                // Rename in place so a failed write leaves the previous save readable.
                SaveFailed = !MoveFileEx(temporaryPath, savePath, MoveFileReplaceExisting | MoveFileWriteThrough);
            }
            catch (IOException) { SaveFailed = true; }
            catch (UnauthorizedAccessException) { SaveFailed = true; }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFile, string newFile, int flags);
    }
}
