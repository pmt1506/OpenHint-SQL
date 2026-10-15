using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
                if (SqlContextParser.IsInsideQuotedText(fullText, caretOffset) &&
                    !IsQuotedSqlCompletionAllowed(fullText, caretOffset))
                    return new List<CompletionItemData>();

                var completionScope = GetCompletionScope(fullText, caretOffset);

                // Parse the SQL context at the caret position
                var context = SqlContextParser.GetContext(fullText, caretOffset);
                bool emptyPrefix = string.IsNullOrEmpty(prefix);
                bool variablePrefix = !emptyPrefix && prefix[0] == '@';
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
                    AddSchemaMatches(results, prefix, context, completionScope.text, completionScope.caretOffset, server, database, connectionString);
                    return SortAndLimit(results, prefix);
                }

                // Keyword/snippet matches are noise when the user hasn't typed a prefix —
                // in that case only schema items make sense.
                if (!emptyPrefix)
                {
                    AddKeywordMatches(results, prefix);
                    if (!variablePrefix)
                        AddSnippetMatches(results, prefix);
                }

                if (variablePrefix || context == SqlContext.DeclareVariable || context == SqlContext.Exec)
                {
                    AddVariableMatches(results, prefix, completionScope.text, completionScope.caretOffset);
                }

                if (variablePrefix)
                    return SortAndLimit(results, prefix);

                AddSchemaMatches(results, prefix, context, completionScope.text, completionScope.caretOffset, server, database, connectionString);

                return SortAndLimit(results, prefix);
            }
            catch (Exception ex)
            {
                Logger.Error("CompletionEngine.GetCompletionItems failed", ex);
                return new List<CompletionItemData>();
            }
        }

        /// <summary>
        /// Allows completion inside a quoted SQL fragment only when it appears to be
        /// a dynamic-SQL string inside a BEGIN/END branch, typically after EXEC.
        /// This keeps ordinary string literals silent while still supporting
        /// suggestions for text being assembled with EXEC('...').
        /// </summary>
        private static bool IsQuotedSqlCompletionAllowed(string fullText, int caretOffset)
        {
            if (string.IsNullOrEmpty(fullText) || caretOffset <= 0)
                return false;

            var statementScope = GetCurrentStatementScope(fullText, caretOffset);
            if (string.IsNullOrWhiteSpace(statementScope.text))
                return false;

            int branchStart = FindCurrentBranchStart(statementScope.text, statementScope.caretOffset);
            if (branchStart <= 0)
                return false;

            int quoteStart = FindCurrentSingleQuoteStart(statementScope.text, statementScope.caretOffset);
            if (quoteStart < 0 || quoteStart <= branchStart)
                return false;

            return ContainsDynamicSqlKeyword(statementScope.text, branchStart, quoteStart);
        }

        /// <summary>
        /// Finds the opening single quote for the literal that currently contains the caret.
        /// Returns -1 when the caret is not inside a single-quoted string.
        /// </summary>
        private static int FindCurrentSingleQuoteStart(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text) || caretOffset <= 0)
                return -1;

            int limit = Math.Min(Math.Max(caretOffset, 0), text.Length);
            bool inSingleQuote = false;
            bool inBacktick = false;
            int quoteStart = -1;

            for (int i = 0; i < limit; i++)
            {
                char c = text[i];

                if (inSingleQuote)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < limit && text[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        inSingleQuote = false;
                        quoteStart = -1;
                    }

                    continue;
                }

                if (inBacktick)
                {
                    if (c == '`')
                        inBacktick = false;

                    continue;
                }

                if (c == '\'')
                {
                    inSingleQuote = true;
                    quoteStart = i;
                    continue;
                }

                if (c == '`')
                    inBacktick = true;
            }

            return inSingleQuote ? quoteStart : -1;
        }

        /// <summary>
        /// Checks whether a dynamic-SQL keyword such as EXEC/EXECUTE appears in the
        /// non-quoted text between the current branch start and the opening quote.
        /// </summary>
        private static bool ContainsDynamicSqlKeyword(string text, int branchStart, int quoteStart)
        {
            if (string.IsNullOrEmpty(text) || branchStart < 0 || quoteStart <= branchStart)
                return false;

            bool inSingleQuote = false;
            bool inBacktick = false;

            for (int i = Math.Max(branchStart, 0); i < quoteStart; i++)
            {
                char c = text[i];

                if (inSingleQuote)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < quoteStart && text[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        inSingleQuote = false;
                    }

                    continue;
                }

                if (inBacktick)
                {
                    if (c == '`')
                        inBacktick = false;

                    continue;
                }

                if (c == '\'')
                {
                    inSingleQuote = true;
                    continue;
                }

                if (c == '`')
                {
                    inBacktick = true;
                    continue;
                }

                if (!char.IsLetter(c))
                    continue;

                if (IsKeywordAt(text, i, "EXEC") || IsKeywordAt(text, i, "EXECUTE"))
                    return true;
            }

            return false;
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
            string scopeText,
            int scopeCaretOffset,
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
                if (SqlContextParser.GetTableContext(scopeText, scopeCaretOffset) != null)
                {
                    AddColumns(results, schema, prefix, scopeText, scopeCaretOffset, context);
                    return;
                }

                switch (context)
                {
                    // ── Table-position contexts ─────────────────────────────────
                    case SqlContext.FromClause:
                        AddTablesAndViews(results, schema, prefix, includeAlias: true, excludeCurrentJoin: false, fullText: scopeText, caretOffset: scopeCaretOffset);
                        break;

                    case SqlContext.UpdateTarget:
                        AddTablesAndViews(results, schema, prefix, includeAlias: false, excludeCurrentJoin: false, fullText: scopeText, caretOffset: scopeCaretOffset);
                        break;

                    case SqlContext.JoinClause:
                        // Table-name position only — join-column suggestions appear
                        // later, in the ON clause (AddOnClauseJoinColumnSuggestions).
                        AddTablesAndViews(results, schema, prefix, includeAlias: true, excludeCurrentJoin: true, fullText: scopeText, caretOffset: scopeCaretOffset);
                        break;

                    case SqlContext.InsertColumns:
                        // After `INSERT INTO `, we're at the table-name position until
                        // an opening paren is typed; once inside `INSERT INTO foo (...)`,
                        // we're in the column-list position. Treat the table-name case
                        // as the only schema-aware branch; columns inside the paren are
                        // left to the dot-context path (typing `foo.` works there).
                        if (ParenDepthAfterContextKeyword(scopeText, scopeCaretOffset) == 0)
                            AddTablesAndViews(results, schema, prefix, includeAlias: false, excludeCurrentJoin: false, fullText: scopeText, caretOffset: scopeCaretOffset);
                        break;

                    // ── Column-position contexts ────────────────────────────────
                    case SqlContext.SelectClause:
                    case SqlContext.WhereClause:
                    case SqlContext.OrderByClause:
                    case SqlContext.GroupByClause:
                    case SqlContext.HavingClause:
                    case SqlContext.SetClause:
                        AddScopedAliasSuggestions(results, schema, prefix, scopeText, scopeCaretOffset, prioritizeCurrentJoinTarget: false, includeTablesAfterCaret: true);
                        AddColumns(results, schema, prefix, scopeText, scopeCaretOffset, context);
                        break;

                    case SqlContext.OnClause:
                        AddScopedAliasSuggestions(results, schema, prefix, scopeText, scopeCaretOffset, prioritizeCurrentJoinTarget: true, includeTablesAfterCaret: false);
                        AddOnClauseJoinConditionSuggestions(results, schema, prefix, scopeText, scopeCaretOffset);
                        AddOnClauseJoinColumnSuggestions(results, schema, prefix, scopeText, scopeCaretOffset);
                        AddColumns(results, schema, prefix, scopeText, scopeCaretOffset, context);
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
            int usageScore = TableUsageProvider.GetUsageScore(CompletionItemKind.Table, t?.FullName);
            return new CompletionItemData
            {
                Text = t.FullName,
                InsertText = insertText,
                Description = $"Table: {insertText}",
                Kind = CompletionItemKind.Table,
                Priority = 10,
                UsageScore = usageScore,
                IsFavorite = TableUsageProvider.IsFavorite(CompletionItemKind.Table, t?.FullName),
                UsageKey = t?.FullName,
                IconKey = "Table"
            };
        }

        private static CompletionItemData BuildColumnItem(
            ColumnInfo col,
            string sourceTableName = null,
            int priority = 70,
            int? sortOrder = null)
        {
            if (col == null)
                return null;

            string usageKey = string.IsNullOrWhiteSpace(sourceTableName)
                ? col.Name
                : $"{sourceTableName}.{col.Name}";

            string description = string.IsNullOrWhiteSpace(sourceTableName)
                ? col.DisplayText
                : $"{col.DisplayText}  [{sourceTableName}]";

            return new CompletionItemData
            {
                Text = col.Name,
                InsertText = col.Name,
                Description = description,
                Kind = CompletionItemKind.Column,
                Priority = priority,
                SortOrder = sortOrder,
                UsageScore = TableUsageProvider.GetUsageScore(CompletionItemKind.Column, usageKey),
                IsFavorite = TableUsageProvider.IsFavorite(CompletionItemKind.Column, usageKey),
                UsageKey = usageKey,
                IconKey = "Column"
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

            var completionScope = GetCompletionScope(fullText, caretOffset);
            if (string.IsNullOrEmpty(completionScope.text))
                return aliases;

            var scopeText = excludeCurrentJoin
                ? GetCompletedJoinScopeText(completionScope.text, completionScope.caretOffset)
                : completionScope.text.Substring(0, completionScope.caretOffset);

            foreach (var scoped in ResolveScopedTables(scopeText, schema))
            {
                if (!string.IsNullOrWhiteSpace(scoped.EffectiveAlias))
                    aliases.Add(scoped.EffectiveAlias);
            }

            return aliases;
        }

        // ───────────────────────────────────────────────
        //  JOIN scope parsing (shared by ON-clause suggestions)
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
            public string EffectiveAlias => !string.IsNullOrEmpty(Alias) ? Alias : Table?.Name;
        }

        private static IEnumerable<ColumnInfo> GetLikelyJoinKeyColumns(TableInfo table)
        {
            return table.Columns.Where(column =>
                column.IsPrimaryKey ||
                column.IsIdentity ||
                string.Equals(NormalizeIdentifier(column.Name), "id", StringComparison.OrdinalIgnoreCase) ||
                LooksLikeJoinId(column.Name));
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
            if (string.IsNullOrWhiteSpace(scopeText) || schema == null)
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = JoinScopePattern.Matches(scopeText);

            foreach (Match m in matches)
            {
                string schemaName = m.Groups[1].Success ? m.Groups[1].Value : null;
                string tableName = m.Groups[2].Value;
                string alias = m.Groups[3].Success ? m.Groups[3].Value : null;

                TableInfo table = ResolveTable(schema, schemaName, tableName);
                if (table == null)
                    continue;

                AddScopedTable(result, seen, new ScopedTable { Table = table, Alias = alias });
            }

            foreach (var alias in GetDerivedTableAliases(scopeText))
            {
                AddScopedTable(result, seen, new ScopedTable { Table = null, Alias = alias });
            }

            return result;
        }

        private static void AddScopedTable(List<ScopedTable> result, HashSet<string> seen, ScopedTable scopedTable)
        {
            string key = (scopedTable.Table?.FullName ?? string.Empty) + "|" + (scopedTable.Alias ?? string.Empty);
            if (!seen.Add(key))
                return;

            result.Add(scopedTable);
        }

        private static IEnumerable<string> GetDerivedTableAliases(string scopeText)
        {
            if (string.IsNullOrWhiteSpace(scopeText))
                yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tokenMatches = Regex.Matches(scopeText, @"\b(?:FROM|JOIN)\b", RegexOptions.IgnoreCase);
            foreach (Match token in tokenMatches)
            {
                int openParen = SkipWhitespace(scopeText, token.Index + token.Length);
                if (openParen >= scopeText.Length || scopeText[openParen] != '(')
                    continue;

                if (!TryReadDerivedTableAlias(scopeText, openParen, out var alias))
                    continue;

                if (seen.Add(alias))
                    yield return alias;
            }
        }

        private static bool TryReadDerivedTableAlias(string text, int openParenIndex, out string alias)
        {
            alias = null;
            if (string.IsNullOrEmpty(text) || openParenIndex < 0 || openParenIndex >= text.Length || text[openParenIndex] != '(')
                return false;

            int depth = 0;
            bool inSingleQuote = false;
            bool inBacktick = false;
            int closeParenIndex = -1;

            for (int i = openParenIndex; i < text.Length; i++)
            {
                char c = text[i];

                if (inSingleQuote)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        inSingleQuote = false;
                    }

                    continue;
                }

                if (inBacktick)
                {
                    if (c == '`')
                        inBacktick = false;

                    continue;
                }

                if (c == '\'')
                {
                    inSingleQuote = true;
                    continue;
                }

                if (c == '`')
                {
                    inBacktick = true;
                    continue;
                }

                if (c == '(')
                {
                    depth++;
                    continue;
                }

                if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeParenIndex = i;
                        break;
                    }
                }
            }

            if (closeParenIndex < 0)
                return false;

            int aliasStart = SkipWhitespace(text, closeParenIndex + 1);
            if (aliasStart >= text.Length)
                return false;

            if (aliasStart + 2 <= text.Length &&
                string.Equals(text.Substring(aliasStart, 2), "AS", StringComparison.OrdinalIgnoreCase) &&
                (aliasStart + 2 == text.Length || !IsIdentifierChar(text[aliasStart + 2])))
            {
                aliasStart = SkipWhitespace(text, aliasStart + 2);
            }

            if (aliasStart >= text.Length)
                return false;

            if (text[aliasStart] == '[')
            {
                int aliasEnd = text.IndexOf(']', aliasStart + 1);
                if (aliasEnd <= aliasStart + 1)
                    return false;

                alias = text.Substring(aliasStart + 1, aliasEnd - aliasStart - 1);
                return !string.IsNullOrWhiteSpace(alias);
            }

            int end = aliasStart;
            while (end < text.Length && IsIdentifierChar(text[end]))
                end++;

            if (end <= aliasStart)
                return false;

            alias = text.Substring(aliasStart, end - aliasStart);
            return !string.IsNullOrWhiteSpace(alias);
        }

        private static int SkipWhitespace(string text, int index)
        {
            int pos = Math.Max(index, 0);
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;
            return pos;
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
            // The cached lookup avoids a full table scan per FROM/JOIN reference on every keystroke.
            var lookup = GetShortNameLookup(schema);
            if (lookup.Tables.TryGetValue(tableName, out var uniqueTable) && uniqueTable != null)
                return uniqueTable;
            if (lookup.Views.TryGetValue(tableName, out var uniqueView) && uniqueView != null)
                return uniqueView;

            return null;
        }

        /// <summary>
        /// Short-name (unqualified) lookup tables. A null value marks an ambiguous
        /// short name shared by multiple objects, mirroring the old "unique match only" scan.
        /// </summary>
        private sealed class ShortNameLookup
        {
            public Dictionary<string, TableInfo> Tables;
            public Dictionary<string, TableInfo> Views;
        }

        /// <summary>
        /// Weak per-schema cache. Schema reloads publish a fresh <see cref="DatabaseSchema"/>
        /// instance, so stale entries fall out with the old object.
        /// </summary>
        private static readonly ConditionalWeakTable<DatabaseSchema, ShortNameLookup> ShortNameLookupBySchema =
            new ConditionalWeakTable<DatabaseSchema, ShortNameLookup>();

        private static ShortNameLookup GetShortNameLookup(DatabaseSchema schema)
            => ShortNameLookupBySchema.GetValue(schema, BuildShortNameLookup);

        private static ShortNameLookup BuildShortNameLookup(DatabaseSchema schema)
        {
            var lookup = new ShortNameLookup
            {
                Tables = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase),
                Views = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase)
            };

            foreach (var table in schema.Tables.Values)
                lookup.Tables[table.Name] = lookup.Tables.ContainsKey(table.Name) ? null : table;
            foreach (var view in schema.Views.Values)
                lookup.Views[view.Name] = lookup.Views.ContainsKey(view.Name) ? null : view;

            return lookup;
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
                        var tableInfo = ResolveTableByAnyName(schema, tableName);
                        if (tableInfo?.Columns == null)
                            continue;

                        foreach (var col in tableInfo.Columns)
                        {
                            if (MatchesColumnPrefix(col.Name, prefix))
                                results.Add(BuildColumnItem(col, tableInfo.FullName));
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
                                    results.Add(BuildColumnItem(col, table.FullName));
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

                    results.Add(BuildColumnItem(col, table.FullName, priority: 5, sortOrder: results.Count));
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
                        results.Add(BuildColumnItem(col, table.FullName));
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

        private static (string text, int caretOffset) GetCompletionScope(string fullText, int caretOffset)
        {
            var statementScope = GetCurrentStatementScope(fullText, caretOffset);
            if (string.IsNullOrWhiteSpace(statementScope.text))
                return statementScope;

            const int branchScopeThreshold = 1200;
            if (statementScope.text.Length < branchScopeThreshold)
                return statementScope;

            int branchStart = FindCurrentBranchStart(statementScope.text, statementScope.caretOffset);
            if (branchStart <= 0)
                return statementScope;

            return (statementScope.text.Substring(branchStart), statementScope.caretOffset - branchStart);
        }

        private static int FindCurrentBranchStart(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int limit = Math.Min(Math.Max(caretOffset, 0), text.Length);
            int parenDepth = 0;
            bool inSingleQuote = false;
            bool inBacktick = false;
            int lastBoundary = 0;

            for (int i = 0; i < limit; i++)
            {
                char c = text[i];

                if (inSingleQuote)
                {
                    if (c == '\'')
                    {
                        if (i + 1 < limit && text[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        inSingleQuote = false;
                    }

                    continue;
                }

                if (inBacktick)
                {
                    if (c == '`')
                        inBacktick = false;

                    continue;
                }

                if (c == '\'')
                {
                    inSingleQuote = true;
                    continue;
                }

                if (c == '`')
                {
                    inBacktick = true;
                    continue;
                }

                if (c == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (c == ')')
                {
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                }

                if (parenDepth > 0 || !char.IsLetter(c))
                    continue;

                if (IsKeywordAt(text, i, "BEGIN") || IsKeywordAt(text, i, "ELSE"))
                    lastBoundary = i;
            }

            return lastBoundary;
        }

        private static bool IsKeywordAt(string text, int index, string keyword)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
                return false;
            if (index < 0 || index + keyword.Length > text.Length)
                return false;
            if (index > 0 && IsIdentifierChar(text[index - 1]))
                return false;
            if (index + keyword.Length < text.Length && IsIdentifierChar(text[index + keyword.Length]))
                return false;

            for (int i = 0; i < keyword.Length; i++)
            {
                if (char.ToUpperInvariant(text[index + i]) != keyword[i])
                    return false;
            }

            return true;
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
                    string tableName = scopedRef.Table != null
                        ? GetObjectInsertName(scopedRef.Table)
                        : alias;

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

        /// <summary>
        /// Suggests complete ON conditions (<c>a.Col = b.Col</c>) between the current
        /// join target and each table already in scope. FK-backed conditions come first;
        /// name-based heuristic pairs follow for databases without declared FKs.
        /// </summary>
        private static void AddOnClauseJoinConditionSuggestions(
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

                var target = scoped[scoped.Count - 1];
                if (target.Table?.Columns == null || string.IsNullOrWhiteSpace(target.EffectiveAlias))
                    return;

                var preferredKeys = GetPreferredJoinKeyNames(target.Table).ToList();
                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < scoped.Count - 1; i++)
                {
                    var scopedRef = scoped[i];
                    if (scopedRef.Table?.Columns == null || string.IsNullOrWhiteSpace(scopedRef.EffectiveAlias))
                        continue;

                    // FK-backed conditions: the exact column pairing declared in the database.
                    foreach (var fk in schema.GetForeignKeysBetween(scopedRef.Table, target.Table))
                    {
                        bool scopedIsParent = ReferenceEquals(fk.ParentTable, scopedRef.Table);
                        string parentAlias = scopedIsParent ? scopedRef.EffectiveAlias : target.EffectiveAlias;
                        string refAlias = scopedIsParent ? target.EffectiveAlias : scopedRef.EffectiveAlias;

                        var condition = new StringBuilder();
                        for (int c = 0; c < fk.ParentColumns.Count && c < fk.ReferencedColumns.Count; c++)
                        {
                            if (c > 0) condition.Append(" AND ");
                            condition.Append(parentAlias).Append('.').Append(fk.ParentColumns[c].Name);
                            condition.Append(" = ");
                            condition.Append(refAlias).Append('.').Append(fk.ReferencedColumns[c].Name);
                        }

                        EmitOnClauseCondition(results, emitted, condition.ToString(), prefix,
                            description: $"FK {fk.Name}", priority: 200, sortOrder: i * 1000);
                    }

                    // Heuristic pairs: scoped columns that look joinable to the target,
                    // paired with the matching target-side column.
                    foreach (var candidate in scopedRef.Table.Columns
                        .Select(column => new
                        {
                            Column = column,
                            Rank = GetOnClauseJoinColumnRank(target.Table, column, preferredKeys)
                        })
                        .Where(x => x.Rank >= 0)
                        .OrderBy(x => x.Rank)
                        .ThenBy(x => x.Column.OrdinalPosition))
                    {
                        var targetColumn = FindTargetColumnForCandidate(target.Table, candidate.Column);
                        if (targetColumn == null)
                            continue;

                        string condition = $"{scopedRef.EffectiveAlias}.{candidate.Column.Name} = {target.EffectiveAlias}.{targetColumn.Name}";
                        EmitOnClauseCondition(results, emitted, condition, prefix,
                            description: $"Join voi {target.EffectiveAlias} ({target.Table.Name})",
                            priority: 190 - candidate.Rank,
                            sortOrder: (i * 1000) + candidate.Column.OrdinalPosition);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AddOnClauseJoinConditionSuggestions failed", ex);
            }
        }

        private static void EmitOnClauseCondition(
            List<CompletionItemData> results,
            HashSet<string> emitted,
            string condition,
            string prefix,
            string description,
            int priority,
            int sortOrder)
        {
            if (string.IsNullOrWhiteSpace(condition) || !emitted.Add(condition))
                return;
            if (!string.IsNullOrEmpty(prefix) && !MatchesPrefix(condition, prefix))
                return;

            results.Add(new CompletionItemData
            {
                Text = condition,
                InsertText = condition,
                Description = description,
                Kind = CompletionItemKind.JoinSuggestion,
                Priority = priority,
                SortOrder = sortOrder,
                IconKey = "JoinSuggestion"
            });
        }

        /// <summary>
        /// Finds the target-side column to pair with a scoped candidate column:
        /// same normalized name first, then the target's PK / id-like key.
        /// </summary>
        private static ColumnInfo FindTargetColumnForCandidate(TableInfo targetTable, ColumnInfo candidate)
        {
            string normalized = NormalizeIdentifier(candidate.Name);
            foreach (var column in targetTable.Columns)
            {
                if (string.Equals(NormalizeIdentifier(column.Name), normalized, StringComparison.OrdinalIgnoreCase))
                    return column;
            }

            var pk = targetTable.PrimaryKeyColumns.OrderBy(c => c.OrdinalPosition).FirstOrDefault();
            if (pk != null)
                return pk;

            return GetLikelyJoinKeyColumns(targetTable)
                .OrderBy(c => c.OrdinalPosition)
                .FirstOrDefault();
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
            if (string.IsNullOrEmpty(prefix))
                return true;

            if (MatchesPrefix(scopedRef.EffectiveAlias, prefix))
                return true;

            if (scopedRef.Table == null)
                return false;

            return MatchesPrefix(scopedRef.Table.Name, prefix) ||
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

        /// <summary>
        /// Adds variable and parameter suggestions for `@...` prefixes.
        /// Pulls from the current procedure header and local DECLARE statements.
        /// </summary>
        private static void AddVariableMatches(
            List<CompletionItemData> results,
            string prefix,
            string fullText,
            int caretOffset)
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string statementText = GetCurrentStatementScope(fullText, caretOffset).text;

                foreach (var parameterName in GetCurrentProcedureParameterNames(statementText))
                {
                    if (!MatchesPrefix(parameterName, prefix) || !seen.Add(parameterName))
                        continue;

                    results.Add(BuildVariableItem(
                        parameterName,
                        $"Procedure parameter: {parameterName}",
                        priority: 240));
                }

                foreach (var variableName in GetDeclaredVariableNames(statementText))
                {
                    if (!MatchesPrefix(variableName, prefix) || !seen.Add(variableName))
                        continue;

                    results.Add(BuildVariableItem(
                        variableName,
                        $"Declared variable: {variableName}",
                        priority: 230));
                }

            }
            catch (Exception ex)
            {
                Logger.Error("AddVariableMatches failed", ex);
            }
        }

        private static CompletionItemData BuildVariableItem(string name, string description, int priority)
        {
            return new CompletionItemData
            {
                Text = name,
                InsertText = name,
                Description = description,
                Kind = CompletionItemKind.Variable,
                Priority = priority,
                IconKey = "Variable"
            };
        }

        private static IEnumerable<string> GetCurrentProcedureParameterNames(string statementText)
        {
            if (string.IsNullOrWhiteSpace(statementText))
                yield break;

            var headerMatch = Regex.Match(
                statementText,
                @"(?is)\b(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?:PROC(?:EDURE)?|FUNCTION)\b(?<header>[\s\S]*?)\bAS\b");
            if (!headerMatch.Success)
                yield break;

            var header = headerMatch.Groups["header"].Value;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(header, @"@\w+"))
            {
                if (seen.Add(match.Value))
                    yield return match.Value;
            }
        }

        private static IEnumerable<string> GetDeclaredVariableNames(string statementText)
        {
            if (string.IsNullOrWhiteSpace(statementText))
                yield break;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match declareMatch in Regex.Matches(
                statementText,
                @"(?im)^\s*DECLARE\b(?<decl>[^\r\n;]*)"))
            {
                var declaration = declareMatch.Groups["decl"].Value;
                foreach (Match variableMatch in Regex.Matches(declaration, @"@\w+"))
                {
                    if (seen.Add(variableMatch.Value))
                        yield return variableMatch.Value;
                }
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
