using System;
using System.IO;

namespace KspPrPicker
{
    // Mirrors everything that goes to the on-screen log into a plain-text file next to the exe
    // (picker.log). Used both for after-the-fact debugging and so tooling can tail the run output,
    // which a WinForms textbox can't expose. Thread-safe: Pipeline streams from a worker thread.
    internal static class Logger
    {
        static readonly object _lock = new object();

        public static string LogPath { get; private set; }

        public static void Init()
        {
            LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "picker.log");
            Write(Environment.NewLine + $"==== session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
        }

        public static void Write(string line)
        {
            if (LogPath == null) return;
            try
            {
                lock (_lock)
                    File.AppendAllText(LogPath, (line ?? "") + Environment.NewLine);
            }
            catch
            {
                // Logging must never take down the app; a locked/again-open file just drops the line.
            }
        }
    }
}
