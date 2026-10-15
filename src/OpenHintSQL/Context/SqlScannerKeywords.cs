using System.Collections.Generic;
using System.Linq;

namespace OpenHintSQL.Context
{
    /// <summary>
    /// Keywords that <see cref="SqlStatementScanner"/> reacts to when splitting a batch
    /// into statements. Any other word is <see cref="None"/>.
    /// </summary>
    internal enum SqlKeyword
    {
        None = 0,
        Select, Insert, Update, Delete, Merge, With, Exec, Values, Set, Declare,
        If, Else, While, Begin, End, Case, Try, Catch,
        Tran, Transaction, Distributed, Dialog, Conversation,
        Union, All, Except, Intersect, For, Then, On, Or, Row, Rows, As,
        Create, Alter, Drop, Proc, Function, Trigger, View, Table, Index, Schema,
        Print, Raiserror, Throw, Return, Truncate, Open, Fetch, Close, Deallocate,
        Commit, Rollback, Break, Continue, Goto, Waitfor, Use, Grant, Deny, Revoke
    }

    /// <summary>
    /// Case-insensitive keyword lookup straight from a character buffer. The scanner
    /// checks every word of a (possibly very long) procedure on each keystroke, so the
    /// lookup buckets keywords by length and never allocates a string per word.
    /// </summary>
    internal static class SqlScannerKeywords
    {
        private static readonly KeyValuePair<string, SqlKeyword>[][] ByLength = Build(new Dictionary<string, SqlKeyword>
        {
            ["SELECT"] = SqlKeyword.Select, ["INSERT"] = SqlKeyword.Insert, ["UPDATE"] = SqlKeyword.Update,
            ["DELETE"] = SqlKeyword.Delete, ["MERGE"] = SqlKeyword.Merge, ["WITH"] = SqlKeyword.With,
            ["EXEC"] = SqlKeyword.Exec, ["EXECUTE"] = SqlKeyword.Exec, ["VALUES"] = SqlKeyword.Values,
            ["SET"] = SqlKeyword.Set, ["DECLARE"] = SqlKeyword.Declare, ["IF"] = SqlKeyword.If,
            ["ELSE"] = SqlKeyword.Else, ["WHILE"] = SqlKeyword.While, ["BEGIN"] = SqlKeyword.Begin,
            ["END"] = SqlKeyword.End, ["CASE"] = SqlKeyword.Case, ["TRY"] = SqlKeyword.Try,
            ["CATCH"] = SqlKeyword.Catch, ["TRAN"] = SqlKeyword.Tran, ["TRANSACTION"] = SqlKeyword.Transaction,
            ["DISTRIBUTED"] = SqlKeyword.Distributed, ["DIALOG"] = SqlKeyword.Dialog,
            ["CONVERSATION"] = SqlKeyword.Conversation, ["UNION"] = SqlKeyword.Union, ["ALL"] = SqlKeyword.All,
            ["EXCEPT"] = SqlKeyword.Except, ["INTERSECT"] = SqlKeyword.Intersect, ["FOR"] = SqlKeyword.For,
            ["THEN"] = SqlKeyword.Then, ["ON"] = SqlKeyword.On, ["OR"] = SqlKeyword.Or, ["ROW"] = SqlKeyword.Row,
            ["ROWS"] = SqlKeyword.Rows, ["AS"] = SqlKeyword.As, ["CREATE"] = SqlKeyword.Create,
            ["ALTER"] = SqlKeyword.Alter, ["DROP"] = SqlKeyword.Drop, ["PROC"] = SqlKeyword.Proc,
            ["PROCEDURE"] = SqlKeyword.Proc, ["FUNCTION"] = SqlKeyword.Function, ["TRIGGER"] = SqlKeyword.Trigger,
            ["VIEW"] = SqlKeyword.View, ["TABLE"] = SqlKeyword.Table, ["INDEX"] = SqlKeyword.Index,
            ["SCHEMA"] = SqlKeyword.Schema, ["PRINT"] = SqlKeyword.Print, ["RAISERROR"] = SqlKeyword.Raiserror,
            ["THROW"] = SqlKeyword.Throw, ["RETURN"] = SqlKeyword.Return, ["TRUNCATE"] = SqlKeyword.Truncate,
            ["OPEN"] = SqlKeyword.Open, ["FETCH"] = SqlKeyword.Fetch, ["CLOSE"] = SqlKeyword.Close,
            ["DEALLOCATE"] = SqlKeyword.Deallocate, ["COMMIT"] = SqlKeyword.Commit,
            ["ROLLBACK"] = SqlKeyword.Rollback, ["BREAK"] = SqlKeyword.Break, ["CONTINUE"] = SqlKeyword.Continue,
            ["GOTO"] = SqlKeyword.Goto, ["WAITFOR"] = SqlKeyword.Waitfor, ["USE"] = SqlKeyword.Use,
            ["GRANT"] = SqlKeyword.Grant, ["DENY"] = SqlKeyword.Deny, ["REVOKE"] = SqlKeyword.Revoke,
        });

        public static SqlKeyword Lookup(char[] text, int start, int length)
        {
            if (length >= ByLength.Length)
                return SqlKeyword.None;

            var bucket = ByLength[length];
            if (bucket == null)
                return SqlKeyword.None;

            foreach (var entry in bucket)
            {
                if (EqualsUpperAscii(text, start, entry.Key))
                    return entry.Value;
            }

            return SqlKeyword.None;
        }

        private static bool EqualsUpperAscii(char[] text, int start, string keyword)
        {
            for (int i = 0; i < keyword.Length; i++)
            {
                char c = text[start + i];
                if (c >= 'a' && c <= 'z')
                    c = (char)(c - ('a' - 'A'));
                if (c != keyword[i])
                    return false;
            }

            return true;
        }

        private static KeyValuePair<string, SqlKeyword>[][] Build(Dictionary<string, SqlKeyword> keywords)
        {
            int maxLength = keywords.Keys.Max(k => k.Length);
            var buckets = new KeyValuePair<string, SqlKeyword>[maxLength + 1][];
            foreach (var group in keywords.GroupBy(k => k.Key.Length))
                buckets[group.Key] = group.ToArray();

            return buckets;
        }
    }
}
