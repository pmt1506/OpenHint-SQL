using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenHintSQL.Context;
using OpenHintSQL.Providers;
using OpenHintSQL.Schema;
using OpenHintSQL.Snippets;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Completion
{
    /// <summary>
    /// The completion engine orchestrates completion item generation by aggregating
    /// results from keyword providers, snippet providers, and schema cache based on
    /// the current SQL context.
    /// </summary>
    /// <remarks>
    /// This class is stateless and thread-safe. All methods can be called from any thread.
    /// Results are sorted by relevance and capped at <see cref="MaxResults"/> items.
    /// </remarks>
    internal static class CompletionEngine
    {
        /// <summary>
        /// Maximum number of completion items returned to keep the popup responsive.
        /// </summary>
        private const int MaxResults = 50;

        /// <summary>
        /// Generates a list of completion items based on the current editor state.
        /// </summary>
        /// <param name="prefix">The word being typed (prefix to filter by).</param>
        /// <param name="fullText">The full text of the editor buffer.</param>
        /// <param name="caretOffset">The caret's zero-based character offset.</param>
        /// <param name="server">The current SQL Server instance name (may be null).</param>
        /// <param name="database">The current database name (may be null).</param>
        /// <param name="connectionString">The active connection string (may be null).</param>
        /// <returns>
        /// A sorted, filtered list of <see cref="CompletionItemData"/> items, limited to
        /// <see cref="MaxResults"/> entries. Returns an empty list if no matches are found.
        /// </returns>
        public static List<CompletionItemData> GetCompletionItems(
            string prefix,
            string fullText,
            int caretOffset,
            string server,
            string database,
            string connectionString)
        {
            try
            {
                // Parse the SQL context at the caret position
                var context = SqlContextParser.GetContext(fullText, caretOffset);
                bool emptyPrefix = string.IsNullOrEmpty(prefix);
                bool inDotContext = SqlContextParser.GetTableContext(fullText, caretOffset) != null;

                // Empty-prefix is only valid in "strong" clause contexts (immediate-on-space
                // after FROM/JOIN/EXEC) or right after a dot (alias-qualified column list).
                if (emptyPrefix && !IsStrongClauseContext(context) && !inDotContext)
                    return new List<CompletionItemData>();

                Logger.Log($"CompletionEngine: prefix='{prefix}', context={context}, server='{server}', db='{database}'");

                var results = new List<CompletionItemData>();

                // Keyword/snippet matches are noise when the user hasn't typed a prefix —
                // in that case only schema items make sense.
                if (!emptyPrefix)
                {
                    AddKeywordMatches(results, prefix);
                    AddSnippetMatches(results, prefix);
                }

                AddSchemaMatches(results, prefix, context, fullText, caretOffset, server, database, connectionString);

                return SortAndLimit(results, prefix);
            }
            catch (Exception ex)
            {
                Logger.Error("CompletionEngine.GetCompletionItems failed", ex);
                return new List<CompletionItemData>();
            }
        }

        /// <summary>
        /// Adds matching SQL keywords to the results list.
        /// </summary>
        private static void AddKeywordMatches(List<CompletionItemData> results, string prefix)
        {
            try
            {
                var keywords = SqlKeywordProvider.GetMatches(prefix);
                if (keywords != null)
                {
                    foreach (var kw in keywords)
                    {
                        results.Add(new CompletionItemData
                        {
                            Text = kw.Text,
                            InsertText = kw.InsertText,
                            Description = kw.Description,
                            Kind = kw.Kind,
                            Priority = GetKeywordPriority(kw.Text, prefix),
                            IconKey = kw.IconKey
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddKeywordMatches failed", ex);
            }
        }

        /// <summary>
        /// Adds matching snippets to the results list.
        /// </summary>
        private static void AddSnippetMatches(List<CompletionItemData> results, string prefix)
        {
            try
            {
                var snippets = SnippetProvider.Instance.GetMatchingSnippets(prefix);
                if (snippets != null)
                {
                    foreach (var snippet in snippets)
                    {
                        results.Add(snippet);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddSnippetMatches failed", ex);
            }
        }

        /// <summary>
        /// Adds schema objects from the cache based on the current SQL context.
        /// Strict policy: tables are only offered in table positions (FROM, JOIN,
        /// UPDATE target, INSERT INTO target); columns only in column positions
        /// (SELECT, WHERE, ON, ORDER BY, GROUP BY, HAVING, SET, dot-context).
        /// </summary>
        private static void AddSchemaMatches(
            List<CompletionItemData> results,
            string prefix,
            SqlContext context,
            string fullText,
            int caretOffset,
            string server,
            string database,
            string connectionString)
        {
            try
            {
                // No schema completion without a connection
                if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(database))
                {
                    if (ContextNeedsSchema(context))
                    {
                        results.Add(BuildStatusItem(
                            "No active database connection",
                            "Connect the query window to enable table and column suggestions"));
                    }
                    return;
                }

                if (string.IsNullOrEmpty(connectionString))
                {
                    if (ContextNeedsSchema(context))
                    {
                        results.Add(BuildStatusItem(
                            "Connection details unavailable",
                            "Open a connected query window and try again"));
                    }
                    return;
                }

                var schema = SchemaCache.GetOrLoad(server, database, connectionString);
                if (schema == null || !schema.IsLoaded)
                {
                    if (ContextNeedsSchema(context))
                    {
                        var loadError = SchemaCache.GetLastLoadError(server, database, connectionString);
                        if (!string.IsNullOrWhiteSpace(loadError))
                        {
                            results.Add(BuildStatusItem(
                                "Schema load failed",
                                ShortenStatusDescription(loadError)));
                        }
                        else
                        {
                            results.Add(BuildStatusItem(
                                "Loading database schema...",
                                $"{server} / {database}"));
                        }
                    }
                    return;
                }

                // Dot-context (alias-qualified) wins regardless of the surrounding clause:
                // typing `c.` always means "columns of whatever c resolves to".
                if (SqlContextParser.GetTableContext(fullText, caretOffset) != null)
                {
                    AddColumns(results, schema, prefix, fullText, caretOffset);
                    return;
                }

                switch (context)
                {
                    // ── Table-position contexts ─────────────────────────────────
                    case SqlContext.FromClause:
                    case SqlContext.UpdateTarget:
                        AddTablesAndViews(results, schema, prefix);
                        break;

                    case SqlContext.JoinClause:
                        // FK-aware JOIN suggestions float above generic table matches.
                        AddJoinSuggestions(results, schema, fullText, caretOffset);
                        AddTablesAndViews(results, schema, prefix);
                        break;

                    case SqlContext.InsertColumns:
                        // After `INSERT INTO `, we're at the table-name position until
                        // an opening paren is typed; once inside `INSERT INTO foo (...)`,
                        // we're in the column-list position. Treat the table-name case
                        // as the only schema-aware branch; columns inside the paren are
                        // left to the dot-context path (typing `foo.` works there).
                        if (ParenDepthAfterContextKeyword(fullText, caretOffset) == 0)
                            AddTablesAndViews(results, schema, prefix);
                        break;

                    // ── Column-position contexts ────────────────────────────────
                    case SqlContext.SelectClause:
                    case SqlContext.WhereClause:
                    case SqlContext.OnClause:
                    case SqlContext.OrderByClause:
                    case SqlContext.GroupByClause:
                    case SqlContext.HavingClause:
                    case SqlContext.SetClause:
                        AddColumns(results, schema, prefix, fullText, caretOffset);
                        break;

                    // ── Other ───────────────────────────────────────────────────
                    case SqlContext.Exec:
                        AddProcedures(results, schema, prefix);
                        break;

                    // TopLevel / Unknown / CreateTable / AlterTable / DeclareVariable:
                    // no schema suggestions — keyword/snippet matches above are enough.
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddSchemaMatches failed", ex);
            }
        }

        private static CompletionItemData BuildStatusItem(string text, string description)
        {
            return new CompletionItemData
            {
                Text = text,
                InsertText = string.Empty,
                Description = description,
                Kind = CompletionItemKind.Status,
                Priority = 1000,
                IconKey = "Status"
            };
        }

        private static string ShortenStatusDescription(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return "Check the OpenHint SQL output log for details";

            description = description.Replace(Environment.NewLine, " ").Trim();
            return description.Length <= 180
                ? description
                : description.Substring(0, 177) + "...";
        }

        private static bool ContextNeedsSchema(SqlContext context)
        {
            switch (context)
            {
                case SqlContext.FromClause:
                case SqlContext.JoinClause:
                case SqlContext.UpdateTarget:
                case SqlContext.InsertColumns:
                case SqlContext.SelectClause:
                case SqlContext.WhereClause:
                case SqlContext.OnClause:
                case SqlContext.OrderByClause:
                case SqlContext.GroupByClause:
                case SqlContext.HavingClause:
                case SqlContext.SetClause:
                case SqlContext.Exec:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Counts unclosed `(` characters in the text between the nearest preceding
        /// clause keyword and the caret. Used to decide whether the caret is at a
        /// table-name position (depth 0) or inside a column list (depth &gt; 0).
        /// </summary>
        private static int ParenDepthAfterContextKeyword(string fullText, int caretOffset)
        {
            if (string.IsNullOrEmpty(fullText))
                return 0;

            int limit = Math.Min(caretOffset, fullText.Length);
            int depth = 0;
            for (int i = 0; i < limit; i++)
            {
                char c = fullText[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
            }
            return depth;
        }

        /// <summary>
        /// Adds matching tables and views from the schema cache.
        /// When <paramref name="prefix"/> is empty, returns every table and view (used by
        /// the immediate-on-space trigger after FROM/JOIN).
        /// </summary>
        private static void AddTablesAndViews(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string prefix)
        {
            try
            {
                if (string.IsNullOrEmpty(prefix))
                {
                    if (schema.Tables.Count == 0 && schema.Views.Count == 0)
                    {
                        results.Add(BuildStatusItem(
                            "No user tables or views found",
                            "Check the active database in the query window"));
                        return;
                    }

                    foreach (var table in schema.Tables.Values)
                        results.Add(BuildTableItem(table));
                    foreach (var view in schema.Views.Values)
                        results.Add(BuildViewItem(view));
                    return;
                }

                var matches = schema.GetMatchingObjects(prefix);
                if (matches != null)
                {
                    foreach (var item in matches)
                    {
                        if (item.Kind == CompletionItemKind.Table || item.Kind == CompletionItemKind.View)
                        {
                            results.Add(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddTablesAndViews failed", ex);
            }
        }

        private static CompletionItemData BuildTableItem(TableInfo t) => new CompletionItemData
        {
            Text = t.FullName,
            InsertText = t.Name,
            Description = $"Table: {t.BracketedName}",
            Kind = CompletionItemKind.Table,
            Priority = 10,
            IconKey = "Table"
        };

        private static CompletionItemData BuildViewItem(TableInfo v) => new CompletionItemData
        {
            Text = v.FullName,
            InsertText = v.Name,
            Description = $"View: {v.BracketedName}",
            Kind = CompletionItemKind.View,
            Priority = 15,
            IconKey = "View"
        };

        // ───────────────────────────────────────────────
        //  JOIN suggestions (FK-aware)
        // ───────────────────────────────────────────────

        /// <summary>
        /// Regex mirroring SqlContextParser.TableRefPattern — captures every FROM/JOIN
        /// table reference in the text so we can find which tables are already in scope.
        /// </summary>
        private static readonly Regex JoinScopePattern = new Regex(
            @"(?:FROM|JOIN)\s+" +
            @"(?:\[?(\w+)\]?\.)?" +
            @"\[?(\w+)\]?" +
            @"(?:\s+(?:AS\s+)?(\w+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Scoped table reference parsed from a FROM/JOIN clause already present in the text.
        /// </summary>
        private struct ScopedTable
        {
            public TableInfo Table;
            public string Alias; // user-provided alias if any, else null (we'll fall back to table name)
            public string EffectiveAlias => string.IsNullOrEmpty(Alias) ? Table.Name : Alias;
        }

        /// <summary>
        /// Builds FK-aware JOIN suggestions for every table that has a foreign-key relationship
        /// with one of the tables already in scope (FROM/JOIN before the caret).
        /// Each suggestion is a complete <c>&lt;table&gt; &lt;alias&gt; ON ...</c> insertion.
        /// </summary>
        private static void AddJoinSuggestions(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string fullText,
            int caretOffset)
        {
            try
            {
                if (schema == null || !schema.IsLoaded)
                    return;

                int limit = Math.Min(Math.Max(caretOffset, 0), fullText?.Length ?? 0);
                if (limit == 0)
                    return;

                var scopeText = fullText.Substring(0, limit);
                var scoped = ResolveScopedTables(scopeText, schema);
                if (scoped.Count == 0)
                    return;

                // Aliases (including generated ones) we must avoid colliding with.
                var usedAliases = new HashSet<string>(
                    scoped.Select(s => s.EffectiveAlias),
                    StringComparer.OrdinalIgnoreCase);

                // De-dup so we don't emit the same target table multiple times if it has
                // FKs to multiple scoped tables.
                var emittedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var scopedRef in scoped)
                {
                    // Outgoing FKs: this scoped table has FKs to other tables.
                    foreach (var fk in scopedRef.Table.ForeignKeys)
                    {
                        EmitJoinSuggestion(results, fk, scopedRef, fk.ReferencedTable,
                            fkParentIsScoped: true, usedAliases, emittedTargets);
                    }

                    // Incoming FKs: other tables have FKs to this scoped table.
                    foreach (var fk in scopedRef.Table.IncomingForeignKeys)
                    {
                        EmitJoinSuggestion(results, fk, scopedRef, fk.ParentTable,
                            fkParentIsScoped: false, usedAliases, emittedTargets);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddJoinSuggestions failed", ex);
            }
        }

        private static void EmitJoinSuggestion(
            List<CompletionItemData> results,
            ForeignKeyInfo fk,
            ScopedTable scopedRef,
            TableInfo targetTable,
            bool fkParentIsScoped,
            HashSet<string> usedAliases,
            HashSet<string> emittedTargets)
        {
            if (targetTable == null || ReferenceEquals(targetTable, scopedRef.Table))
                return;

            // First-FK-wins per target: avoid showing both Customers→Orders and Orders→Customers.
            if (!emittedTargets.Add(targetTable.FullName))
                return;

            var newAlias = GenerateAlias(targetTable.Name, usedAliases);
            usedAliases.Add(newAlias);

            // Compose the ON clause. ParentColumns/ReferencedColumns are paired by ordinal.
            // If the scoped table is the FK *parent*, the new table is the referenced side, and vice versa.
            var on = new StringBuilder();
            for (int i = 0; i < fk.ParentColumns.Count && i < fk.ReferencedColumns.Count; i++)
            {
                if (i > 0) on.Append(" AND ");
                if (fkParentIsScoped)
                {
                    // scoped = parent, target = referenced
                    on.Append(scopedRef.EffectiveAlias).Append('.').Append(fk.ParentColumns[i].Name);
                    on.Append(" = ");
                    on.Append(newAlias).Append('.').Append(fk.ReferencedColumns[i].Name);
                }
                else
                {
                    // scoped = referenced, target = parent
                    on.Append(newAlias).Append('.').Append(fk.ParentColumns[i].Name);
                    on.Append(" = ");
                    on.Append(scopedRef.EffectiveAlias).Append('.').Append(fk.ReferencedColumns[i].Name);
                }
            }

            // Display text shows what the user will get; InsertText excludes the leading "JOIN "
            // because the user has already typed it (the word "JOIN" is what we replace).
            string display = $"{targetTable.Name} {newAlias} ON {on}";
            string insert = $"{targetTable.Name} {newAlias} ON {on}";

            // The CommandFilter replaces only the word-before-caret (the table-name fragment).
            // For empty-prefix JOIN trigger, that word is empty, so InsertText is spliced
            // at the caret position immediately after the trailing space of "JOIN ".
            results.Add(new CompletionItemData
            {
                Text = display,
                InsertText = insert,
                Description = $"FK {fk.Name}",
                Kind = CompletionItemKind.JoinSuggestion,
                Priority = 100,
                IconKey = "JoinSuggestion"
            });
        }

        /// <summary>
        /// Parses FROM/JOIN clauses out of <paramref name="scopeText"/> and resolves each
        /// table-name reference to a <see cref="TableInfo"/> in the schema. References that
        /// don't match a known table are silently dropped.
        /// </summary>
        private static List<ScopedTable> ResolveScopedTables(string scopeText, DatabaseSchema schema)
        {
            var result = new List<ScopedTable>();
            var matches = JoinScopePattern.Matches(scopeText);

            foreach (Match m in matches)
            {
                string schemaName = m.Groups[1].Success ? m.Groups[1].Value : null;
                string tableName = m.Groups[2].Value;
                string alias = m.Groups[3].Success ? m.Groups[3].Value : null;

                TableInfo table = ResolveTable(schema, schemaName, tableName);
                if (table == null)
                    continue;

                result.Add(new ScopedTable { Table = table, Alias = alias });
            }

            return result;
        }

        private static TableInfo ResolveTable(DatabaseSchema schema, string schemaName, string tableName)
        {
            if (!string.IsNullOrEmpty(schemaName))
            {
                var full = $"{schemaName}.{tableName}";
                if (schema.Tables.TryGetValue(full, out var t))
                    return t;
                if (schema.Views.TryGetValue(full, out var v))
                    return v;
                return null;
            }

            // No schema specified — fall back to unique short-name match (mirrors what users do).
            var byShort = schema.Tables.Values
                .Where(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byShort.Count == 1)
                return byShort[0];

            var viewByShort = schema.Views.Values
                .Where(v => string.Equals(v.Name, tableName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (viewByShort.Count == 1)
                return viewByShort[0];

            return null;
        }

        /// <summary>
        /// Generates a short alias for <paramref name="tableName"/> that doesn't collide with
        /// <paramref name="usedAliases"/>. Strategy: first letter of each capital-letter run,
        /// lowercased; fall back to suffix counter on collision.
        /// </summary>
        private static string GenerateAlias(string tableName, HashSet<string> usedAliases)
        {
            if (string.IsNullOrEmpty(tableName))
                return "t";

            var sb = new StringBuilder();
            bool prevWasLower = false;
            foreach (var c in tableName)
            {
                if (char.IsUpper(c) || sb.Length == 0)
                {
                    sb.Append(char.ToLowerInvariant(c));
                    prevWasLower = false;
                }
                else
                {
                    prevWasLower = char.IsLower(c);
                }
            }

            string baseAlias = sb.Length > 0 ? sb.ToString() : char.ToLowerInvariant(tableName[0]).ToString();

            if (!usedAliases.Contains(baseAlias))
                return baseAlias;

            for (int i = 2; i < 100; i++)
            {
                var candidate = baseAlias + i;
                if (!usedAliases.Contains(candidate))
                    return candidate;
            }

            return baseAlias + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>
        /// Finds tables/views referenced in the text.
        /// </summary>
        private static HashSet<string> GetReferencedTables(string fullText, DatabaseSchema schema)
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(fullText) || schema == null)
                return referenced;

            foreach (var scoped in ResolveScopedTables(fullText, schema))
            {
                if (scoped.Table != null)
                    referenced.Add(scoped.Table.FullName);
            }

            if (referenced.Count > 0)
                return referenced;

            var words = Regex.Split(fullText, @"[^\w]");
            foreach (var word in words)
            {
                if (string.IsNullOrEmpty(word))
                    continue;

                if (schema.Tables.ContainsKey(word))
                    referenced.Add(word);
                else if (schema.Views.ContainsKey(word))
                    referenced.Add(word);
                else
                {
                    var table = ResolveTable(schema, null, word);
                    if (table != null)
                        referenced.Add(table.FullName);
                }
            }

            return referenced;
        }

        /// <summary>
        /// Adds matching columns from the schema cache.
        /// </summary>
        private static void AddColumns(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string prefix,
            string fullText,
            int caretOffset)
        {
            try
            {
                // Check if we are in a dot context (e.g. "u.Name")
                var tableContext = SqlContextParser.GetTableContext(fullText, caretOffset);
                if (tableContext != null)
                {
                    string targetTable = !string.IsNullOrEmpty(tableContext.ResolvedTable)
                        ? tableContext.ResolvedTable
                        : tableContext.AliasOrTable;

                    var cols = schema.GetColumnsForTable(targetTable);
                    if (cols != null)
                    {
                        foreach (var col in cols)
                        {
                            if (MatchesPrefix(col.Text, prefix))
                            {
                                results.Add(col);
                            }
                        }
                    }
                    return;
                }

                // If not in a dot context, find tables referenced in the script
                var referencedTables = GetReferencedTables(fullText, schema);
                if (referencedTables.Count > 0)
                {
                    foreach (var tableName in referencedTables)
                    {
                        var cols = schema.GetColumnsForTable(tableName);
                        if (cols != null)
                        {
                            foreach (var col in cols)
                            {
                                if (MatchesPrefix(col.Text, prefix))
                                {
                                    results.Add(col);
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Fallback: search all columns in the database (since database is small)
                    foreach (var table in schema.Tables.Values)
                    {
                        foreach (var col in table.Columns)
                        {
                            if (MatchesPrefix(col.Name, prefix))
                            {
                                results.Add(new CompletionItemData
                                {
                                    Text = col.Name,
                                    InsertText = col.Name,
                                    Description = col.DisplayText,
                                    Kind = CompletionItemKind.Column,
                                    Priority = 70,
                                    IconKey = "Column"
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddColumns failed", ex);
            }
        }

        /// <summary>
        /// Adds matching stored procedures from the schema cache.
        /// </summary>
        private static void AddProcedures(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string prefix)
        {
            try
            {
                var matches = schema.GetMatchingObjects(prefix);
                if (matches != null)
                {
                    foreach (var item in matches)
                    {
                        if (item.Kind == CompletionItemKind.Procedure || item.Kind == CompletionItemKind.Function)
                        {
                            results.Add(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddProcedures failed", ex);
            }
        }

        /// <summary>
        /// True when the context warrants opening the popup with no prefix typed —
        /// i.e. the user has just hit space after a clause keyword and we have a
        /// useful schema-driven list to show (tables, columns, or procedures).
        /// </summary>
        private static bool IsStrongClauseContext(SqlContext context)
        {
            switch (context)
            {
                case SqlContext.FromClause:
                case SqlContext.JoinClause:
                case SqlContext.UpdateTarget:
                case SqlContext.InsertColumns:
                case SqlContext.SelectClause:
                case SqlContext.WhereClause:
                case SqlContext.OnClause:
                case SqlContext.OrderByClause:
                case SqlContext.GroupByClause:
                case SqlContext.HavingClause:
                case SqlContext.SetClause:
                case SqlContext.Exec:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Checks if a name matches the given prefix (case-insensitive).
        /// An empty prefix matches every name — callers use that to enumerate.
        /// Also matches when the prefix appears in the unqualified part of a schema-qualified name.
        /// </summary>
        private static bool MatchesPrefix(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (string.IsNullOrEmpty(prefix))
                return true;

            // Direct prefix match (case-insensitive)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check the part after the dot for schema-qualified names (e.g., dbo.TableName)
            int dotIndex = name.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < name.Length - 1)
            {
                string unqualified = name.Substring(dotIndex + 1);
                if (unqualified.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Substring match for flexibility
            if (name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        /// <summary>
        /// Assigns a priority bonus to keywords that exactly match the prefix.
        /// </summary>
        private static int GetKeywordPriority(string keyword, string prefix)
        {
            if (string.Equals(keyword, prefix, StringComparison.OrdinalIgnoreCase))
                return 200; // Exact match

            if (keyword.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return 90; // Prefix match

            return 50; // Substring match
        }

        /// <summary>
        /// Sorts results by relevance and limits to <see cref="MaxResults"/> items.
        /// Sort order: exact prefix match first, then by priority descending, then alphabetical.
        /// </summary>
        private static List<CompletionItemData> SortAndLimit(List<CompletionItemData> results, string prefix)
        {
            if (results == null || results.Count == 0)
                return new List<CompletionItemData>();

            // Remove duplicates by text (case-insensitive)
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduplicated = new List<CompletionItemData>();
            foreach (var item in results)
            {
                if (item.Text != null && seen.Add(item.Text))
                {
                    deduplicated.Add(item);
                }
            }

            // Sort: exact prefix match first → priority desc → alphabetical
            var sorted = deduplicated
                .OrderByDescending(item =>
                    item.Text != null && item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? 1 : 0)
                .ThenByDescending(item => item.Priority)
                .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults)
                .ToList();

            return sorted;
        }
    }
}
