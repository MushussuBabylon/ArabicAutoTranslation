using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>A piece of a text: either sent to the translator or copied verbatim.</summary>
    internal sealed class TextSegment
    {
        internal readonly string Text;
        /// <summary>True when the piece has to be translated; false for spaces, line breaks and pieces without letters.</summary>
        internal readonly bool Translate;

        internal TextSegment(string text, bool translate)
        {
            Text = text;
            Translate = translate;
        }
    }

    /// <summary>
    /// Cuts a text into pieces for the translator without losing any of its structure. Line breaks (real ones and the
    /// escape \n) always separate pieces and are copied verbatim; a paragraph longer than the limit is cut after
    /// sentence-ending punctuation. Joining all pieces again gives back exactly the original text.
    /// (The generic splitter of the mod drops the delimiters, which would destroy line breaks and full stops.)
    /// </summary>
    internal static class TextSegmenter
    {
        /// <summary>Longest text sent to the translator in one request (the limit the mod has always used).</summary>
        internal const int DefaultLimit = 200;

        private enum PieceKind : byte
        {
            Sentence,
            Space,
            Break
        }

        private struct Piece
        {
            internal string Text;
            internal PieceKind Kind;
        }

        internal static List<TextSegment> Split(string text, int limit)
        {
            var result = new List<TextSegment>();
            if (string.IsNullOrEmpty(text)) return result;
            if (limit < 1) limit = 1;

            var pieces = Cut(text);
            var chunk = new StringBuilder();
            for (int k = 0; k < pieces.Count; k++)
            {
                var p = pieces[k];
                if (p.Kind == PieceKind.Break)
                {
                    Flush(result, chunk);
                    result.Add(new TextSegment(p.Text, false));
                }
                else if (p.Kind == PieceKind.Space)
                {
                    // inside a paragraph the space belongs to the chunk when the next sentence still fits into it
                    if (chunk.Length > 0 && k + 1 < pieces.Count && pieces[k + 1].Kind == PieceKind.Sentence &&
                        chunk.Length + p.Text.Length + pieces[k + 1].Text.Length <= limit)
                    {
                        chunk.Append(p.Text);
                    }
                    else
                    {
                        Flush(result, chunk);
                        result.Add(new TextSegment(p.Text, false));
                    }
                }
                else
                {
                    if (chunk.Length > 0 && chunk.Length + p.Text.Length > limit) Flush(result, chunk);
                    chunk.Append(p.Text);
                }
            }
            Flush(result, chunk);
            return result;
        }

        private static void Flush(List<TextSegment> result, StringBuilder chunk)
        {
            if (chunk.Length == 0) return;
            var s = chunk.ToString();
            chunk.Length = 0;
            result.Add(new TextSegment(s, HasLetters(s)));
        }

        /// <summary>
        /// True when the text has a letter outside its placeholders, tags and multiplier signs (otherwise there is nothing to
        /// translate).
        /// </summary>
        internal static bool HasLetters(string s)
        {
            var stripped = Placeholders.WireSpan.Replace(s, " ");
            for (int i = 0; i < stripped.Length; i++)
            {
                if (char.IsLetter(stripped[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// Cuts a text at its placeholders and tags: each placeholder (with the multiplier sign in front of it, if any) is a
        /// piece that is copied verbatim, the text between two placeholders is a piece to translate (when it has letters).
        /// Joining all pieces gives back the original text.
        /// </summary>
        internal static List<TextSegment> SplitAtPlaceholders(string text)
        {
            var result = new List<TextSegment>();
            if (string.IsNullOrEmpty(text)) return result;

            int pos = 0;
            for (var m = Placeholders.WireSpan.Match(text); m.Success; m = m.NextMatch())
            {
                if (m.Index > pos)
                {
                    var between = text.Substring(pos, m.Index - pos);
                    result.Add(new TextSegment(between, HasLetters(between)));
                }
                result.Add(new TextSegment(m.Value, false));
                pos = m.Index + m.Length;
            }
            if (pos < text.Length)
            {
                var tail = text.Substring(pos);
                result.Add(new TextSegment(tail, HasLetters(tail)));
            }
            return result;
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\u00A0';
        }

        private static bool EndsSentence(StringBuilder buf)
        {
            if (buf.Length == 0) return false;
            char c = buf[buf.Length - 1];
            return c == '.' || c == '!' || c == '?' || c == '\u2026' || c == '\u061F' || c == '\u3002';
        }

        private static void FlushSentence(List<Piece> pieces, StringBuilder buf)
        {
            if (buf.Length == 0) return;
            pieces.Add(new Piece { Text = buf.ToString(), Kind = PieceKind.Sentence });
            buf.Length = 0;
        }

        private static List<Piece> Cut(string text)
        {
            var pieces = new List<Piece>();
            var buf = new StringBuilder();
            int i = 0;
            Match next = Placeholders.Span.Match(text);
            while (i < text.Length)
            {
                if (next.Success && next.Index == i)
                {
                    if (next.Value == "\\n")
                    {
                        // the escape of a line break
                        FlushSentence(pieces, buf);
                        pieces.Add(new Piece { Text = next.Value, Kind = PieceKind.Break });
                    }
                    else
                    {
                        buf.Append(next.Value);
                    }
                    i += next.Length;
                    next = next.NextMatch();
                    continue;
                }

                char c = text[i];
                if (c == '\r' || c == '\n')
                {
                    FlushSentence(pieces, buf);
                    int len = (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                    pieces.Add(new Piece { Text = text.Substring(i, len), Kind = PieceKind.Break });
                    i += len;
                    continue;
                }

                if (IsSpace(c))
                {
                    int j = i + 1;
                    while (j < text.Length && IsSpace(text[j])) j++;
                    var run = text.Substring(i, j - i);
                    if (buf.Length == 0 || EndsSentence(buf))
                    {
                        FlushSentence(pieces, buf);
                        pieces.Add(new Piece { Text = run, Kind = PieceKind.Space });
                    }
                    else
                    {
                        buf.Append(run);
                    }
                    i = j;
                    continue;
                }

                buf.Append(c);
                i++;
            }
            FlushSentence(pieces, buf);
            return pieces;
        }
    }
}
