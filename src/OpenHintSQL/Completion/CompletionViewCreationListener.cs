using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using OpenHintSQL.Connection;
using OpenHintSQL.Schema;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Completion
{
    /// <summary>
    /// MEF-exported listener that attaches a <see cref="CompletionCommandFilter"/>
    /// to every SQL editor view in SSMS. This is the entry point that wires up
    /// the entire completion system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SSMS 20 typically uses the content type "SQL Server Tools" for SQL editor windows.
    /// We export with that content type. A separate class (<see cref="CompletionViewCreationListenerSql"/>)
    /// handles the "sql" content type as a fallback, in case SSMS registers a different type.
    /// </para>
    /// <para>
    /// The listener logs the actual content type when a view is created, which is invaluable
    /// for debugging which content types SSMS registers.
    /// </para>
    /// </remarks>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("SQL Server Tools")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CompletionViewCreationListener : IVsTextViewCreationListener
    {
        /// <summary>
        /// MEF-imported adapter service for converting between VS interop views and WPF text views.
        /// </summary>
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;

        [Import(AllowDefault = true)]
        internal ICompletionBroker CompletionBroker = null;

        /// <summary>
        /// Called when a new text view is created. Attaches the command filter to the view.
        /// </summary>
        /// <param name="textViewAdapter">The interop text view adapter.</param>
        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            AttachCommandFilter(textViewAdapter, AdapterService, CompletionBroker);
        }

        /// <summary>
        /// Shared logic for attaching the completion command filter to a text view.
        /// </summary>
        internal static void AttachCommandFilter(
            IVsTextView textViewAdapter,
            IVsEditorAdaptersFactoryService adapterService,
            ICompletionBroker completionBroker = null)
        {
            try
            {
                if (textViewAdapter == null || adapterService == null)
                    return;

                IWpfTextView textView = adapterService.GetWpfTextView(textViewAdapter);
                if (textView == null)
                {
                    Logger.Warn("VsTextViewCreated: Could not get IWpfTextView from adapter.");
                    return;
                }

                // Log the content type for debugging (essential for identifying SSMS content types)
                string contentType = textView.TextBuffer?.ContentType?.TypeName ?? "(unknown)";
                Logger.Log($"TextViewCreated: ContentType='{contentType}'");

                // Avoid attaching multiple filters to the same view
                if (textView.Properties.ContainsProperty(typeof(CompletionCommandFilter)))
                {
                    Logger.Log("CompletionCommandFilter already attached to this view, skipping.");
                    return;
                }

                // Create the filter and insert it into the command chain
                var filter = new CompletionCommandFilter(textView, completionBroker);

                int hr = textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget nextTarget);
                if (hr != 0)
                {
                    Logger.Warn($"AddCommandFilter returned hr=0x{hr:X8}");
                    return;
                }

                filter.NextTarget = nextTarget;

                // Store in view properties to prevent double-attachment
                textView.Properties.AddProperty(typeof(CompletionCommandFilter), filter);

                Logger.Log($"CompletionCommandFilter attached to editor (ContentType='{contentType}').");

                // Kick off a background schema preload so the first completion request
                // doesn't have to wait on a cold-cache DB query. If the connection isn't
                // known yet (script opened detached), this no-ops; the lazy path inside
                // CompletionCommandFilter will pick it up on the first trigger instead.
                _ = Task.Run(() => PreloadSchemaForActiveConnection());
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to attach CompletionCommandFilter", ex);
            }
        }

        /// <summary>
        /// Runs on a background thread. Reads the active SSMS query connection and asks
        /// <see cref="SchemaCache"/> to warm itself. Silently does nothing if no
        /// connection is currently attached to the active editor.
        /// </summary>
        private static void PreloadSchemaForActiveConnection()
        {
            try
            {
                var conn = ConnectionTracker.GetActiveConnection();
                if (conn == null || string.IsNullOrEmpty(conn.Server) || string.IsNullOrEmpty(conn.Database))
                {
                    Logger.Log("Preload: no active connection on new view; will lazy-load on first trigger");
                    return;
                }

                // Returns immediately and kicks off the actual load on a background task.
                // The popup wired up in CompletionCommandFilter is already subscribed to
                // SchemaCache.OnSchemaLoaded, so a visible popup will refresh itself.
                Logger.Log($"Preload: warming cache for {conn.Server} / {conn.Database}");
                SchemaCache.GetOrLoad(conn.Server, conn.Database, conn.ConnectionString);
            }
            catch (Exception ex)
            {
                Logger.Warn($"PreloadSchemaForActiveConnection failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Fallback MEF listener for the "sql" content type, in case SSMS registers its
    /// SQL editor windows with this type instead of "SQL Server Tools".
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("sql")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CompletionViewCreationListenerSql : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;
        [Import(AllowDefault = true)]
        internal ICompletionBroker CompletionBroker = null;
        public void VsTextViewCreated(IVsTextView textViewAdapter) => CompletionViewCreationListener.AttachCommandFilter(textViewAdapter, AdapterService, CompletionBroker);
    }

    /// <summary>
    /// Fallback for "TSQL" (SSMS 18 case)
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("TSQL")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CompletionViewCreationListenerTSql : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;
        [Import(AllowDefault = true)]
        internal ICompletionBroker CompletionBroker = null;
        public void VsTextViewCreated(IVsTextView textViewAdapter) => CompletionViewCreationListener.AttachCommandFilter(textViewAdapter, AdapterService, CompletionBroker);
    }

    /// <summary>
    /// Fallback for "T-SQL"
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("T-SQL")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CompletionViewCreationListenerT_SQL : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;
        [Import(AllowDefault = true)]
        internal ICompletionBroker CompletionBroker = null;
        public void VsTextViewCreated(IVsTextView textViewAdapter) => CompletionViewCreationListener.AttachCommandFilter(textViewAdapter, AdapterService, CompletionBroker);
    }

    /// <summary>
    /// Fallback for "Transact-SQL"
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("Transact-SQL")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CompletionViewCreationListenerTransactSql : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;
        [Import(AllowDefault = true)]
        internal ICompletionBroker CompletionBroker = null;
        public void VsTextViewCreated(IVsTextView textViewAdapter) => CompletionViewCreationListener.AttachCommandFilter(textViewAdapter, AdapterService, CompletionBroker);
    }

    /// <summary>
    /// Fallback for "T-SQL90" (SSMS 18 legacy)
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("T-SQL90")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CompletionViewCreationListenerT_SQL90 : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;
        [Import(AllowDefault = true)]
        internal ICompletionBroker CompletionBroker = null;
        public void VsTextViewCreated(IVsTextView textViewAdapter) => CompletionViewCreationListener.AttachCommandFilter(textViewAdapter, AdapterService, CompletionBroker);
    }

    /// <summary>
    /// Fallback for "T-SQL100" (SSMS 18 legacy)
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("T-SQL100")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CompletionViewCreationListenerT_SQL100 : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterService = null;
        [Import(AllowDefault = true)]
        internal ICompletionBroker CompletionBroker = null;
        public void VsTextViewCreated(IVsTextView textViewAdapter) => CompletionViewCreationListener.AttachCommandFilter(textViewAdapter, AdapterService, CompletionBroker);
    }
}
