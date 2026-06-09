using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenHintSQL.Context;
using OpenHintSQL.Providers;
using OpenHintSQL.Schema;
using OpenHintSQL.Settings;
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
        private const int MinFuzzyPrefixLength = 4;
        private const int MinDirectResultsBeforeFuzzy = 3;

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
                bool inUseDatabasePosition = context == SqlContext.UseDatabase &&
                    IsUseDatabasePosition(fullText, caretOffset);

                // Empty-prefix is only valid in "strong" clause contexts (immediate-on-space
                // after FROM/JOIN/EXEC/USE) or right after a dot (alias-qualified column list).
                if (emptyPrefix && !IsStrongClauseContext(context) && !inDotContext)
                    return new List<CompletionItemData>();

                Logger.Diagnostic($"CompletionEngine: prefix='{prefix}', context={context}, server='{server}', db='{database}'");

                var results = new List<CompletionItemData>();

                if (inUseDatabasePosition)
                {
                    AddDatabaseMatches(results, prefix, server, connectionString);
                    return SortAndLimit(results, prefix);
                }

                // In table/object positions, keep the popup focused on database objects.
                // Keywords and snippets here are syntactic noise once the user is choosing
                // a FROM/JOIN/UPDATE/INSERT target.
                if (!inDotContext && IsTableObjectContext(context, fullText, caretOffset, prefix))
                {
                    AddSchemaMatches(results, prefix, context, fullText, caretOffset, server, database, connectionString);
                    return SortAndLimit(results, prefix);
                }

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
                    AddColumns(results, schema, prefix, fullText, caretOffset, context);
                    return;
                }

                switch (context)
                {
                    // ── Table-position contexts ─────────────────────────────────
                    case SqlContext.FromClause:
                        AddTablesAndViews(results, schema, prefix, includeAlias: true, excludeCurrentJoin: false, fullText: fullText, caretOffset: caretOffset);
                        break;

                    case SqlContext.UpdateTarget:
                        AddTablesAndViews(results, schema, prefix, includeAlias: false, excludeCurrentJoin: false, fullText: fullText, caretOffset: caretOffset);
                        break;

                    case SqlContext.JoinClause:
                        // FK-aware JOIN suggestions float above generic table matches.
                        AddJoinSuggestions(results, schema, fullText, caretOffset, prefix);
                        AddTablesAndViews(results, schema, prefix, includeAlias: true, excludeCurrentJoin: true, fullText: fullText, caretOffset: caretOffset);
                        break;

                    case SqlContext.InsertColumns:
                        // After `INSERT INTO `, we're at the table-name position until
                        // an opening paren is typed; once inside `INSERT INTO foo (...)`,
                        // we're in the column-list position. Treat the table-name case
                        // as the only schema-aware branch; columns inside the paren are
                        // left to the dot-context path (typing `foo.` works there).
                        if (ParenDepthAfterContextKeyword(fullText, caretOffset) == 0)
                            AddTablesAndViews(results, schema, prefix, includeAlias: false, excludeCurrentJoin: false, fullText: fullText, caretOffset: caretOffset);
                        break;

                    // ── Column-position contexts ────────────────────────────────
                    case SqlContext.SelectClause:
                    case SqlContext.WhereClause:
                    case SqlContext.OrderByClause:
                    case SqlContext.GroupByClause:
                    case SqlContext.HavingClause:
                    case SqlContext.SetClause:
                        AddScopedAliasSuggestions(results, schema, prefix, fullText, caretOffset, prioritizeCurrentJoinTarget: false, includeTablesAfterCaret: true);
                        AddColumns(results, schema, prefix, fullText, caretOffset, context);
                        break;

                    case SqlContext.OnClause:
                        AddScopedAliasSuggestions(results, schema, prefix, fullText, caretOffset, prioritizeCurrentJoinTarget: true, includeTablesAfterCaret: false);
                        AddOnClauseJoinColumnSuggestions(results, schema, prefix, fullText, caretOffset);
                        AddColumns(results, schema, prefix, fullText, caretOffset, context);
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

        private static void AddDatabaseMatches(
            List<CompletionItemData> results,
            string prefix,
            string server,
            string connectionString)
        {
            try
            {
                if (string.IsNullOrEmpty(server))
                {
                    results.Add(BuildStatusItem(
                        "No active server connection",
                        "Connect the query window to enable database suggestions"));
                    return;
                }

                if (string.IsNullOrEmpty(connectionString))
                {
                    results.Add(BuildStatusItem(
                        "Connection details unavailable",
                        "Open a connected query window and try again"));
                    return;
                }

                var databases = DatabaseListCache.GetOrLoad(server, connectionString);
                if (databases == null || !databases.IsLoaded)
                {
                    var loadError = DatabaseListCache.GetLastLoadError(server, connectionString);
                    if (!string.IsNullOrWhiteSpace(loadError))
                    {
                        results.Add(BuildStatusItem(
                            "Database list load failed",
                            ShortenStatusDescription(loadError)));
                    }
                    else
                    {
                        results.Add(BuildStatusItem(
                            "Loading databases...",
                            server));
                    }
                    return;
                }

                if (databases.Names.Count == 0)
                {
                    results.Add(BuildStatusItem(
                        "No databases found",
                        "The active login cannot see any online databases on this server"));
                    return;
                }

                foreach (var name in databases.Names)
                {
                    if (MatchesPrefix(name, prefix))
                        results.Add(BuildDatabaseItem(name));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddDatabaseMatches failed", ex);
            }
        }

        private static CompletionItemData BuildDatabaseItem(string databaseName)
        {
            var bracketedName = QuoteIdentifier(databaseName);
            return new CompletionItemData
            {
                Text = databaseName,
                InsertText = bracketedName,
                Description = $"Database: {bracketedName}",
                Kind = CompletionItemKind.Database,
                Priority = 30,
                IconKey = "Database"
            };
        }

        private static string QuoteIdentifier(string identifier)
        {
            return $"[{(identifier ?? string.Empty).Replace("]", "]]")}]";
        }

        private static bool IsUseDatabasePosition(string fullText, int caretOffset)
        {
            if (string.IsNullOrEmpty(fullText) || caretOffset <= 0)
                return false;

            int pos = Math.Min(caretOffset, fullText.Length) - 1;

            while (pos >= 0 && IsDatabaseNameFragmentChar(fullText[pos]))
                pos--;

            if (pos < 0 || !char.IsWhiteSpace(fullText[pos]))
                return false;

            while (pos >= 0 && char.IsWhiteSpace(fullText[pos]))
                pos--;

            const string keyword = "USE";
            int start = pos - keyword.Length + 1;
            if (start < 0)
                return false;

            if (!string.Equals(fullText.Substring(start, keyword.Length), keyword, StringComparison.OrdinalIgnoreCase))
                return false;

            return start == 0 || !IsDatabaseNameFragmentChar(fullText[start - 1]);
        }

        private static bool IsDatabaseNameFragmentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '[' || c == ']';
        }

        private static bool MatchesColumnPrefix(string name, string prefix)
        {
            if (MatchesPrefix(name, prefix))
                return true;

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prefix))
                return false;

            string normalizedName = NormalizeLooseIdentifier(name);
            string normalizedPrefix = NormalizeLooseIdentifier(prefix);
            if (string.IsNullOrEmpty(normalizedName) || string.IsNullOrEmpty(normalizedPrefix))
                return false;

            return normalizedName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLooseIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
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
            string prefix,
            bool includeAlias,
            bool excludeCurrentJoin,
            string fullText,
            int caretOffset)
        {
            try
            {
                var usedAliases = includeAlias
                    ? GetUsedAliases(fullText, caretOffset, schema, excludeCurrentJoin)
                    : null;

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
                        results.Add(BuildTableItem(table, includeAlias, usedAliases));
                    foreach (var view in schema.Views.Values)
                        results.Add(BuildViewItem(view, includeAlias, usedAliases));
                    return;
                }

                foreach (var table in schema.Tables.Values)
                {
                    if (MatchesTablePrefix(table, prefix))
                        results.Add(BuildTableItem(table, includeAlias, usedAliases));
                }

                foreach (var view in schema.Views.Values)
                {
                    if (MatchesTablePrefix(view, prefix))
                        results.Add(BuildViewItem(view, includeAlias, usedAliases));
                }

                if (ShouldUseFuzzyFallback(prefix, results.Count))
                {
                    foreach (var table in schema.Tables.Values)
                    {
                        if (MatchesTablePrefix(table, prefix) || !IsFuzzyTableMatch(table, prefix))
                            continue;

                        var item = BuildTableItem(table, includeAlias, usedAliases);
                        item.Priority = 1;
                        results.Add(item);
                    }

                    foreach (var view in schema.Views.Values)
                    {
                        if (MatchesTablePrefix(view, prefix) || !IsFuzzyTableMatch(view, prefix))
                            continue;

                        var item = BuildViewItem(view, includeAlias, usedAliases);
                        item.Priority = 1;
                        results.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddTablesAndViews failed", ex);
            }
        }

        private static CompletionItemData BuildTableItem(TableInfo t, bool includeAlias, HashSet<string> usedAliases)
        {
            var insertText = BuildObjectInsertText(t, includeAlias, usedAliases);
            int usageScore = TableUsageProvider.GetUsageScore(t?.FullName);
            return new CompletionItemData
            {
                Text = t.FullName,
                InsertText = insertText,
                Description = $"Table: {insertText}",
                Kind = CompletionItemKind.Table,
                Priority = 10,
                UsageScore = usageScore,
                IsFavorite = TableUsageProvider.IsFavorite(t?.FullName),
                IconKey = "Table"
            };
        }

        private static CompletionItemData BuildViewItem(TableInfo v, bool includeAlias, HashSet<string> usedAliases)
        {
            var insertText = BuildObjectInsertText(v, includeAlias, usedAliases);
            return new CompletionItemData
            {
                Text = v.FullName,
                InsertText = insertText,
                Description = $"View: {insertText}",
                Kind = CompletionItemKind.View,
                Priority = 15,
                IconKey = "View"
            };
        }

        private static string BuildObjectInsertText(TableInfo table, bool includeAlias, HashSet<string> usedAliases)
        {
            if (!includeAlias || table == null)
                return GetObjectInsertName(table);

            var alias = GenerateAlias(table.Name, usedAliases ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return $"{GetObjectInsertName(table)} {alias}";
        }

        private static bool MatchesTablePrefix(TableInfo table, string prefix)
        {
            if (table == null)
                return false;

            return MatchesPrefix(table.FullName, prefix) ||
                   MatchesPrefix(table.Name, prefix) ||
                   MatchesPrefix(table.BracketedName, prefix);
        }

        private static bool IsFuzzyTableMatch(TableInfo table, string prefix)
        {
            if (table == null)
                return false;

            return IsFuzzyMatch(table.FullName, prefix) ||
                   IsFuzzyMatch(table.Name, prefix) ||
                   IsFuzzyMatch(table.BracketedName, prefix);
        }

        private static HashSet<string> GetUsedAliases(
            string fullText,
            int caretOffset,
            DatabaseSchema schema,
            bool excludeCurrentJoin)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (schema == null || string.IsNullOrEmpty(fullText))
                return aliases;

            int limit = Math.Min(Math.Max(caretOffset, 0), fullText.Length);
            if (limit == 0)
                return aliases;

            var scopeText = excludeCurrentJoin
                ? GetCompletedJoinScopeText(fullText, limit)
                : fullText.Substring(0, limit);

            foreach (var scoped in ResolveScopedTables(scopeText, schema))
            {
                if (!string.IsNullOrWhiteSpace(scoped.EffectiveAlias))
                    aliases.Add(scoped.EffectiveAlias);
            }

            return aliases;
        }

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

        private static readonly Regex JoinKeywordPattern = new Regex(
            @"\b(?:JOIN|APPLY)\b",
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
            int caretOffset,
            string prefix)
        {
            try
            {
                if (schema == null || !schema.IsLoaded)
                    return;

                int limit = Math.Min(Math.Max(caretOffset, 0), fullText?.Length ?? 0);
                if (limit == 0)
                    return;

                var scopeText = GetCompletedJoinScopeText(fullText, limit);
                var scoped = ResolveScopedTables(scopeText, schema);
                if (scoped.Count == 0)
                    return;

                // Aliases (including generated ones) we must avoid colliding with.
                var usedAliases = new HashSet<string>(
                    scoped.Select(s => s.EffectiveAlias),
                    StringComparer.OrdinalIgnoreCase);

                // De-dup exact suggestion shapes, but allow the same target table to appear
                // multiple times when different scoped tables can join to it.
                var emittedSuggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int scopedIndex = scoped.Count - 1; scopedIndex >= 0; scopedIndex--)
                {
                    var scopedRef = scoped[scopedIndex];

                    // Outgoing FKs: this scoped table has FKs to other tables.
                    foreach (var fk in scopedRef.Table.ForeignKeys)
                    {
                        EmitJoinSuggestion(results, fk, scopedRef, fk.ReferencedTable,
                            fkParentIsScoped: true, usedAliases, emittedSuggestions, prefix, scopedIndex);
                    }

                    // Incoming FKs: other tables have FKs to this scoped table.
                    foreach (var fk in scopedRef.Table.IncomingForeignKeys)
                    {
                        EmitJoinSuggestion(results, fk, scopedRef, fk.ParentTable,
                            fkParentIsScoped: false, usedAliases, emittedSuggestions, prefix, scopedIndex);
                    }
                }

                AddHeuristicJoinSuggestions(results, schema, scoped, usedAliases, emittedSuggestions, prefix);
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
            HashSet<string> emittedSuggestions,
            string prefix,
            int scopedIndex)
        {
            if (targetTable == null || ReferenceEquals(targetTable, scopedRef.Table))
                return;
            if (!MatchesPrefix(targetTable.FullName, prefix) &&
                !MatchesPrefix(targetTable.Name, prefix) &&
                !MatchesPrefix(targetTable.BracketedName, prefix))
                return;

            var newAlias = GenerateAlias(targetTable.Name, usedAliases);

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

            // Display text shows what the user will get; InsertText starts at the table
            // reference because the leading JOIN keyword is preserved from the query text.
            string display = $"{GetObjectInsertName(targetTable)} {newAlias} ON {on}";
            string insert = $"{GetObjectInsertName(targetTable)} {newAlias} ON {on}";
            string suggestionKey = $"FK|{scopedRef.EffectiveAlias}|{targetTable.FullName}|{on}";
            if (!emittedSuggestions.Add(suggestionKey))
                return;

            // For empty-prefix JOIN trigger, InsertText is spliced at the caret position
            // immediately after the trailing space of "JOIN ".
            results.Add(new CompletionItemData
            {
                Text = display,
                InsertText = insert,
                Description = $"FK {fk.Name}",
                Kind = CompletionItemKind.JoinSuggestion,
                Priority = 100 + scopedIndex,
                IconKey = "JoinSuggestion"
            });
        }

        private static void AddHeuristicJoinSuggestions(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            List<ScopedTable> scoped,
            HashSet<string> usedAliases,
            HashSet<string> emittedSuggestions,
            string prefix)
        {
            try
            {
                if (schema == null || scoped == null || scoped.Count == 0)
                    return;

                var allTargets = schema.Tables.Values.Concat(schema.Views.Values);
                for (int scopedIndex = scoped.Count - 1; scopedIndex >= 0; scopedIndex--)
                {
                    var scopedRef = scoped[scopedIndex];
                    foreach (var targetTable in allTargets)
                    {
                        if (targetTable == null || ReferenceEquals(targetTable, scopedRef.Table))
                            continue;
                        if (!MatchesTablePrefix(targetTable, prefix))
                            continue;

                        if (!TryBuildHeuristicJoin(scopedRef, targetTable, out var onClauseTemplate, out var matchDetail))
                            continue;

                        var newAlias = GenerateAlias(targetTable.Name, usedAliases);
                        string onClause = onClauseTemplate.Replace("{alias}", newAlias);
                        string display = $"{GetObjectInsertName(targetTable)} {newAlias} ON {onClause}";
                        string suggestionKey = $"HEUR|{scopedRef.EffectiveAlias}|{targetTable.FullName}|{onClause}";
                        if (!emittedSuggestions.Add(suggestionKey))
                            continue;
                        results.Add(new CompletionItemData
                        {
                            Text = display,
                            InsertText = display,
                            Description = $"Heuristic join on {matchDetail}",
                            Kind = CompletionItemKind.JoinSuggestion,
                            Priority = 60 + scopedIndex,
                            IconKey = "JoinSuggestion"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddHeuristicJoinSuggestions failed", ex);
            }
        }

        private static bool TryBuildHeuristicJoin(
            ScopedTable scopedRef,
            TableInfo targetTable,
            out string onClause,
            out string matchDetail)
        {
            onClause = null;
            matchDetail = null;

            if (scopedRef.Table?.Columns == null || targetTable?.Columns == null)
                return false;

            var targetKeyColumns = GetLikelyJoinKeyColumns(targetTable).ToList();
            var scopedKeyColumns = GetLikelyJoinKeyColumns(scopedRef.Table).ToList();
            if (targetKeyColumns.Count == 0 || scopedKeyColumns.Count == 0)
                return false;

            var preferredTargetKeys = GetPreferredTargetJoinKeys(targetTable).ToList();
            foreach (var targetKey in preferredTargetKeys)
            {
                string normalizedTargetKey = NormalizeIdentifier(targetKey.Name);
                foreach (var scopedColumn in scopedRef.Table.Columns.OrderBy(column => column.OrdinalPosition))
                {
                    if (!string.Equals(NormalizeIdentifier(scopedColumn.Name), normalizedTargetKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    onClause = $"{scopedRef.EffectiveAlias}.{scopedColumn.Name} = {{alias}}.{targetKey.Name}";
                    matchDetail = scopedColumn.Name;
                    return true;
                }
            }

            foreach (var scopedColumn in scopedRef.Table.Columns)
            {
                if (!TryGetIdBaseName(scopedColumn.Name, out var scopedBase))
                    continue;

                if (MatchesTableNameVariant(targetTable.Name, scopedBase))
                {
                    var targetKey = FindBestTargetKey(targetKeyColumns, scopedColumn, scopedBase);
                    if (targetKey != null)
                    {
                        onClause = $"{scopedRef.EffectiveAlias}.{scopedColumn.Name} = {{alias}}.{targetKey.Name}";
                        matchDetail = scopedColumn.Name;
                        return true;
                    }
                }
            }

            foreach (var targetColumn in targetTable.Columns)
            {
                if (!TryGetIdBaseName(targetColumn.Name, out var targetBase))
                    continue;

                if (MatchesTableNameVariant(scopedRef.Table.Name, targetBase))
                {
                    var scopedKey = FindBestTargetKey(scopedKeyColumns, targetColumn, targetBase);
                    if (scopedKey != null)
                    {
                        onClause = $"{{alias}}.{targetColumn.Name} = {scopedRef.EffectiveAlias}.{scopedKey.Name}";
                        matchDetail = targetColumn.Name;
                        return true;
                    }
                }
            }

            foreach (var scopedColumn in scopedRef.Table.Columns)
            {
                if (!LooksLikeJoinId(scopedColumn.Name))
                    continue;

                var targetColumn = targetTable.Columns.FirstOrDefault(column =>
                    string.Equals(NormalizeIdentifier(column.Name), NormalizeIdentifier(scopedColumn.Name), StringComparison.OrdinalIgnoreCase));
                if (targetColumn == null)
                    continue;

                onClause = $"{scopedRef.EffectiveAlias}.{scopedColumn.Name} = {{alias}}.{targetColumn.Name}";
                matchDetail = scopedColumn.Name;
                return true;
            }

            return false;
        }

        private static IEnumerable<ColumnInfo> GetLikelyJoinKeyColumns(TableInfo table)
        {
            return table.Columns.Where(column =>
                column.IsPrimaryKey ||
                column.IsIdentity ||
                string.Equals(NormalizeIdentifier(column.Name), "id", StringComparison.OrdinalIgnoreCase) ||
                LooksLikeJoinId(column.Name));
        }

        private static IEnumerable<ColumnInfo> GetPreferredTargetJoinKeys(TableInfo table)
        {
            if (table?.Columns == null)
                yield break;

            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var preferredNames = GetPreferredJoinKeyNames(table).ToList();
            foreach (var preferredName in preferredNames)
            {
                var key = table.Columns
                    .OrderBy(column => column.OrdinalPosition)
                    .FirstOrDefault(column =>
                        string.Equals(NormalizeIdentifier(column.Name), preferredName, StringComparison.OrdinalIgnoreCase));
                if (key != null && emitted.Add(key.Name))
                    yield return key;
            }
        }

        private static ColumnInfo FindBestTargetKey(IEnumerable<ColumnInfo> candidateKeys, ColumnInfo sourceColumn, string sourceBase)
        {
            var normalizedSource = NormalizeIdentifier(sourceColumn.Name);
            foreach (var candidate in candidateKeys)
            {
                if (string.Equals(NormalizeIdentifier(candidate.Name), normalizedSource, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            foreach (var candidate in candidateKeys)
            {
                if (string.Equals(NormalizeIdentifier(candidate.Name), "id", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            foreach (var candidate in candidateKeys)
            {
                if (TryGetIdBaseName(candidate.Name, out var candidateBase) &&
                    string.Equals(candidateBase, sourceBase, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return candidateKeys.FirstOrDefault();
        }

        private static bool MatchesTableNameVariant(string tableName, string baseName)
        {
            if (string.IsNullOrEmpty(tableName) || string.IsNullOrEmpty(baseName))
                return false;

            return GetTableNameVariants(tableName).Any(variant =>
                string.Equals(variant, baseName, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> GetTableNameVariants(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                yield break;

            var normalizedFull = NormalizeIdentifier(tableName);
            if (!string.IsNullOrEmpty(normalizedFull))
                yield return normalizedFull;

            var underscoreTokens = tableName
                .Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeIdentifier)
                .Where(token => !string.IsNullOrEmpty(token))
                .ToArray();

            if (underscoreTokens.Length > 1)
            {
                for (int i = 1; i < underscoreTokens.Length; i++)
                {
                    yield return string.Concat(underscoreTokens.Skip(i));
                }
            }
        }

        private static bool TryGetIdBaseName(string columnName, out string baseName)
        {
            baseName = null;
            if (!LooksLikeJoinId(columnName))
                return false;

            var normalized = NormalizeIdentifier(columnName);
            if (normalized.EndsWith("id", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 2);

            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            baseName = normalized;
            return true;
        }

        private static bool LooksLikeJoinId(string columnName)
        {
            if (string.IsNullOrWhiteSpace(columnName))
                return false;

            var normalized = NormalizeIdentifier(columnName);
            return normalized.Length > 2 &&
                normalized.EndsWith("id", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
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

        private static string GetCompletedJoinScopeText(string fullText, int caretOffset)
        {
            if (string.IsNullOrEmpty(fullText) || caretOffset <= 0)
                return string.Empty;

            int limit = Math.Min(Math.Max(caretOffset, 0), fullText.Length);
            var beforeCaret = fullText.Substring(0, limit);
            var matches = JoinKeywordPattern.Matches(beforeCaret);
            if (matches.Count == 0)
                return beforeCaret;

            var currentJoin = matches[matches.Count - 1];
            return beforeCaret.Substring(0, currentJoin.Index);
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
        /// <paramref name="usedAliases"/>. For underscore-delimited names, uses the first
        /// letter of each token (e.g. TT_NOITRU_BENHAN -> tnb). Otherwise uses CamelCase /
        /// PascalCase initials (e.g. BenhAn -> ba). Falls back to a numeric suffix on collision.
        /// </summary>
        private static string GenerateAlias(string tableName, HashSet<string> usedAliases)
        {
            if (string.IsNullOrEmpty(tableName))
                return "t";

            var sb = new StringBuilder();

            if (tableName.IndexOf('_') >= 0)
            {
                var tokens = tableName.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in tokens)
                {
                    if (!string.IsNullOrEmpty(token))
                        sb.Append(char.ToLowerInvariant(token[0]));
                }
            }
            else
            {
                for (int i = 0; i < tableName.Length; i++)
                {
                    char c = tableName[i];
                    if (!char.IsLetterOrDigit(c))
                        continue;

                    bool isWordStart =
                        i == 0 ||
                        !char.IsLetterOrDigit(tableName[i - 1]) ||
                        (char.IsUpper(c) && char.IsLetter(tableName[i - 1]) && char.IsLower(tableName[i - 1]));

                    if (isWordStart)
                        sb.Append(char.ToLowerInvariant(c));
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

        private static string GetObjectInsertName(TableInfo table)
        {
            if (table == null)
                return null;

            var settings = SettingsProvider.GetSettings();
            if (settings.OmitDboSchemaOnInsert)
                return table.Name;

            return table.BracketedName;
        }

        /// <summary>
        /// Finds tables/views referenced in the text.
        /// </summary>
        private static HashSet<string> GetReferencedTables(string fullText, DatabaseSchema schema)
        {
            return new HashSet<string>(
                GetReferencedTablesInOrder(fullText, schema),
                StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> GetReferencedTablesInOrder(string fullText, DatabaseSchema schema)
        {
            var referenced = new List<string>();
            if (string.IsNullOrEmpty(fullText) || schema == null)
                return referenced;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scoped in ResolveScopedTables(fullText, schema))
            {
                if (scoped.Table != null && seen.Add(scoped.Table.FullName))
                    referenced.Add(scoped.Table.FullName);
            }

            if (referenced.Count > 0)
                return referenced;

            var words = Regex.Split(fullText, @"[^\w]");
            foreach (var word in words)
            {
                if (string.IsNullOrEmpty(word))
                    continue;

                if (schema.Tables.ContainsKey(word) && seen.Add(word))
                    referenced.Add(word);
                else if (schema.Views.ContainsKey(word) && seen.Add(word))
                    referenced.Add(word);
                else
                {
                    var table = ResolveTable(schema, null, word);
                    if (table != null && seen.Add(table.FullName))
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
            int caretOffset,
            SqlContext context)
        {
            try
            {
                // Check if we are in a dot context (e.g. "u.Name")
                var tableContext = SqlContextParser.GetTableContext(fullText, caretOffset);
                if (tableContext != null)
                {
                    string targetTable = !string.IsNullOrEmpty(tableContext.ResolvedTable)
                        ? tableContext.ResolvedTable
                        : ResolveTableNameFromCurrentStatement(schema, fullText, caretOffset, tableContext.AliasOrTable)
                            ?? tableContext.AliasOrTable;

                    var cols = schema.GetColumnsForTable(targetTable);
                    if (cols != null)
                    {
                        foreach (var col in cols)
                        {
                            if (MatchesColumnPrefix(col.Text, prefix))
                            {
                                results.Add(col);
                            }
                        }
                }
                    return;
                }

                if (context == SqlContext.SelectClause)
                {
                    AddSelectClauseColumns(results, schema, prefix, fullText, caretOffset);
                    AddSelectClauseFunctions(results, schema, prefix);
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
                                if (MatchesColumnPrefix(col.Text, prefix))
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
                            if (MatchesColumnPrefix(col.Name, prefix))
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

        private static void AddSelectClauseColumns(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string prefix,
            string fullText,
            int caretOffset)
        {
            var statementScope = GetCurrentStatementScope(fullText, caretOffset);
            var statementText = statementScope.text;
            int statementCaretOffset = statementScope.caretOffset;

            var referencedTables = GetReferencedTablesInOrder(statementText, schema);
            if (referencedTables.Count == 0)
            {
                AddAllColumnsFallback(results, schema, prefix);
                return;
            }

            var selectedColumns = GetPreviouslySelectedColumns(statementText, statementCaretOffset);
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tableName in referencedTables)
            {
                var table = ResolveTableByAnyName(schema, tableName);
                if (table?.Columns == null)
                    continue;

                foreach (var col in table.Columns.OrderBy(c => c.OrdinalPosition))
                {
                    if (!MatchesColumnPrefix(col.Name, prefix))
                        continue;
                    if (selectedColumns.Contains(col.Name))
                        continue;
                    if (!emitted.Add(col.Name))
                        continue;

                    results.Add(new CompletionItemData
                    {
                        Text = col.Name,
                        InsertText = col.Name,
                        Description = col.DisplayText,
                        Kind = CompletionItemKind.Column,
                        Priority = 5,
                        SortOrder = results.Count,
                        IconKey = "Column"
                    });
                }
            }

            if (results.Count == 0)
                AddAllColumnsFallback(results, schema, prefix);
        }

        private static void AddAllColumnsFallback(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string prefix)
        {
            foreach (var table in schema.Tables.Values)
            {
                foreach (var col in table.Columns.OrderBy(c => c.OrdinalPosition))
                {
                    if (MatchesColumnPrefix(col.Name, prefix))
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

        private static void AddSelectClauseFunctions(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string prefix)
        {
            if (schema?.Procedures == null)
                return;

            foreach (var proc in schema.Procedures.Values)
            {
                if (proc == null || !proc.IsFunction)
                    continue;
                if (!MatchesPrefix(proc.FullName, prefix) && !MatchesPrefix(proc.Name, prefix))
                    continue;

                string insertText = proc.Name + "()";
                string description = proc.Parameters.Count > 0
                    ? $"{proc.ObjectType}: {proc.Signature}"
                    : $"{proc.ObjectType}: {proc.FullName}()";

                results.Add(new CompletionItemData
                {
                    Text = proc.FullName,
                    InsertText = insertText,
                    Description = description,
                    Kind = CompletionItemKind.Function,
                    Priority = 4,
                    IconKey = "Function"
                });
            }
        }

        private static TableInfo ResolveTableByAnyName(DatabaseSchema schema, string tableName)
        {
            if (schema == null || string.IsNullOrWhiteSpace(tableName))
                return null;

            if (schema.Tables.TryGetValue(tableName, out var table))
                return table;
            if (schema.Views.TryGetValue(tableName, out table))
                return table;

            return ResolveTable(schema, null, tableName);
        }

        private static string ResolveTableNameFromCurrentStatement(
            DatabaseSchema schema,
            string fullText,
            int caretOffset,
            string aliasOrTable)
        {
            if (schema == null || string.IsNullOrWhiteSpace(aliasOrTable))
                return null;

            var statementScope = GetCurrentStatementScope(fullText, caretOffset).text;
            foreach (var scoped in ResolveScopedTables(statementScope, schema))
            {
                if (string.Equals(scoped.EffectiveAlias, aliasOrTable, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scoped.Table?.Name, aliasOrTable, StringComparison.OrdinalIgnoreCase))
                {
                    return scoped.Table?.FullName;
                }
            }

            return null;
        }

        private static HashSet<string> GetPreviouslySelectedColumns(string fullText, int caretOffset)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(fullText) || caretOffset <= 0)
                return selected;

            string beforeCaret = fullText.Substring(0, Math.Min(caretOffset, fullText.Length));
            var selectMatch = Regex.Match(
                beforeCaret,
                @"\bSELECT\b(?<body>[\s\S]*)$",
                RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
            if (!selectMatch.Success)
                return selected;

            string selectBody = selectMatch.Groups["body"].Value;
            int fromIndex = IndexOfTopLevelFrom(selectBody);
            if (fromIndex >= 0)
                selectBody = selectBody.Substring(0, fromIndex);

            foreach (string rawSegment in selectBody.Split(','))
            {
                string segment = rawSegment.Trim();
                if (string.IsNullOrEmpty(segment))
                    continue;

                segment = Regex.Replace(segment, @"\s+AS\s+\[?\w+\]?\s*$", string.Empty, RegexOptions.IgnoreCase);
                segment = Regex.Replace(segment, @"\s+\[?\w+\]?\s*$", string.Empty, RegexOptions.IgnoreCase);

                var qualified = Regex.Match(segment, @"(?:\[?\w+\]?\.)?\[?(?<col>\w+)\]?$", RegexOptions.IgnoreCase);
                if (qualified.Success)
                    selected.Add(qualified.Groups["col"].Value);
            }

            return selected;
        }

        private static (string text, int caretOffset) GetCurrentStatementScope(string fullText, int caretOffset)
        {
            if (string.IsNullOrEmpty(fullText))
                return (string.Empty, 0);

            int safeCaret = Math.Min(Math.Max(caretOffset, 0), fullText.Length);
            int start = FindStatementStart(fullText, safeCaret);
            int end = FindStatementEnd(fullText, safeCaret);

            if (end < start)
                end = fullText.Length;

            string text = fullText.Substring(start, end - start);
            return (text, safeCaret - start);
        }

        private static int FindStatementStart(string text, int caretOffset)
        {
            int start = 0;

            for (int i = 0; i < caretOffset; i++)
            {
                if (text[i] == ';')
                    start = i + 1;
            }

            var beforeCaret = text.Substring(0, caretOffset);
            foreach (Match match in Regex.Matches(beforeCaret, @"(?im)^[ \t]*GO(?:[ \t]+--.*)?[ \t]*\r?$"))
            {
                start = Math.Max(start, match.Index + match.Length);
            }

            return start;
        }

        private static int FindStatementEnd(string text, int caretOffset)
        {
            for (int i = caretOffset; i < text.Length; i++)
            {
                if (text[i] == ';')
                    return i;
            }

            var afterCaret = text.Substring(caretOffset);
            var goMatch = Regex.Match(afterCaret, @"(?im)^[ \t]*GO(?:[ \t]+--.*)?[ \t]*\r?$", RegexOptions.Multiline);
            return goMatch.Success ? caretOffset + goMatch.Index : text.Length;
        }

        private static int IndexOfTopLevelFrom(string text)
        {
            if (string.IsNullOrEmpty(text))
                return -1;

            int depth = 0;
            for (int i = 0; i < text.Length - 3; i++)
            {
                char c = text[i];
                if (c == '(')
                {
                    depth++;
                    continue;
                }

                if (c == ')')
                {
                    if (depth > 0)
                        depth--;
                    continue;
                }

                if (depth > 0)
                    continue;

                if ((i == 0 || !IsIdentifierChar(text[i - 1])) &&
                    i + 4 <= text.Length &&
                    string.Equals(text.Substring(i, 4), "FROM", StringComparison.OrdinalIgnoreCase) &&
                    (i + 4 == text.Length || !IsIdentifierChar(text[i + 4])))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#';
        }

        private static void AddScopedAliasSuggestions(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string prefix,
            string fullText,
            int caretOffset,
            bool prioritizeCurrentJoinTarget,
            bool includeTablesAfterCaret)
        {
            try
            {
                string scopeText;
                if (includeTablesAfterCaret)
                {
                    scopeText = GetCurrentStatementScope(fullText, caretOffset).text;
                }
                else
                {
                    scopeText = fullText?.Substring(0, Math.Min(Math.Max(caretOffset, 0), fullText?.Length ?? 0)) ?? string.Empty;
                }

                var scoped = ResolveScopedTables(scopeText, schema);
                if (scoped.Count == 0)
                    return;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int lastIndex = scoped.Count - 1;
                int start = prioritizeCurrentJoinTarget ? lastIndex : 0;
                int end = prioritizeCurrentJoinTarget ? -1 : scoped.Count;
                int step = prioritizeCurrentJoinTarget ? -1 : 1;

                for (int i = start; i != end; i += step)
                {
                    var scopedRef = scoped[i];
                    string alias = scopedRef.EffectiveAlias;
                    if (string.IsNullOrWhiteSpace(alias) || !seen.Add(alias))
                        continue;

                    if (!MatchesOnClauseAliasPrefix(scopedRef, prefix))
                        continue;

                    bool isCurrentJoinTarget = prioritizeCurrentJoinTarget && i == lastIndex;
                    bool isPrimaryFromTable = !prioritizeCurrentJoinTarget && i == 0;
                    string insertText = alias + ".";
                    string tableName = GetObjectInsertName(scopedRef.Table);

                    results.Add(new CompletionItemData
                    {
                        Text = insertText,
                        InsertText = insertText,
                        Description = isCurrentJoinTarget
                            ? $"Alias cua bang vua JOIN: {tableName}"
                            : isPrimaryFromTable
                                ? $"Alias cua bang chinh: {tableName}"
                            : $"Alias trong scope: {tableName}",
                        Kind = CompletionItemKind.Alias,
                        Priority = isCurrentJoinTarget ? 160 : (isPrimaryFromTable ? 150 : 140),
                        SortOrder = i,
                        IconKey = "Alias"
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddScopedAliasSuggestions failed", ex);
            }
        }

        private static void AddOnClauseJoinColumnSuggestions(
            List<CompletionItemData> results,
            DatabaseSchema schema,
            string prefix,
            string fullText,
            int caretOffset)
        {
            try
            {
                string scopeText = fullText?.Substring(0, Math.Min(Math.Max(caretOffset, 0), fullText?.Length ?? 0)) ?? string.Empty;
                var scoped = ResolveScopedTables(scopeText, schema);
                if (scoped.Count < 2)
                    return;

                var currentJoinTarget = scoped[scoped.Count - 1];
                if (currentJoinTarget.Table?.Columns == null)
                    return;

                var preferredKeys = GetPreferredJoinKeyNames(currentJoinTarget.Table).ToList();
                if (preferredKeys.Count == 0)
                    return;

                string preferredTargetDisplay = GetPreferredJoinKeyDisplayName(currentJoinTarget.Table);
                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < scoped.Count - 1; i++)
                {
                    var scopedRef = scoped[i];
                    if (scopedRef.Table?.Columns == null || string.IsNullOrWhiteSpace(scopedRef.EffectiveAlias))
                        continue;

                    foreach (var candidate in scopedRef.Table.Columns
                        .Select(column => new
                        {
                            Column = column,
                            Rank = GetOnClauseJoinColumnRank(currentJoinTarget.Table, column, preferredKeys)
                        })
                        .Where(x => x.Rank >= 0)
                        .OrderBy(x => x.Rank)
                        .ThenBy(x => x.Column.OrdinalPosition))
                    {
                        string qualifiedName = $"{scopedRef.EffectiveAlias}.{candidate.Column.Name}";
                        if (!MatchesOnClauseJoinColumnPrefix(scopedRef.EffectiveAlias, candidate.Column.Name, prefix))
                            continue;
                        if (!emitted.Add(qualifiedName))
                            continue;

                        results.Add(new CompletionItemData
                        {
                            Text = qualifiedName,
                            InsertText = qualifiedName,
                            Description = $"Join voi {currentJoinTarget.EffectiveAlias}.{preferredTargetDisplay}",
                            Kind = CompletionItemKind.Column,
                            Priority = 130 - candidate.Rank,
                            SortOrder = (i * 1000) + candidate.Column.OrdinalPosition,
                            IconKey = "Column"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddOnClauseJoinColumnSuggestions failed", ex);
            }
        }

        private static IEnumerable<string> GetPreferredJoinKeyNames(TableInfo table)
        {
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var variant in GetTableNameVariants(table?.Name))
            {
                var logicalId = NormalizeIdentifier(variant + "id");
                if (!string.IsNullOrEmpty(logicalId) && emitted.Add(logicalId))
                    yield return logicalId;
            }

            if (table?.PrimaryKeyColumns != null)
            {
                foreach (var column in table.PrimaryKeyColumns.OrderBy(c => c.OrdinalPosition))
                {
                    var normalized = NormalizeIdentifier(column.Name);
                    if (!string.IsNullOrEmpty(normalized) && emitted.Add(normalized))
                        yield return normalized;
                }
            }

            if (table?.Columns != null)
            {
                foreach (var column in GetLikelyJoinKeyColumns(table).OrderBy(c => c.OrdinalPosition))
                {
                    var normalized = NormalizeIdentifier(column.Name);
                    if (!string.IsNullOrEmpty(normalized) && emitted.Add(normalized))
                        yield return normalized;
                }
            }
        }

        private static string GetPreferredJoinKeyDisplayName(TableInfo table)
        {
            if (table == null)
                return "Id";

            foreach (var variant in GetTableNameVariants(table.Name))
            {
                if (!string.IsNullOrWhiteSpace(variant))
                    return variant + "_Id";
            }

            var pk = table.PrimaryKeyColumns.OrderBy(c => c.OrdinalPosition).FirstOrDefault();
            if (pk != null)
                return pk.Name;

            return table.Columns
                .OrderBy(c => c.OrdinalPosition)
                .FirstOrDefault(c => LooksLikeJoinId(c.Name) || c.IsPrimaryKey || c.IsIdentity)?.Name
                ?? "Id";
        }

        private static int GetOnClauseJoinColumnRank(
            TableInfo currentJoinTarget,
            ColumnInfo candidateColumn,
            List<string> preferredKeys)
        {
            if (currentJoinTarget == null || candidateColumn == null)
                return -1;

            string normalizedCandidate = NormalizeIdentifier(candidateColumn.Name);
            if (string.IsNullOrEmpty(normalizedCandidate))
                return -1;

            string logicalTableId = preferredKeys.FirstOrDefault();
            if (!string.IsNullOrEmpty(logicalTableId) &&
                string.Equals(normalizedCandidate, logicalTableId, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int exactKeyIndex = preferredKeys.FindIndex(key =>
                string.Equals(key, normalizedCandidate, StringComparison.OrdinalIgnoreCase));
            if (exactKeyIndex >= 0)
                return 1 + exactKeyIndex;

            if (TryGetIdBaseName(candidateColumn.Name, out var candidateBase) &&
                MatchesTableNameVariant(currentJoinTarget.Name, candidateBase))
            {
                return 20;
            }

            return -1;
        }

        private static bool MatchesOnClauseJoinColumnPrefix(string alias, string columnName, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                return true;

            string qualified = $"{alias}.{columnName}";
            return MatchesPrefix(alias, prefix) ||
                   MatchesPrefix(qualified, prefix) ||
                   MatchesColumnPrefix(columnName, prefix);
        }

        private static bool MatchesOnClauseAliasPrefix(ScopedTable scopedRef, string prefix)
        {
            if (scopedRef.Table == null)
                return false;

            if (string.IsNullOrEmpty(prefix))
                return true;

            return MatchesPrefix(scopedRef.EffectiveAlias, prefix) ||
                   MatchesPrefix(scopedRef.Table.Name, prefix) ||
                   MatchesPrefix(scopedRef.Table.FullName, prefix);
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

                if (ShouldUseFuzzyFallback(prefix, results.Count))
                {
                    foreach (var proc in schema.Procedures.Values)
                    {
                        if (!IsFuzzyProcedureMatch(proc, prefix))
                            continue;

                        var kind = proc.ObjectType != null && proc.ObjectType.Contains("FUNCTION")
                            ? CompletionItemKind.Function
                            : CompletionItemKind.Procedure;
                        var label = kind == CompletionItemKind.Function ? "Function" : "Procedure";

                        results.Add(new CompletionItemData
                        {
                            Text = proc.FullName,
                            InsertText = proc.Name,
                            Description = $"{label}: {proc.FullName}",
                            Kind = kind,
                            Priority = 1,
                            IconKey = label
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddProcedures failed", ex);
            }
        }

        private static bool IsFuzzyProcedureMatch(ProcedureInfo proc, string prefix)
        {
            if (proc == null)
                return false;

            return IsFuzzyMatch(proc.FullName, prefix) ||
                   IsFuzzyMatch(proc.Name, prefix);
        }

        /// <summary>
        /// True when the context warrants opening the popup with no prefix typed —
        /// i.e. the user has just hit space after a clause keyword and we have a
        /// useful metadata-driven list to show (tables, columns, procedures, or databases).
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
                case SqlContext.UseDatabase:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsTableObjectContext(
            SqlContext context,
            string fullText,
            int caretOffset,
            string prefix)
        {
            switch (context)
            {
                case SqlContext.FromClause:
                case SqlContext.JoinClause:
                case SqlContext.UpdateTarget:
                    return IsAtObjectNamePosition(fullText, caretOffset, prefix);
                case SqlContext.InsertColumns:
                    return ParenDepthAfterContextKeyword(fullText, caretOffset) == 0 &&
                           IsAtObjectNamePosition(fullText, caretOffset, prefix);
                default:
                    return false;
            }
        }

        private static readonly Regex ObjectPositionKeywordPattern = new Regex(
            @"\b(?:FROM|JOIN|APPLY|UPDATE|INSERT\s+INTO)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsAtObjectNamePosition(string fullText, int caretOffset, string prefix)
        {
            if (string.IsNullOrEmpty(fullText))
                return true;

            int prefixLength = string.IsNullOrEmpty(prefix) ? 0 : prefix.Length;
            int limit = Math.Min(Math.Max(caretOffset - prefixLength, 0), fullText.Length);
            string beforePrefix = fullText.Substring(0, limit);

            var matches = ObjectPositionKeywordPattern.Matches(beforePrefix);
            if (matches.Count == 0)
                return true;

            var keyword = matches[matches.Count - 1];
            string afterKeyword = beforePrefix.Substring(keyword.Index + keyword.Length);
            string trimmed = afterKeyword.TrimEnd();

            if (trimmed.Length == 0)
                return true;

            char last = trimmed[trimmed.Length - 1];
            return last == ',' || last == '.' || last == '[';
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

            // Token-aware match for names like dbo.TT_TIEPNHAN or [dbo].[TT_TIEPNHAN].
            // This lets users search by the meaningful part without typing technical prefixes.
            foreach (var term in GetSearchTerms(name))
            {
                if (term.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Substring match for flexibility
            if (name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private static bool ShouldUseFuzzyFallback(string prefix, int directResultsCount)
        {
            return !string.IsNullOrWhiteSpace(prefix) &&
                   prefix.Length >= MinFuzzyPrefixLength &&
                   directResultsCount < MinDirectResultsBeforeFuzzy;
        }

        private static IEnumerable<string> GetSearchTerms(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                yield break;

            foreach (var part in name.Split(new[] { '.', '_', '[', ']', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return part;
            }
        }

        private static bool IsFuzzyMatch(string name, string prefix)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prefix))
                return false;

            foreach (var term in GetSearchTerms(name))
            {
                if (IsFuzzyTokenMatch(term, prefix))
                    return true;
            }

            return false;
        }

        private static bool IsFuzzyTokenMatch(string candidate, string prefix)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(prefix))
                return false;

            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

            var normalizedCandidate = candidate.ToLowerInvariant();
            var normalizedPrefix = prefix.ToLowerInvariant();

            if (normalizedCandidate.Length < MinFuzzyPrefixLength ||
                normalizedPrefix.Length < MinFuzzyPrefixLength)
            {
                return false;
            }

            var candidateSliceLength = Math.Min(normalizedCandidate.Length, normalizedPrefix.Length + 1);
            var candidateSlice = normalizedCandidate.Substring(0, candidateSliceLength);
            var maxDistance = normalizedPrefix.Length >= 7 ? 2 : 1;

            return GetEditDistance(candidateSlice, normalizedPrefix, maxDistance) <= maxDistance;
        }

        private static int GetEditDistance(string left, string right, int maxDistance)
        {
            if (string.IsNullOrEmpty(left))
                return string.IsNullOrEmpty(right) ? 0 : right.Length;
            if (string.IsNullOrEmpty(right))
                return left.Length;

            if (Math.Abs(left.Length - right.Length) > maxDistance)
                return maxDistance + 1;

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            var previousPrevious = new int[right.Length + 1];

            for (int j = 0; j <= right.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                int rowMin = current[0];

                for (int j = 1; j <= right.Length; j++)
                {
                    int substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                    int value = Math.Min(
                        Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + substitutionCost);

                    if (i > 1 && j > 1 &&
                        left[i - 1] == right[j - 2] &&
                        left[i - 2] == right[j - 1])
                    {
                        value = Math.Min(value, previousPrevious[j - 2] + 1);
                    }

                    current[j] = value;
                    if (value < rowMin)
                        rowMin = value;
                }

                if (rowMin > maxDistance)
                    return maxDistance + 1;

                var temp = previousPrevious;
                previousPrevious = previous;
                previous = current;
                current = temp;
            }

            return previous[right.Length];
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
                .OrderByDescending(item => GetPrefixRelevance(item, prefix))
                .ThenByDescending(item => item.IsFavorite)
                .ThenByDescending(item => item.UsageScore)
                .ThenByDescending(item => item.Priority)
                .ThenBy(item => item.SortOrder ?? int.MaxValue)
                .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults)
                .ToList();

            return sorted;
        }

        private static int GetPrefixRelevance(CompletionItemData item, string prefix)
        {
            if (item == null || string.IsNullOrEmpty(prefix))
                return 0;

            var candidates = new[]
            {
                item.Text,
                item.InsertText,
                ExtractObjectName(item.Text),
                ExtractObjectName(item.InsertText)
            };

            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate, prefix, StringComparison.OrdinalIgnoreCase))
                    return 4;
            }

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) &&
                    candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return 3;
                }
            }

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) &&
                    candidate.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return 1;
                }
            }

            return 0;
        }

        private static string ExtractObjectName(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var firstToken = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, 2)[0];
            var lastDot = firstToken.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < firstToken.Length - 1)
                firstToken = firstToken.Substring(lastDot + 1);

            if (firstToken.Length >= 2 && firstToken[0] == '[' && firstToken[firstToken.Length - 1] == ']')
                firstToken = firstToken.Substring(1, firstToken.Length - 2).Replace("]]", "]");

            return firstToken;
        }
    }
}
