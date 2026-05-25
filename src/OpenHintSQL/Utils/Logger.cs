using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace OpenHintSQL.Utils
{
    /// <summary>
    /// Centralized logging to VS Output Window and Debug trace.
    /// Thread-safe, fire-and-forget design — never blocks callers.
    /// </summary>
    public static class Logger
    {
        private static IVsOutputWindowPane _pane;
        private static IVsOutputWindow _outputWindow;
        private static readonly Guid PaneGuid = new Guid("E7A1F2B3-C4D5-4E6F-A7B8-C9D0E1F2A3B4");
        private static readonly object _lock = new object();
        private static bool _initialized;
        private static readonly string LogFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenHintSQL",
                "OpenHintSQL.log");

        /// <summary>
        /// Initialize the logger with a reference to the VS shell.
        /// Must be called on the UI thread.
        /// </summary>
        public static void Initialize(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                _outputWindow = serviceProvider.GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (_outputWindow != null)
                {
                    _outputWindow.CreatePane(ref Unsafe.AsRef(PaneGuid), "OpenHint SQL", 1, 1);
                    _outputWindow.GetPane(ref Unsafe.AsRef(PaneGuid), out _pane);
                }
                _initialized = true;
            }
            catch
            {
                // Fallback to Debug.WriteLine if output window unavailable
                _initialized = false;
            }
        }

        /// <summary>
        /// Log a message to the Output Window. Thread-safe.
        /// </summary>
        public static void Log(string message)
        {
            var timestamped = $"[OpenHintSQL {DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            Debug.Write(timestamped);

            WriteFileLog(timestamped);

            if (!_initialized || _pane == null) return;

            try
            {
                // OutputString is thread-safe in VS
                _pane.OutputStringThreadSafe(timestamped);
            }
            catch
            {
                // Silently ignore — logging should never crash the extension
            }
        }

        /// <summary>
        /// Log a warning message.
        /// </summary>
        public static void Warn(string message)
        {
            Log($"⚠ WARNING: {message}");
        }

        /// <summary>
        /// Log an error with exception details.
        /// </summary>
        public static void Error(string message, Exception ex = null)
        {
            if (ex != null)
                Log($"❌ ERROR: {message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            else
                Log($"❌ ERROR: {message}");
        }

        /// <summary>
        /// Reference helper to avoid readonly struct issues with ref params.
        /// </summary>
        private static class Unsafe
        {
            private static Guid _paneGuid = PaneGuid;
            public static ref Guid AsRef(Guid _) => ref _paneGuid;
        }

        private static void WriteFileLog(string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath));
                    File.AppendAllText(LogFilePath, message);
                }
            }
            catch
            {
                // File logging is diagnostic only and must never affect editor behavior.
            }
        }
    }
}
