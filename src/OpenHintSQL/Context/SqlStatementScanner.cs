using System;
using System.Collections.Generic;

namespace OpenHintSQL.Context
{
    /// <summary>
    /// The T-SQL statement that contains the caret, plus the batch-level names
    /// (declared variables, procedure parameters) visible from it.
    /// </summary>
    internal sealed class SqlCaretScope
    {
        public string StatementText { get; set; } = string.Empty;
        public int CaretOffset { get; set; }
        public IReadOnlyList<string> DeclaredVariables { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> ProcedureParameters { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Finds the statement around the caret with one forward pass over its GO batch.
    /// </summary>
    /// <remarks>
    /// Stored procedures are often written without ';', so splitting on ';' alone made
    /// the "statement" the whole procedure and every completion re-analysed thousands
    /// of lines. This scanner also splits on block structure (BEGIN/END, IF/ELSE,
    /// WHILE, TRY/CATCH) and on keywords that can only start a new statement, while
    /// skipping strings, comments, brackets, parenthesised subqueries and CASE…END.
    /// It is a heuristic, not a parser: where it can't tell, it keeps text together,
    /// because a slightly larger scope only costs speed while a wrong split hides
    /// tables and aliases. It stops at the end of the caret's statement.
    /// </remarks>
    internal sealed class SqlStatementScanner
    {
        private const byte BlockFrame = 1;
        private const byte CaseFrame = 2;
        private const int MaxCteLookahead = 4096;

        private readonly char[] _text;
        private readonly int _end;
        private readonly int _caret;
        private readonly Stack<byte> _frames = new Stack<byte>();
        private readonly List<string> _variables = new List<string>();
        private readonly List<string> _parameters = new List<string>();

        private int _pos;
        private int _depth;
        private int _statementStart;
        private int _resultStart = -1;
        private int _resultEnd = -1;

        // State of the statement being scanned.
        private SqlKeyword _lead;
        private bool _bodySeen;          // INSERT/WITH/VIEW body (SELECT, VALUES, EXEC) reached
        private bool _setSeen;           // UPDATE … SET reached
        private bool _viewBody;          // after CREATE VIEW … AS
        private bool _inHeader;          // CREATE/ALTER PROC|FUNCTION|TRIGGER|VIEW before AS
        private bool _routineHeader;
        private bool _expectDeclaredName;
        private SqlKeyword _previous;
        private bool _previousIsVariable;

        private SqlStatementScanner(char[] text, int start, int end, int caret)
        {
            _text = text;
            _pos = start;
            _statementStart = start;
            _end = end;
            _caret = caret;
        }

        public static SqlCaretScope Analyze(string text, int caret)
        {
            if (string.IsNullOrEmpty(text))
                return new SqlCaretScope();

            return Analyze(text.ToCharArray(), text.Length, caret);
        }

        public static SqlCaretScope Analyze(char[] text, int length, int caret)
        {
            if (text == null || length <= 0)
                return new SqlCaretScope();

            caret = Math.Max(0, Math.Min(caret, length));
            FindBatchBounds(text, length, caret, out int batchStart, out int batchEnd);

            var scanner = new SqlStatementScanner(text, batchStart, batchEnd, caret);
            scanner.Run();

            int start = scanner._resultStart;
            int end = scanner._resultEnd;
            return new SqlCaretScope
            {
                StatementText = new string(text, start, end - start),
                CaretOffset = caret - start,
                DeclaredVariables = scanner._variables,
                ProcedureParameters = scanner._parameters
            };
        }

        // ───────────────────────── batch bounds ─────────────────────────

        private static void FindBatchBounds(char[] text, int length, int caret, out int start, out int end)
        {
            int caretLineStart = caret;
            while (caretLineStart > 0 && text[caretLineStart - 1] != '\n')
                caretLineStart--;

            start = 0;
            for (int lineEnd = caretLineStart - 1; lineEnd >= 0;)
            {
                int lineStart = lineEnd;
                while (lineStart > 0 && text[lineStart - 1] != '\n')
                    lineStart--;

                if (IsGoLine(text, lineStart, lineEnd))
                {
                    start = lineEnd + 1;
                    break;
                }

                lineEnd = lineStart - 1;
            }

            int caretLineEnd = caret;
            while (caretLineEnd < length && text[caretLineEnd] != '\n')
                caretLineEnd++;

            end = length;
            for (int lineStart = caretLineEnd + 1; lineStart < length;)
            {
                int lineEnd = lineStart;
                while (lineEnd < length && text[lineEnd] != '\n')
                    lineEnd++;

                if (IsGoLine(text, lineStart, lineEnd))
                {
                    end = lineStart;
                    break;
                }

                lineStart = lineEnd + 1;
            }
        }

        /// <summary>"GO" alone on a line, optionally with a repeat count and/or a -- comment.</summary>
        private static bool IsGoLine(char[] text, int start, int end)
        {
            int pos = start;
            while (pos < end && (text[pos] == ' ' || text[pos] == '\t'))
                pos++;

            if (end - pos < 2 ||
                char.ToUpperInvariant(text[pos]) != 'G' ||
                char.ToUpperInvariant(text[pos + 1]) != 'O')
            {
                return false;
            }

            pos += 2;
            if (pos < end && IsWordChar(text[pos]))
                return false;

            while (pos < end && (text[pos] == ' ' || text[pos] == '\t'))
                pos++;
            while (pos < end && char.IsDigit(text[pos]))
                pos++;
            while (pos < end && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\r'))
                pos++;

            return pos == end || (pos + 1 < end && text[pos] == '-' && text[pos + 1] == '-');
        }

        // ───────────────────────── scanning ─────────────────────────

        private void Run()
        {
            while (_pos < _end && _resultStart < 0)
            {
                char c = _text[_pos];
                char next = _pos + 1 < _end ? _text[_pos + 1] : '\0';

                if (char.IsWhiteSpace(c))
                    _pos++;
                else if (c == '-' && next == '-')
                    SkipLineComment();
                else if (c == '/' && next == '*')
                    SkipBlockComment();
                else if (c == '\'' || c == '"' || c == '[')
                    SkipQuoted(c == '[' ? ']' : c, stopAtNewline: c != '\'');
                else if (IsWordChar(c))
                    ReadWord();
                else
                    ReadPunctuation(c);
            }

            if (_resultStart < 0)
            {
                _resultStart = _statementStart;
                _resultEnd = _end;
            }
        }

        private void ReadPunctuation(char c)
        {
            _pos++;
            MarkOtherToken();

            if (c == '(')
            {
                _depth++;
            }
            else if (c == ')')
            {
                if (_depth > 0)
                    _depth--;
            }
            else if (_depth == 0 && c == ';')
            {
                while (_frames.Count > 0 && _frames.Peek() == CaseFrame)
                    _frames.Pop();
                Boundary(_pos - 1, _pos);
            }
            else if (_depth == 0 && c == ',' && _lead == SqlKeyword.Declare)
            {
                _expectDeclaredName = true;
            }
        }

        private void ReadWord()
        {
            int start = _pos;
            while (_pos < _end && IsWordChar(_text[_pos]))
                _pos++;

            if (_text[start] == '@')
            {
                ReadVariable(start, _pos - start);
                return;
            }

            _expectDeclaredName = false;
            var keyword = SqlScannerKeywords.Lookup(_text, start, _pos - start);
            if (keyword != SqlKeyword.None && _depth == 0)
                OnKeyword(keyword, start, _pos);

            _previous = keyword;
            _previousIsVariable = false;
        }

        private void ReadVariable(int start, int length)
        {
            bool isSystemFunction = length > 1 && _text[start + 1] == '@';
            if (!isSystemFunction && length > 1)
            {
                if (_inHeader && _routineHeader)
                    AddUnique(_parameters, new string(_text, start, length));
                else if (_expectDeclaredName && _depth == 0 && _lead == SqlKeyword.Declare)
                    AddUnique(_variables, new string(_text, start, length));
            }

            _expectDeclaredName = false;
            _previous = SqlKeyword.None;
            _previousIsVariable = true;
        }

        private void OnKeyword(SqlKeyword keyword, int start, int end)
        {
            // Inside CASE … END only nesting matters; ELSE/END there belong to the expression.
            if (_frames.Count > 0 && _frames.Peek() == CaseFrame)
            {
                if (keyword == SqlKeyword.Case)
                    _frames.Push(CaseFrame);
                else if (keyword == SqlKeyword.End)
                    _frames.Pop();
                return;
            }

            if (_inHeader)
            {
                // The routine body starts after the header's AS; "@p AS INT" and
                // "EXECUTE AS OWNER" are part of the header.
                if (keyword == SqlKeyword.As && !_previousIsVariable && _previous != SqlKeyword.Exec)
                {
                    _inHeader = false;
                    if (_routineHeader)
                        Boundary(end, end);
                    else
                        _viewBody = true;
                }
                return;
            }

            switch (keyword)
            {
                case SqlKeyword.Case:
                    _frames.Push(CaseFrame);
                    return;

                case SqlKeyword.Begin:
                    OnBegin(start, end);
                    return;

                case SqlKeyword.End:
                    OnEnd(start, end);
                    return;

                case SqlKeyword.Else:
                    Boundary(start, end);
                    return;

                case SqlKeyword.Proc:
                case SqlKeyword.Function:
                case SqlKeyword.Trigger:
                case SqlKeyword.View:
                    if (_previous == SqlKeyword.Create || _previous == SqlKeyword.Alter)
                    {
                        _inHeader = true;
                        _routineHeader = keyword != SqlKeyword.View;
                    }
                    return;

                case SqlKeyword.If:
                    // DROP TABLE IF EXISTS … is not a control-flow IF.
                    if (_previous != SqlKeyword.Table && _previous != SqlKeyword.View &&
                        _previous != SqlKeyword.Proc && _previous != SqlKeyword.Function &&
                        _previous != SqlKeyword.Trigger && _previous != SqlKeyword.Index &&
                        _previous != SqlKeyword.Schema)
                    {
                        StartStatement(start, keyword);
                    }
                    return;

                case SqlKeyword.Create:
                case SqlKeyword.Alter:
                case SqlKeyword.Drop:
                    if (_previous != SqlKeyword.Or && _lead != SqlKeyword.Alter)
                        StartStatement(start, keyword);
                    return;

                case SqlKeyword.Set:
                    if ((_lead == SqlKeyword.Update && !_setSeen) || _lead == SqlKeyword.Merge ||
                        _lead == SqlKeyword.Alter || _previous == SqlKeyword.Delete || _previous == SqlKeyword.Update)
                    {
                        _setSeen = true;
                        return;
                    }
                    StartStatement(start, keyword);
                    return;

                case SqlKeyword.Select:
                    if (_previous == SqlKeyword.Union || _previous == SqlKeyword.All ||
                        _previous == SqlKeyword.Except || _previous == SqlKeyword.Intersect ||
                        _previous == SqlKeyword.For)
                    {
                        return;
                    }
                    if (!_bodySeen && (_lead == SqlKeyword.Insert || _lead == SqlKeyword.With || _viewBody))
                    {
                        _bodySeen = true;
                        return;
                    }
                    StartStatement(start, keyword);
                    return;

                case SqlKeyword.Values:
                    if (_lead == SqlKeyword.Insert)
                        _bodySeen = true;
                    return;

                case SqlKeyword.Insert:
                case SqlKeyword.Update:
                case SqlKeyword.Delete:
                case SqlKeyword.Merge:
                    OnDataModification(keyword, start);
                    return;

                case SqlKeyword.With:
                    if (LooksLikeCommonTableExpression(end))
                        StartStatement(start, keyword);
                    return;

                case SqlKeyword.Exec:
                    if (_previous == SqlKeyword.Grant || _previous == SqlKeyword.Deny ||
                        _previous == SqlKeyword.Revoke || _previous == SqlKeyword.With)
                    {
                        return;
                    }
                    if (_lead == SqlKeyword.Insert && !_bodySeen)
                    {
                        _bodySeen = true;
                        return;
                    }
                    StartStatement(start, keyword);
                    return;

                case SqlKeyword.Fetch:
                    // OFFSET … ROWS FETCH NEXT … is part of a SELECT.
                    if (_previous != SqlKeyword.Row && _previous != SqlKeyword.Rows)
                        StartStatement(start, keyword);
                    return;

                case SqlKeyword.Declare:
                    StartStatement(start, keyword);
                    _expectDeclaredName = true;
                    return;

                case SqlKeyword.While:
                case SqlKeyword.Print:
                case SqlKeyword.Raiserror:
                case SqlKeyword.Throw:
                case SqlKeyword.Return:
                case SqlKeyword.Truncate:
                case SqlKeyword.Open:
                case SqlKeyword.Close:
                case SqlKeyword.Deallocate:
                case SqlKeyword.Commit:
                case SqlKeyword.Rollback:
                case SqlKeyword.Break:
                case SqlKeyword.Continue:
                case SqlKeyword.Goto:
                case SqlKeyword.Waitfor:
                case SqlKeyword.Use:
                case SqlKeyword.Grant:
                case SqlKeyword.Deny:
                case SqlKeyword.Revoke:
                    StartStatement(start, keyword);
                    return;
            }
        }

        private void OnBegin(int start, int end)
        {
            var next = PeekWord(end, out int nextEnd);
            if (next == SqlKeyword.Tran || next == SqlKeyword.Transaction || next == SqlKeyword.Distributed ||
                next == SqlKeyword.Dialog || next == SqlKeyword.Conversation)
            {
                StartStatement(start, SqlKeyword.Begin);
                return;
            }

            int after = next == SqlKeyword.Try || next == SqlKeyword.Catch ? nextEnd : end;
            Boundary(start, after);
            _frames.Push(BlockFrame);
            _pos = after;
        }

        private void OnEnd(int start, int end)
        {
            var next = PeekWord(end, out int nextEnd);
            int after = next == SqlKeyword.Try || next == SqlKeyword.Catch ? nextEnd : end;
            if (_frames.Count > 0)
                _frames.Pop();
            Boundary(start, after);
            _pos = after;
        }

        private void OnDataModification(SqlKeyword keyword, int start)
        {
            // MERGE … WHEN MATCHED THEN UPDATE/DELETE/INSERT, FOR UPDATE, ON DELETE CASCADE.
            if (_previous == SqlKeyword.Then || _previous == SqlKeyword.On ||
                (_previous == SqlKeyword.For && keyword == SqlKeyword.Update) ||
                (_lead == SqlKeyword.Merge && keyword != SqlKeyword.Merge))
            {
                return;
            }

            // WITH cte AS (…) INSERT/UPDATE/DELETE/MERGE: the CTE's body, same statement.
            if (_lead == SqlKeyword.With && !_bodySeen)
            {
                _lead = keyword;
                _bodySeen = keyword != SqlKeyword.Insert;
                _setSeen = false;
                return;
            }

            StartStatement(start, keyword);
        }

        /// <summary>WITH name [(columns)] AS ( … — rather than a table hint or WITH TIES etc.</summary>
        private bool LooksLikeCommonTableExpression(int pos)
        {
            int limit = Math.Min(_end, pos + MaxCteLookahead);
            pos = SkipSpaces(pos, limit);
            if (pos >= limit)
                return false;

            if (_text[pos] == '[')
            {
                while (pos < limit && _text[pos] != ']')
                    pos++;
                pos++;
            }
            else if (IsWordChar(_text[pos]) && _text[pos] != '@')
            {
                while (pos < limit && IsWordChar(_text[pos]))
                    pos++;
            }
            else
            {
                return false;
            }

            pos = SkipSpaces(pos, limit);
            if (pos < limit && _text[pos] == '(')
            {
                int depth = 0;
                for (; pos < limit; pos++)
                {
                    if (_text[pos] == '(') depth++;
                    else if (_text[pos] == ')' && --depth == 0) break;
                }
                pos = SkipSpaces(pos + 1, limit);
            }

            if (pos + 2 > limit ||
                char.ToUpperInvariant(_text[pos]) != 'A' ||
                char.ToUpperInvariant(_text[pos + 1]) != 'S' ||
                (pos + 2 < limit && IsWordChar(_text[pos + 2])))
            {
                return false;
            }

            pos = SkipSpaces(pos + 2, limit);
            return pos < limit && _text[pos] == '(';
        }

        private void StartStatement(int start, SqlKeyword lead)
        {
            Boundary(start, start);
            _lead = lead;
        }

        /// <summary>
        /// The current statement ends at <paramref name="previousEnd"/> and the next one
        /// starts at <paramref name="nextStart"/> (the gap is a BEGIN/END/ELSE keyword).
        /// Records the result as soon as the caret's statement is complete.
        /// </summary>
        private void Boundary(int previousEnd, int nextStart)
        {
            if (_caret <= previousEnd)
            {
                _resultStart = _statementStart;
                _resultEnd = previousEnd;
                return;
            }

            if (_caret < nextStart)
            {
                _resultStart = previousEnd;
                _resultEnd = nextStart;
                return;
            }

            _statementStart = nextStart;
            _lead = SqlKeyword.None;
            _bodySeen = false;
            _setSeen = false;
            _viewBody = false;
            _inHeader = false;
            _expectDeclaredName = false;
        }

        // ───────────────────────── token helpers ─────────────────────────

        private SqlKeyword PeekWord(int pos, out int wordEnd)
        {
            pos = SkipSpaces(pos, _end);
            wordEnd = pos;
            while (wordEnd < _end && IsWordChar(_text[wordEnd]))
                wordEnd++;

            return wordEnd > pos ? SqlScannerKeywords.Lookup(_text, pos, wordEnd - pos) : SqlKeyword.None;
        }

        private int SkipSpaces(int pos, int limit)
        {
            while (pos < limit && char.IsWhiteSpace(_text[pos]))
                pos++;
            return pos;
        }

        private void SkipLineComment()
        {
            while (_pos < _end && _text[_pos] != '\n')
                _pos++;
        }

        private void SkipBlockComment()
        {
            int nesting = 0;
            while (_pos < _end)
            {
                if (_text[_pos] == '/' && _pos + 1 < _end && _text[_pos + 1] == '*')
                {
                    nesting++;
                    _pos += 2;
                }
                else if (_text[_pos] == '*' && _pos + 1 < _end && _text[_pos + 1] == '/')
                {
                    _pos += 2;
                    if (--nesting == 0)
                        return;
                }
                else
                {
                    _pos++;
                }
            }
        }

        /// <summary>
        /// Skips a quoted token whose closing quote is escaped by doubling ('it''s', [a]]b]).
        /// Identifiers can't span lines, so an unclosed one ends at the line break
        /// instead of swallowing the rest of the batch while the user is typing it.
        /// </summary>
        private void SkipQuoted(char close, bool stopAtNewline)
        {
            _pos++;
            while (_pos < _end)
            {
                char c = _text[_pos];
                if (c == close)
                {
                    if (_pos + 1 < _end && _text[_pos + 1] == close)
                    {
                        _pos += 2;
                        continue;
                    }

                    _pos++;
                    break;
                }

                if (stopAtNewline && c == '\n')
                    break;

                _pos++;
            }

            MarkOtherToken();
        }

        private void MarkOtherToken()
        {
            _previous = SqlKeyword.None;
            _previousIsVariable = false;
        }

        private static void AddUnique(List<string> names, string name)
        {
            foreach (var existing in names)
            {
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            names.Add(name);
        }

        private static bool IsWordChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';
    }
}
