using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace WaitingGaming
{
    internal sealed class GameWindowState
    {
        private const int SaveVersion = 1;
        private const int SaveLength = 3 * sizeof(int);
        private const int MoveFileReplaceExisting = 0x1;
        private const int MoveFileWriteThrough = 0x8;
        private readonly string savePath;
        private Point? savedPosition;

        public GameWindowState(string dataDirectory)
        {
            savePath = Path.Combine(dataDirectory, "window.dat");
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(savePath)))
                {
                    if (reader.BaseStream.Length == SaveLength && reader.ReadInt32() == SaveVersion)
                        savedPosition = new Point(reader.ReadInt32(), reader.ReadInt32());
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public Point Restore(Size windowSize, Rectangle defaultWorkArea, Rectangle[] availableWorkAreas)
        {
            return Place(savedPosition, windowSize, defaultWorkArea, availableWorkAreas);
        }

        internal static Point Place(Point? savedPosition, Size windowSize, Rectangle defaultWorkArea, Rectangle[] availableWorkAreas)
        {
            if (windowSize.Width <= 0 || windowSize.Height <= 0)
                throw new ArgumentOutOfRangeException("windowSize");

            if (defaultWorkArea.Width <= 0 || defaultWorkArea.Height <= 0)
                defaultWorkArea = new Rectangle(Point.Empty, windowSize);

            if (!savedPosition.HasValue)
            {
                var center = new Point(
                    Coordinate((long)defaultWorkArea.X + (defaultWorkArea.Width - (long)windowSize.Width) / 2),
                    Coordinate((long)defaultWorkArea.Y + (defaultWorkArea.Height - (long)windowSize.Height) / 2));
                return Clamp(center, windowSize, defaultWorkArea);
            }

            Point desired = savedPosition.Value;
            Point nearest = Clamp(desired, windowSize, defaultWorkArea);
            double shortestDistance = double.PositiveInfinity;
            foreach (Rectangle workArea in availableWorkAreas ?? new Rectangle[0])
            {
                if (workArea.Width <= 0 || workArea.Height <= 0)
                    continue;

                Point candidate = Clamp(desired, windowSize, workArea);
                double horizontalDistance = (double)candidate.X - desired.X;
                double verticalDistance = (double)candidate.Y - desired.Y;
                double distance = horizontalDistance * horizontalDistance + verticalDistance * verticalDistance;
                if (distance < shortestDistance)
                {
                    nearest = candidate;
                    shortestDistance = distance;
                }
            }
            return nearest;
        }

        private static Point Clamp(Point position, Size windowSize, Rectangle workArea)
        {
            // Keep the title bar reachable when a display is smaller than the fixed-size game.
            long rightmost = Math.Max(workArea.X, (long)workArea.X + workArea.Width - windowSize.Width);
            long bottommost = Math.Max(workArea.Y, (long)workArea.Y + workArea.Height - windowSize.Height);
            return new Point(
                Coordinate(Math.Max(workArea.X, Math.Min(position.X, rightmost))),
                Coordinate(Math.Max(workArea.Y, Math.Min(position.Y, bottommost))));
        }

        private static int Coordinate(long value)
        {
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value));
        }

        public void Save(Point position)
        {
            savedPosition = position;
            string temporaryPath = savePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(SaveVersion);
                    writer.Write(position.X);
                    writer.Write(position.Y);
                    writer.Flush();
                    stream.Flush(true);
                }
                MoveFileEx(temporaryPath, savePath, MoveFileReplaceExisting | MoveFileWriteThrough);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
