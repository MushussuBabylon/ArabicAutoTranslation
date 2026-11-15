using System.Collections.Generic;
using System.Text;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>UTF-16 <-> code point helpers (the engine works on code points; lone surrogates pass through).</summary>
    internal static class CodePoints
    {
        /// <summary>Code points of a string; an unpaired surrogate is kept as its own value.</summary>
        internal static int[] From(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return new int[0];
            }
            List<int> list = new List<int>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    list.Add(char.ConvertToUtf32(c, s[i + 1]));
                    i++;
                }
                else
                {
                    list.Add(c);
                }
            }
            return list.ToArray();
        }

        internal static void Append(StringBuilder sb, int cp)
        {
            if (cp >= 0x10000 && cp <= 0x10FFFF)
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
            else
            {
                sb.Append((char)cp);
            }
        }

        internal static void Append(StringBuilder sb, int[] cps, int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                Append(sb, cps[i]);
            }
        }

        internal static string ToString(int[] cps)
        {
            StringBuilder sb = new StringBuilder(cps.Length);
            for (int i = 0; i < cps.Length; i++)
            {
                Append(sb, cps[i]);
            }
            return sb.ToString();
        }

        internal static void AddString(List<int> list, string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return;
            }
            int[] cps = From(s);
            for (int i = 0; i < cps.Length; i++)
            {
                list.Add(cps[i]);
            }
        }
    }
}
