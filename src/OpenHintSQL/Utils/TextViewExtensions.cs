using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace OpenHintSQL.Utils
{
    /// <summary>
    /// Extension methods for <see cref="ITextView"/> and <see cref="IWpfTextView"/>
    /// to simplify common editor operations used by the completion system.
    /// </summary>
    internal static class TextViewExtensions
    {
        /// <summary>
        /// Gets the caret's absolute position (character offset) within the text buffer.
        /// </summary>
        /// <param name="view">The text view.</param>
        /// <returns>The zero-based character offset of the caret, or 0 if unavailable.</returns>
        public static int GetCaretPosition(this ITextView view)
        {
            try
            {
                if (view == null || view.IsClosed)
                    return 0;

                return view.Caret.Position.BufferPosition.Position;
            }
            catch (Exception ex)
            {
                Logger.Error("GetCaretPosition failed", ex);
                return 0;
            }
        }

        /// <summary>
        /// Gets the text of the line containing the caret.
        /// </summary>
        /// <param name="view">The text view.</param>
        /// <returns>The full text of the current line, or an empty string if unavailable.</returns>
        public static string GetCurrentLineText(this ITextView view)
        {
            try
            {
                if (view == null || view.IsClosed)
                    return string.Empty;

                var caretPos = view.Caret.Position.BufferPosition;
                var line = caretPos.GetContainingLine();
                return line?.GetText() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Error("GetCurrentLineText failed", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets the word (letters, digits, underscores) immediately before the caret position.
        /// Useful for determining the current completion prefix.
        /// </summary>
        /// <param name="view">The text view.</param>
        /// <returns>The word before the caret, or an empty string if none.</returns>
        public static string GetWordBeforeCaret(this ITextView view)
        {
            try
            {
                if (view == null || view.IsClosed)
                    return string.Empty;

                var caretPos = view.Caret.Position.BufferPosition;
                var snapshot = caretPos.Snapshot;
                int position = caretPos.Position;

                if (position == 0)
                    return string.Empty;

                // Walk backwards from caret, collecting word characters
                int start = position;
                while (start > 0)
                {
                    char c = snapshot[start - 1];
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#')
                    {
                        start--;
                    }
                    else
                    {
                        break;
                    }
                }

                if (start == position)
                    return string.Empty;

                return snapshot.GetText(start, position - start);
            }
            catch (Exception ex)
            {
                Logger.Error("GetWordBeforeCaret failed", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets the full text content of the text buffer.
        /// </summary>
        /// <param name="view">The text view.</param>
        /// <returns>The entire text of the buffer, or an empty string if unavailable.</returns>
        public static string GetAllText(this ITextView view)
        {
            try
            {
                if (view == null || view.IsClosed)
                    return string.Empty;

                var snapshot = view.TextSnapshot;
                if (snapshot == null || snapshot.Length == 0)
                    return string.Empty;

                return snapshot.GetText();
            }
            catch (Exception ex)
            {
                Logger.Error("GetAllText failed", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets the T-SQL batch (the text between GO separator lines) that contains the
        /// caret, plus the caret offset inside that batch.
        /// </summary>
        /// <remarks>
        /// Completion never needs anything outside the current batch: variables,
        /// procedure parameters, aliases and statements are all batch-scoped. Reading
        /// only this slice keeps per-keystroke work proportional to the batch instead
        /// of the whole script, and avoids allocating a full-document string (which
        /// lands on the large object heap for big scripts) on every keystroke.
        /// </remarks>
        /// <param name="view">The text view.</param>
        /// <param name="caretOffsetInBatch">Caret position relative to the returned text.</param>
        /// <returns>The batch text, or an empty string if unavailable.</returns>
        public static string GetCaretBatchText(this ITextView view, out int caretOffsetInBatch)
        {
            caretOffsetInBatch = 0;

            try
            {
                if (view == null || view.IsClosed)
                    return string.Empty;

                var caretPoint = view.Caret.Position.BufferPosition;
                var snapshot = caretPoint.Snapshot;
                if (snapshot == null || snapshot.Length == 0)
                    return string.Empty;

                int caret = caretPoint.Position;
                int caretLineNumber = snapshot.GetLineNumberFromPosition(caret);

                int start = 0;
                for (int n = caretLineNumber - 1; n >= 0; n--)
                {
                    var line = snapshot.GetLineFromLineNumber(n);
                    if (IsBatchSeparatorLine(snapshot, line))
                    {
                        start = line.EndIncludingLineBreak.Position;
                        break;
                    }
                }

                int end = snapshot.Length;
                for (int n = caretLineNumber + 1; n < snapshot.LineCount; n++)
                {
                    var line = snapshot.GetLineFromLineNumber(n);
                    if (IsBatchSeparatorLine(snapshot, line))
                    {
                        end = line.Start.Position;
                        break;
                    }
                }

                caretOffsetInBatch = caret - start;
                return snapshot.GetText(start, end - start);
            }
            catch (Exception ex)
            {
                Logger.Error("GetCaretBatchText failed", ex);
                caretOffsetInBatch = view.GetCaretPosition();
                return view.GetAllText();
            }
        }

        /// <summary>
        /// True for a line holding only the GO batch separator, optionally followed by
        /// a repeat count and/or a <c>--</c> comment. Reads characters straight from
        /// the snapshot so scanning many lines doesn't allocate a string per line.
        /// </summary>
        private static bool IsBatchSeparatorLine(ITextSnapshot snapshot, ITextSnapshotLine line)
        {
            if (line.Length < 2)
                return false;

            int pos = line.Start.Position;
            int end = line.End.Position;

            while (pos < end && IsSpaceOrTab(snapshot[pos]))
                pos++;

            if (end - pos < 2 ||
                char.ToUpperInvariant(snapshot[pos]) != 'G' ||
                char.ToUpperInvariant(snapshot[pos + 1]) != 'O')
            {
                return false;
            }

            pos += 2;
            if (pos == end)
                return true;
            if (!IsSpaceOrTab(snapshot[pos]))
                return false;

            while (pos < end && IsSpaceOrTab(snapshot[pos]))
                pos++;
            while (pos < end && char.IsDigit(snapshot[pos]))
                pos++;
            while (pos < end && IsSpaceOrTab(snapshot[pos]))
                pos++;

            return pos == end ||
                   (pos + 1 < end && snapshot[pos] == '-' && snapshot[pos + 1] == '-');
        }

        private static bool IsSpaceOrTab(char c) => c == ' ' || c == '\t';

        /// <summary>
        /// Replaces a span of text in the text buffer.
        /// </summary>
        /// <param name="view">The text view.</param>
        /// <param name="start">The zero-based start position of the span to replace.</param>
        /// <param name="length">The number of characters to replace.</param>
        /// <param name="newText">The replacement text.</param>
        /// <returns>True if the replacement succeeded; false otherwise.</returns>
        public static bool ReplaceSpan(this ITextView view, int start, int length, string newText)
        {
            try
            {
                if (view == null || view.IsClosed)
                    return false;

                var buffer = view.TextBuffer;
                if (buffer == null)
                    return false;

                var snapshot = buffer.CurrentSnapshot;
                if (snapshot == null)
                    return false;

                // Validate the span is within buffer bounds
                if (start < 0 || length < 0 || start + length > snapshot.Length)
                {
                    Logger.Warn($"ReplaceSpan: Invalid span (start={start}, length={length}, bufferLength={snapshot.Length})");
                    return false;
                }

                using (var edit = buffer.CreateEdit())
                {
                    edit.Replace(new Span(start, length), newText ?? string.Empty);
                    edit.Apply();
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("ReplaceSpan failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Calculates the caret position relative to the text view visual, suitable
        /// for anchoring an editor-owned popup.
        /// </summary>
        public static Point GetCaretPopupPosition(this IWpfTextView view)
        {
            try
            {
                if (view == null || view.IsClosed)
                    return new Point(0, 0);

                var caretPos = view.Caret.Position.BufferPosition;
                var caretLine = view.Caret.ContainingTextViewLine;

                if (caretLine == null)
                    return new Point(0, 0);

                // Get the bounds of the caret character. If caret is at end of line,
                // use the line's right edge as the x-coordinate.
                double x;
                double y;

                if (caretPos.Position < view.TextSnapshot.Length)
                {
                    var charBounds = caretLine.GetCharacterBounds(caretPos);
                    x = charBounds.Left - view.ViewportLeft;
                    y = charBounds.Bottom - view.ViewportTop;
                }
                else
                {
                    // Caret at end of buffer
                    x = caretLine.Right - view.ViewportLeft;
                    y = caretLine.Bottom - view.ViewportTop;
                }

                return new Point(Math.Max(0, x), Math.Max(0, y));
            }
            catch (Exception ex)
            {
                Logger.Error("GetCaretPopupPosition failed", ex);
                return new Point(0, 0);
            }
        }

        /// <summary>
        /// Gets the start position of the word before the caret in the text buffer.
        /// </summary>
        /// <param name="view">The text view.</param>
        /// <returns>The zero-based start position of the word, or the caret position if no word is found.</returns>
        public static int GetWordBeforeCaretStart(this ITextView view)
        {
            try
            {
                if (view == null || view.IsClosed)
                    return 0;

                var caretPos = view.Caret.Position.BufferPosition;
                var snapshot = caretPos.Snapshot;
                int position = caretPos.Position;

                if (position == 0)
                    return 0;

                int start = position;
                while (start > 0)
                {
                    char c = snapshot[start - 1];
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#')
                    {
                        start--;
                    }
                    else
                    {
                        break;
                    }
                }

                return start;
            }
            catch (Exception ex)
            {
                Logger.Error("GetWordBeforeCaretStart failed", ex);
                return view?.GetCaretPosition() ?? 0;
            }
        }
    }
}
