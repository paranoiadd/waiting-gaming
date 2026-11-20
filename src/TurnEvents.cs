using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

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
        internal bool HasActiveTurns { get; set; }
        internal TurnEventKind? LatestTerminal { get; set; }
        internal List<TurnInvitation> EligibleTurns { get; private set; }
        internal HashSet<string> ActiveTurnKeys { get; private set; }

        internal TurnSnapshot()
        {
            EligibleTurns = new List<TurnInvitation>();
            ActiveTurnKeys = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    internal sealed class TurnInvitation
    {
        internal string Key { get; private set; }
        internal WindowOwner Owner { get; private set; }
        internal DateTime StartedUtc { get; private set; }

        internal TurnInvitation(string key, WindowOwner owner, DateTime startedUtc)
        {
            Key = key;
            Owner = owner;
            StartedUtc = startedUtc;
        }
    }

    internal sealed class TurnEvents
    {
        private static readonly TimeSpan ShowDelay = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan ActiveLifetime = TimeSpan.FromHours(8);
        private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromDays(1);

        private readonly string directory;
        private readonly HashSet<string> seenInProcess = new HashSet<string>();

        internal TurnEvents(string dataDirectory)
        {
            directory = Path.Combine(dataDirectory, "turn-events");
        }

        internal static bool Record(string dataDirectory, string sessionId, string turnId,
            TurnEventKind kind, DateTimeOffset now, WindowOwner owner = null)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrWhiteSpace(sessionId) ||
                string.IsNullOrWhiteSpace(turnId))
                return false;

            var events = new TurnEvents(dataDirectory);
            return events.WriteReceipt(TurnKey(sessionId, turnId), kind, now.UtcDateTime, owner);
        }

        internal TurnSnapshot Read(DateTimeOffset now)
        {
            DateTime utcNow = now.UtcDateTime;
            Dictionary<string, TurnReceipts> turns = ReadReceipts(utcNow);
            seenInProcess.RemoveWhere(key => !turns.ContainsKey(key));
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

                snapshot.ActiveTurnKeys.Add(entry.Key);
                if (receipts.Owner != null && !receipts.Seen && !seenInProcess.Contains(entry.Key) &&
                    utcNow - receipts.Start.Value >= ShowDelay)
                    snapshot.EligibleTurns.Add(new TurnInvitation(entry.Key, receipts.Owner, receipts.Start.Value));
            }

            snapshot.EligibleTurns.Sort(delegate(TurnInvitation first, TurnInvitation second)
            {
                int byTime = first.StartedUtc.CompareTo(second.StartedUtc);
                return byTime != 0 ? byTime : string.CompareOrdinal(first.Key, second.Key);
            });
            snapshot.HasActiveTurns = snapshot.ActiveTurnKeys.Count != 0;
            if (latestExpiredStartTime > latestTerminalTime)
                snapshot.LatestTerminal = null;
            return snapshot;
        }

        internal void MarkSeen(string turnKey)
        {
            if (!IsTurnKey(turnKey))
                return;
            if (seenInProcess.Add(turnKey))
                WriteReceipt(turnKey, TurnEventKind.Seen, DateTime.UtcNow, null);
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
                    bool isReceipt = parts.Length == 3 && IsTurnKey(parts[0]) && parts[2] == "evt" &&
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
                    if (kind == TurnEventKind.Start)
                        receipts.Owner = ReadOwner(file);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return turns;
        }

        private bool WriteReceipt(string turn, TurnEventKind kind, DateTime timestamp, WindowOwner owner)
        {
            string receiptPath = Path.Combine(directory, turn + "." + kind + ".evt");
            string tempPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(receiptPath))
                    return false;

                // An atomic rename publishes the first receipt for this turn and event.
                byte[] content = new byte[0];
                if (kind == TurnEventKind.Start && owner != null)
                {
                    string metadata = new JavaScriptSerializer().Serialize(new
                    {
                        version = 1,
                        hwnd = owner.Handle,
                        process_id = owner.ProcessId,
                        process_start_utc_ticks = owner.ProcessStartUtcTicks
                    });
                    content = Encoding.UTF8.GetBytes(metadata);
                }
                File.WriteAllBytes(tempPath, content);
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

        private static bool IsTurnKey(string value)
        {
            if (value == null || value.Length != 64)
                return false;
            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'A' && character <= 'F')))
                    return false;
            }
            return true;
        }

        private static WindowOwner ReadOwner(FileInfo file)
        {
            try
            {
                if (file.Length == 0 || file.Length > 1024)
                    return null;
                var serializer = new JavaScriptSerializer { MaxJsonLength = 1024, RecursionLimit = 4 };
                var values = serializer.DeserializeObject(File.ReadAllText(file.FullName, Encoding.UTF8))
                    as IDictionary<string, object>;
                long version = IntegerField(values, "version");
                long handle = IntegerField(values, "hwnd");
                long processId = IntegerField(values, "process_id");
                long started = IntegerField(values, "process_start_utc_ticks");
                if (version != 1 || handle <= 0 || processId <= 0 || processId > int.MaxValue ||
                    started <= 0 || started > DateTime.MaxValue.Ticks)
                    return null;
                return new WindowOwner(handle, (int)processId, started);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        private static long IntegerField(IDictionary<string, object> values, string key)
        {
            object value;
            if (values == null || !values.TryGetValue(key, out value) || !(value is int || value is long))
                return 0;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
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
            internal WindowOwner Owner;

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
