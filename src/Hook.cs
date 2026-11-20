using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace WaitingGaming
{
    internal static class Hook
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                HandleEvent();
            }
            catch (Exception)
            {
                // The game is optional, so consume parser and launch failures without warning Codex.
            }
            Console.WriteLine("{}");
        }

        private static void HandleEvent()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            WindowOwner foregroundOwner = CodexWindow.CaptureForeground();
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string json;
            using (var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                json = input.ReadToEnd();

            var message = serializer.DeserializeObject(json) as IDictionary<string, object>;
            if (message == null)
                return;

            TurnEventKind kind;
            switch (Field(message, "hook_event_name"))
            {
                case "UserPromptSubmit": kind = TurnEventKind.Start; break;
                case "Stop": kind = TurnEventKind.Stop; break;
                case "Interrupt": kind = TurnEventKind.Interrupt; break;
                default: return;
            }

            string dataDirectory = Environment.GetEnvironmentVariable("PLUGIN_DATA");
            bool recorded = TurnEvents.Record(dataDirectory, Field(message, "session_id"),
                Field(message, "turn_id"), kind, now, foregroundOwner);

            if (recorded && kind == TurnEventKind.Start && foregroundOwner != null)
                StartGame(dataDirectory);
        }

        private static string Field(IDictionary<string, object> message, string key)
        {
            object value;
            return message.TryGetValue(key, out value) ? value as string : null;
        }

        private static void StartGame(string dataDirectory)
        {
            string pluginRoot = Environment.GetEnvironmentVariable("PLUGIN_ROOT");
            if (string.IsNullOrWhiteSpace(pluginRoot))
                return;

            string gamePath = Path.Combine(pluginRoot, "bin", "WaitingGaming.exe");
            if (!File.Exists(gamePath))
                return;

            string fullDirectory = Path.GetFullPath(dataDirectory);
            if (fullDirectory.EndsWith("\\", StringComparison.Ordinal))
                fullDirectory += "\\";

            // ShellExecute keeps the game from inheriting the hook's redirected stdio.
            var start = new ProcessStartInfo(gamePath, "--data \"" + fullDirectory + "\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(gamePath)
            };
            using (Process.Start(start)) { }
        }
    }
}
