using System;
using System.Text;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// 64-bit FNV-1a over UTF-16 code units (low byte first). Used for content signatures; it only has to be
    /// stable across runs and platforms, not secure.
    /// </summary>
    internal static class Fnv64
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        internal static ulong Start()
        {
            return OffsetBasis;
        }

        internal static ulong Add(ulong h, char c)
        {
            unchecked
            {
                h ^= (ulong)(c & 0xFF);
                h *= Prime;
                h ^= (ulong)(c >> 8);
                h *= Prime;
            }
            return h;
        }

        internal static ulong Add(ulong h, string s)
        {
            if (s == null) return Add(h, '\u0000');
            for (int i = 0; i < s.Length; i++) h = Add(h, s[i]);
            return h;
        }

        /// <summary>Adds a string followed by a separator so that ("ab","c") and ("a","bc") differ.</summary>
        internal static ulong AddField(ulong h, string s)
        {
            h = Add(h, s);
            return Add(h, '\u001F');
        }

        internal static ulong AddU64(ulong h, ulong value)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++)
                {
                    h ^= (value >> (8 * i)) & 0xFF;
                    h *= Prime;
                }
            }
            return h;
        }

        internal static ulong Of(string s)
        {
            return Add(OffsetBasis, s);
        }

        internal static string Hex(ulong h)
        {
            var sb = new StringBuilder(16);
            for (int shift = 60; shift >= 0; shift -= 4)
            {
                int nibble = (int)((h >> shift) & 0xF);
                sb.Append((char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10)));
            }
            return sb.ToString();
        }
    }
}
