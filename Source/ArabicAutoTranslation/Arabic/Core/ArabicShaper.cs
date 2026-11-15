using System.Collections.Generic;
using System.Text;

namespace ArabicAutoTranslation.Arabic
{
    /// <summary>
    /// Arabic contextual shaping: logical code points in, logical code points with Unicode presentation forms out.
    /// Port of arabic_ref.shape(); contextual forms come from Unicode's own presentation-form decompositions and the
    /// joining types in <see cref="ArabicTables"/>. Transparent marks are skipped when looking for neighbours.
    /// </summary>
    internal static class ArabicShaper
    {
        /// <summary>
        /// Shapes <paramref name="cps"/>. <paramref name="ownerStart"/>[k] is the index in the input of the first source
        /// character of output character k (a lam-alef ligature owns two input characters).
        /// </summary>
        internal static void Shape(int[] cps, out int[] shaped, out int[] ownerStart)
        {
            int n = cps.Length;
            JoinType[] jt = new JoinType[n];
            for (int i = 0; i < n; i++)
            {
                jt[i] = UnicodeData.JoinTypeOf(cps[i]);
            }

            List<int> output = new List<int>(n);
            List<int> owners = new List<int>(n);
            int pos = 0;
            while (pos < n)
            {
                int c = cps[pos];
                JoinType t = jt[pos];
                if (t != JoinType.D && t != JoinType.R)
                {
                    output.Add(c);
                    owners.Add(pos);
                    pos++;
                    continue;
                }

                int p = pos - 1;
                while (p >= 0 && jt[p] == JoinType.T)
                {
                    p--;
                }
                bool prevJoins = p >= 0 && (jt[p] == JoinType.D || jt[p] == JoinType.L || jt[p] == JoinType.C);

                if (c == UnicodeData.Lam && pos + 1 < n)
                {
                    int[] lig = UnicodeData.LamAlefOf(cps[pos + 1]);
                    if (lig != null)
                    {
                        int g = prevJoins ? lig[1] : lig[0];
                        if (g != 0)
                        {
                            output.Add(g);
                            owners.Add(pos);
                            pos += 2;
                            continue;
                        }
                    }
                }

                int q = pos + 1;
                while (q < n && jt[q] == JoinType.T)
                {
                    q++;
                }
                bool nextJoins = t == JoinType.D && q < n &&
                                 (jt[q] == JoinType.D || jt[q] == JoinType.R || jt[q] == JoinType.C);

                // index into the form row: 0 = isolated, 1 = final, 2 = initial, 3 = medial
                int form;
                if (prevJoins && nextJoins) form = 3;
                else if (prevJoins) form = 1;
                else if (nextJoins) form = 2;
                else form = 0;

                int[] f = UnicodeData.FormsOf(c);
                int glyph = 0;
                if (f != null)
                {
                    glyph = f[form];
                    if (glyph == 0)
                    {
                        if (form == 3)
                        {
                            glyph = f[1];
                        }
                        else if (form == 2 || form == 1)
                        {
                            glyph = f[0];
                        }
                    }
                }
                output.Add(glyph != 0 ? glyph : c);
                owners.Add(pos);
                pos++;
            }

            shaped = output.ToArray();
            ownerStart = owners.ToArray();
        }

        /// <summary>True when the text contains Arabic presentation-form characters (it is already shaped).</summary>
        internal static bool HasPresentationForms(string s)
        {
            int[] cps = CodePoints.From(s);
            for (int i = 0; i < cps.Length; i++)
            {
                int b;
                if (UnicodeData.TryUnshape(cps[i], out b) || UnicodeData.TryUnshapeLamAlef(cps[i], out b))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Maps presentation forms back to base letters (idempotent on logical text).</summary>
        internal static string Unshape(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            int[] cps = CodePoints.From(s);
            StringBuilder sb = null;
            for (int i = 0; i < cps.Length; i++)
            {
                int b;
                if (UnicodeData.TryUnshape(cps[i], out b))
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(s.Length + 4);
                        CodePoints.Append(sb, cps, 0, i);
                    }
                    CodePoints.Append(sb, b);
                }
                else if (UnicodeData.TryUnshapeLamAlef(cps[i], out b))
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(s.Length + 4);
                        CodePoints.Append(sb, cps, 0, i);
                    }
                    CodePoints.Append(sb, UnicodeData.Lam);
                    CodePoints.Append(sb, b);
                }
                else if (sb != null)
                {
                    CodePoints.Append(sb, cps[i]);
                }
            }
            return sb == null ? s : sb.ToString();
        }
    }
}
