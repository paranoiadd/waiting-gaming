using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WaitingGaming
{
    internal sealed class WindowOwner
    {
        internal long Handle { get; private set; }
        internal int ProcessId { get; private set; }
        internal long ProcessStartUtcTicks { get; private set; }

        internal WindowOwner(long handle, int processId, long processStartUtcTicks)
        {
            Handle = handle;
            ProcessId = processId;
            ProcessStartUtcTicks = processStartUtcTicks;
        }
    }

    internal struct WindowBounds
    {
        internal int Left;
        internal int Top;
        internal int Width;
        internal int Height;
    }

    internal static class CodexWindow
    {
        private const uint RootAncestor = 2;
        private static readonly Dictionary<int, long> verifiedProcessStarts = new Dictionary<int, long>();

        internal static WindowOwner CaptureForeground()
        {
            IntPtr window = ForegroundRoot();
            uint processId;
            if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window) ||
                GetWindowThreadProcessId(window, out processId) == 0 || processId > int.MaxValue)
                return null;

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    long started = process.StartTime.ToUniversalTime().Ticks;
                    var owner = new WindowOwner(window.ToInt64(), (int)processId, started);
                    return IsCodexProcess(process, started) ? owner : null;
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        internal static bool IsValid(WindowOwner owner)
        {
            if (owner == null || owner.Handle <= 0 || owner.ProcessId <= 0 ||
                owner.ProcessStartUtcTicks <= 0 || (IntPtr.Size == 4 && owner.Handle > uint.MaxValue))
                return false;

            IntPtr window = Pointer(owner.Handle);
            uint processId;
            if (!IsWindow(window) || GetAncestor(window, RootAncestor) != window ||
                GetWindowThreadProcessId(window, out processId) == 0 || processId != owner.ProcessId)
                return false;

            try
            {
                using (Process process = Process.GetProcessById(owner.ProcessId))
                {
                    long started = process.StartTime.ToUniversalTime().Ticks;
                    return started == owner.ProcessStartUtcTicks && IsCodexProcess(process, started);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        internal static bool IsForeground(WindowOwner owner)
        {
            return IsValid(owner) && ForegroundRoot() == Pointer(owner.Handle);
        }

        internal static bool TryGetVisibleBounds(WindowOwner owner, out WindowBounds bounds)
        {
            bounds = new WindowBounds();
            if (!IsValid(owner))
                return false;

            IntPtr window = Pointer(owner.Handle);
            NativeRect rectangle;
            var origin = new NativePoint();
            if (!IsWindowVisible(window) || IsIconic(window) || !GetClientRect(window, out rectangle) ||
                !ClientToScreen(window, ref origin) || rectangle.Right <= 0 || rectangle.Bottom <= 0)
                return false;

            bounds.Left = origin.X;
            bounds.Top = origin.Y;
            bounds.Width = rectangle.Right;
            bounds.Height = rectangle.Bottom;
            return true;
        }

        private static bool IsCodexProcess(Process process, long started)
        {
            long verifiedStart;
            if (verifiedProcessStarts.TryGetValue(process.Id, out verifiedStart) && verifiedStart == started)
                return true;

            string image = process.MainModule.FileName;
            string name = Path.GetFileName(image);
            if (!string.Equals(name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "Codex.exe", StringComparison.OrdinalIgnoreCase))
                return false;

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(image);
            if (!string.Equals(version.ProductName, "Codex", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(version.CompanyName) ||
                !version.CompanyName.StartsWith("OpenAI", StringComparison.OrdinalIgnoreCase))
                return false;

            // A live PID and its start time identify the image already checked on disk.
            verifiedProcessStarts[process.Id] = started;
            return true;
        }

        private static IntPtr ForegroundRoot()
        {
            IntPtr foreground = GetForegroundWindow();
            return foreground == IntPtr.Zero ? IntPtr.Zero : GetAncestor(foreground, RootAncestor);
        }

        private static IntPtr Pointer(long handle)
        {
            return IntPtr.Size == 4 ? new IntPtr(unchecked((int)handle)) : new IntPtr(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    }
}
