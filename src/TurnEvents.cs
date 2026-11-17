using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WaitingGaming
{
    internal enum TurnEventKind
    {
        Start,
        Stop,
        Interrupt,
        Seen
    }

    internal sealed class TurnSnapshot
    {
        internal bool ShouldShow { get; set; }
        internal bool HasActiveTurns { get; set; }
        internal TurnEventKind? LatestTerminal { get; set; }
    }

    internal sealed class TurnEvents
    {
        private static readonly TimeSpan ShowDelay = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan ActiveLifetime = TimeSpan.FromHours(8);
        private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromDays(1);

        private readonly string directory;
        private readonly List<string> activeTurns = new List<string>();
        private readonly HashSet<string> seenInProcess = new HashSet<string>();

        internal TurnEvents(string dataDirectory)
        {
            directory = Path.Combine(dataDirectory, "turn-events");
        }

        internal static bool Record(string dataDirectory, string sessionId, string turnId,
            TurnEventKind kind, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrWhiteSpace(sessionId) ||
                string.IsNullOrWhiteSpace(turnId))
                return false;

            var events = new TurnEvents(dataDirectory);
            return events.WriteReceipt(TurnKey(sessionId, turnId), kind, now.UtcDateTime);
        }

        internal TurnSnapshot Read(DateTimeOffset now)
        {
            DateTime utcNow = now.UtcDateTime;
            Dictionary<string, TurnReceipts> turns = ReadReceipts(utcNow);
            seenInProcess.RemoveWhere(key => !turns.ContainsKey(key));
            activeTurns.Clear();
            var snapshot = new TurnSnapshot();
            DateTime? latestTerminalTime = null;
            DateTime? latestExpiredStartTime = null;

            foreach (KeyValuePair<string, TurnReceipts> entry in turns)
            {
                TurnReceipts receipts = entry.Value;
                // A terminal receipt wins even if the asynchronous start hook ran later.
                DateTime? terminalTime = receipts.Interrupt ?? receipts.Stop;
                if (terminalTime.HasValue)
                {
                    if (receipts.Start.HasValue &&
                        (!latestTerminalTime.HasValue || terminalTime.Value > latestTerminalTime.Value))
                    {
                        latestTerminalTime = terminalTime;
                        snapshot.LatestTerminal = receipts.Interrupt.HasValue
                            ? TurnEventKind.Interrupt : TurnEventKind.Stop;
                    }
                    continue;
                }

                if (!receipts.Start.HasValue)
                    continue;
                if (utcNow - receipts.Start.Value >= ActiveLifetime)
                {
                    if (!latestExpiredStartTime.HasValue || receipts.Start.Value > latestExpiredStartTime.Value)
                        latestExpiredStartTime = receipts.Start;
                    continue;
                }

                activeTurns.Add(entry.Key);
                if (!receipts.Seen && !seenInProcess.Contains(entry.Key) &&
                    utcNow - receipts.Start.Value >= ShowDelay)
                    snapshot.ShouldShow = true;
            }

            snapshot.HasActiveTurns = activeTurns.Count != 0;
            if (latestExpiredStartTime > latestTerminalTime)
                snapshot.LatestTerminal = null;
            return snapshot;
        }

        internal void MarkVisible()
        {
            RememberActiveTurns();
        }

        internal void DismissActive()
        {
            RememberActiveTurns();
        }

        private void RememberActiveTurns()
        {
            foreach (string turn in activeTurns)
            {
                if (seenInProcess.Add(turn))
                    WriteReceipt(turn, TurnEventKind.Seen, DateTime.UtcNow);
            }
        }

        private Dictionary<string, TurnReceipts> ReadReceipts(DateTime utcNow)
        {
            var turns = new Dictionary<string, TurnReceipts>(StringComparer.Ordinal);
            try
            {
                var eventDirectory = new DirectoryInfo(directory);
                if (!eventDirectory.Exists)
                    return turns;

                foreach (FileInfo file in eventDirectory.EnumerateFiles())
                {
                    string[] parts = file.Name.Split('.');
                    Guid temporaryId;
                    bool isTemporary = parts.Length == 2 && parts[1] == "tmp" &&
                        Guid.TryParseExact(parts[0], "N", out temporaryId);
                    TurnEventKind kind = default(TurnEventKind);
                    bool isReceipt = parts.Length == 3 && parts[0].Length == 64 && parts[2] == "evt" &&
                        Enum.TryParse(parts[1], out kind) && kind.ToString() == parts[1];
                    if (!isTemporary && !isReceipt)
                        continue;

                    DateTime timestamp = file.LastWriteTimeUtc;
                    if (utcNow - timestamp >= ReceiptLifetime)
                    {
                        TryDelete(file.FullName);
                        continue;
                    }

                    if (!isReceipt)
                        continue;

                    TurnReceipts receipts;
                    if (!turns.TryGetValue(parts[0], out receipts))
                    {
                        receipts = new TurnReceipts();
                        turns.Add(parts[0], receipts);
                    }
                    receipts.Add(kind, timestamp);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return turns;
        }

        private bool WriteReceipt(string turn, TurnEventKind kind, DateTime timestamp)
        {
            string receiptPath = Path.Combine(directory, turn + "." + kind + ".evt");
            string tempPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(receiptPath))
                    return false;

                // An atomic rename publishes the first receipt for this turn and event.
                File.WriteAllBytes(tempPath, new byte[0]);
                File.SetLastWriteTimeUtc(tempPath, timestamp);
                File.Move(tempPath, receiptPath);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            finally { TryDelete(tempPath); }
        }

        private static string TurnKey(string sessionId, string turnId)
        {
            string pair = sessionId.Length.ToString(CultureInfo.InvariantCulture) + ":" + sessionId + turnId;
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(pair))).Replace("-", "");
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class TurnReceipts
        {
            internal DateTime? Start;
            internal DateTime? Stop;
            internal DateTime? Interrupt;
            internal bool Seen;

            internal void Add(TurnEventKind kind, DateTime timestamp)
            {
                switch (kind)
                {
                    case TurnEventKind.Start: Start = timestamp; break;
                    case TurnEventKind.Stop: Stop = timestamp; break;
                    case TurnEventKind.Interrupt: Interrupt = timestamp; break;
                    case TurnEventKind.Seen: Seen = true; break;
                }
            }
        }
    }
}
