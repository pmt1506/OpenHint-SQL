using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using OpenHintSQL.Providers;
using OpenHintSQL.Settings;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Raw FK row staged by the schema loader for resolution in <see cref="DatabaseSchema.Build"/>.
    /// Multi-column FKs produce multiple rows sharing the same <see cref="Name"/>.
    /// </summary>
    internal class PendingFkRow
    {
        public string Name;
        public int Ordinal;
        public string ParentSchema;
        public string ParentTable;
        public string ParentColumn;
        public string ReferencedSchema;
        public string ReferencedTable;
        public string ReferencedColumn;
    }

    /// <summary>
    /// In-memory container for a database's schema: tables, views, procedures, and functions.
    /// After populating the dictionaries, call <see cref="Build"/> to construct
    /// the sorted name lists and resolve primary/foreign keys.
    /// </summary>
    internal class DatabaseSchema
    {
        /// <summary>
        /// Tables keyed by full name (schema.name), case-insensitive.
        /// </summary>
        public Dictionary<string, TableInfo> Tables { get; }
            = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Views keyed by full name (schema.name), case-insensitive.
        /// </summary>
        public Dictionary<string, TableInfo> Views { get; }
            = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stored procedures and functions keyed by full name (schema.name), case-insensitive.
        /// </summary>
        public Dictionary<string, ProcedureInfo> Procedures { get; }
            = new Dictionary<string, ProcedureInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Sorted list of all table full names, built by <see cref="Build"/>.
        /// Excluded from JSON by <c>SchemaPersister</c>'s contract resolver.
        /// </summary>
        public IReadOnlyList<string> AllTableNames { get; private set; }
            = Array.Empty<string>();

        /// <summary>
        /// Sorted list of all view full names, built by <see cref="Build"/>.
        /// </summary>
        public IReadOnlyList<string> AllViewNames { get; private set; }
            = Array.Empty<string>();

        /// <summary>
        /// Whether the schema has been loaded from the database.
        /// </summary>
        public bool IsLoaded { get; set; }

        /// <summary>
        /// UTC timestamp when this schema was loaded.
        /// </summary>
        public DateTime LoadedAt { get; set; }

        /// <summary>
        /// Last load error for an unloaded schema, if a live server query failed.
        /// </summary>
        public string LoadError { get; set; }

        /// <summary>
        /// Raw FK rows populated by <see cref="AsyncSchemaLoader"/> before <see cref="Build"/> resolves them
        /// into <see cref="ForeignKeyInfo"/> object refs on the appropriate <see cref="TableInfo"/>.
        /// </summary>
        internal List<PendingFkRow> PendingForeignKeys { get; } = new List<PendingFkRow>();

        /// <summary>
        /// Returns an empty, unloaded schema instance.
        /// </summary>
        public static DatabaseSchema Empty
        {
            get
            {
                return new DatabaseSchema { IsLoaded = false };
            }
        }

        /// <summary>
        /// Builds the sorted name lists and key metadata from the populated dictionaries.
        /// Must be called after all tables, views, and procedures have been added — and
        /// after deserialising from disk, since none of the derived state is persisted.
        /// </summary>
        public void Build()
        {
            // Build sorted table name list
            var tableNames = Tables.Keys.ToList();
            tableNames.Sort(StringComparer.OrdinalIgnoreCase);
            AllTableNames = new ReadOnlyCollection<string>(tableNames);

            // Build sorted view name list
            var viewNames = Views.Keys.ToList();
            viewNames.Sort(StringComparer.OrdinalIgnoreCase);
            AllViewNames = new ReadOnlyCollection<string>(viewNames);

            // Rebuild PrimaryKeyColumns from the IsPrimaryKey flag on each column.
            // (The list itself isn't persisted — it's a convenience derived from columns.)
            foreach (var table in Tables.Values)
            {
                table.PrimaryKeyColumns.Clear();
                foreach (var col in table.Columns)
                {
                    if (col.IsPrimaryKey)
                        table.PrimaryKeyColumns.Add(col);
                }
                // Also wipe stale FK refs from a previous Build() — they'll be re-resolved below.
                table.ForeignKeys.Clear();
                table.IncomingForeignKeys.Clear();
            }

            // No per-character search index is built here: completion matches names with
            // substring scans, and a trie over every object name cost tens of MB that
            // was rebuilt (and garbage-collected) on every schema refresh.

            // Resolve pending FK rows into ForeignKeyInfo refs on the appropriate tables.
            ResolveForeignKeys();
        }

        /// <summary>
        /// Walks <see cref="PendingForeignKeys"/> and builds <see cref="ForeignKeyInfo"/> objects
        /// wired into the parent and referenced <see cref="TableInfo"/> instances.
        /// </summary>
        private void ResolveForeignKeys()
        {
            // Constraint names are only unique per schema, so two FKs can share a name
            // (and their rows interleave in name order). Group by name + parent table
            // instead of by consecutive name, or such FKs get merged into one bogus key.
            var byConstraint = new Dictionary<string, ForeignKeyInfo>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<ForeignKeyInfo>();
            var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in PendingForeignKeys)
            {
                string constraintKey = $"{row.ParentSchema}.{row.ParentTable}|{row.Name}";
                if (unresolved.Contains(constraintKey))
                    continue;

                if (!byConstraint.TryGetValue(constraintKey, out var current))
                {
                    current = StartForeignKey(row);
                    if (current == null)
                    {
                        unresolved.Add(constraintKey);
                        continue;
                    }

                    byConstraint[constraintKey] = current;
                    ordered.Add(current);
                }

                var parentCol = FindColumn(current.ParentTable, row.ParentColumn);
                var refCol = FindColumn(current.ReferencedTable, row.ReferencedColumn);
                if (parentCol != null && refCol != null)
                {
                    current.ParentColumns.Add(parentCol);
                    current.ReferencedColumns.Add(refCol);
                }
            }

            foreach (var fk in ordered)
                AttachForeignKey(fk);

            if (unresolved.Count > 0)
                Logger.Warn($"Skipped {unresolved.Count} FKs that could not be resolved to known tables");

            // NOTE: PendingForeignKeys is intentionally NOT cleared — it's the only
            // string-keyed FK snapshot that survives JSON persistence. Build() re-runs
            // after deserialise and needs these rows again.
        }

        private ForeignKeyInfo StartForeignKey(PendingFkRow row)
        {
            var parentKey = $"{row.ParentSchema}.{row.ParentTable}";
            var refKey = $"{row.ReferencedSchema}.{row.ReferencedTable}";

            if (!Tables.TryGetValue(parentKey, out var parent))
                return null;
            if (!Tables.TryGetValue(refKey, out var referenced))
                return null;

            return new ForeignKeyInfo
            {
                Name = row.Name,
                ParentTable = parent,
                ReferencedTable = referenced
            };
        }

        private static void AttachForeignKey(ForeignKeyInfo fk)
        {
            if (fk.ParentColumns.Count == 0 || fk.ReferencedColumns.Count == 0)
                return;

            fk.ParentTable.ForeignKeys.Add(fk);
            fk.ReferencedTable.IncomingForeignKeys.Add(fk);
        }

        private static ColumnInfo FindColumn(TableInfo table, string columnName)
        {
            if (table == null || string.IsNullOrEmpty(columnName))
                return null;
            return table.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns FK constraints that join <paramref name="a"/> and <paramref name="b"/> in either direction.
        /// Used by the JOIN-suggestion code path to decide which tables are "FK-related" to scoped tables.
        /// </summary>
        public IEnumerable<ForeignKeyInfo> GetForeignKeysBetween(TableInfo a, TableInfo b)
        {
            if (a == null || b == null)
                yield break;

            foreach (var fk in a.ForeignKeys)
            {
                if (ReferenceEquals(fk.ReferencedTable, b))
                    yield return fk;
            }
            foreach (var fk in a.IncomingForeignKeys)
            {
                if (ReferenceEquals(fk.ParentTable, b))
                    yield return fk;
            }
        }

        /// <summary>
        /// Returns completion items for all columns of the specified table or view.
        /// Accepts either the full name (schema.name) or short name.
        /// </summary>
        /// <param name="tableName">Table or view name to look up (case-insensitive).</param>
        /// <returns>List of column <see cref="CompletionItemData"/> items, or empty if not found.</returns>
        public List<CompletionItemData> GetColumnsForTable(string tableName)
        {
            var results = new List<CompletionItemData>();
            if (string.IsNullOrEmpty(tableName) || !IsLoaded)
                return results;

            // Try tables first, then views
            TableInfo tableInfo = null;
            if (!Tables.TryGetValue(tableName, out tableInfo))
            {
                Views.TryGetValue(tableName, out tableInfo);
            }

            // Fallback: search by short name if full name didn't match
            if (tableInfo == null)
            {
                tableInfo = Tables.Values.FirstOrDefault(t =>
                    t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
            }
            if (tableInfo == null)
            {
                tableInfo = Views.Values.FirstOrDefault(v =>
                    v.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
            }

            if (tableInfo == null)
                return results;

            foreach (var col in tableInfo.Columns)
            {
                string usageKey = $"{tableInfo.FullName}.{col.Name}";
                results.Add(new CompletionItemData
                {
                    Text = col.Name,
                    InsertText = col.Name,
                    Description = col.DisplayText,
                    Kind = CompletionItemKind.Column,
                    Priority = 5,
                    UsageScore = TableUsageProvider.GetUsageScore(CompletionItemKind.Column, usageKey),
                    IsFavorite = TableUsageProvider.IsFavorite(CompletionItemKind.Column, usageKey),
                    UsageKey = usageKey,
                    IconKey = "Column"
                });
            }

            return results;
        }
    }
}
