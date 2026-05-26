using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace OpenHintSQL
{
    /// <summary>
    /// VSPackage entry point for OpenHintSQL.
    /// Initializes the extension when SSMS loads.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class OpenHintSQLPackage : AsyncPackage
    {
        public const string PackageGuidString = "63D8CFAD-D1FF-40EB-80DB-7728DEDD7A91";

        /// <summary>
        /// Singleton instance of the package.
        /// </summary>
        public static OpenHintSQLPackage Instance { get; private set; }

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited.
        /// </summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Switch to UI thread for any UI-related initialization
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            Instance = this;

            Utils.Logger.Initialize(this);
            Utils.Logger.Log("OpenHintSQL v1.0.0 loaded successfully.");
            Utils.Logger.Diagnostic($"SSMS Process: {System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName}");
            Utils.Logger.Log($"Process Architecture: {(IntPtr.Size == 8 ? "64-bit" : "32-bit")}");
            Utils.Logger.Log($"VS Shell SDK Version: {typeof(Package).Assembly.GetName().Version}");

            // Load snippets configuration
            try
            {
                var snippetProvider = Snippets.SnippetProvider.Instance;
                Utils.Logger.Log($"Loaded {snippetProvider.Count} snippets.");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Warning: Failed to load snippets: {ex.Message}");
            }
        }
    }
}
