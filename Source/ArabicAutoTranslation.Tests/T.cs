using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>
    /// Minimal assertion helper of the test runner: counts checks per group, remembers failures and prints ASCII only
    /// (every non-ASCII character of a compared text is shown as \uXXXX, so the report reads the same everywhere).
    /// </summary>
    internal static class T
    {
        /// <summary>At most this many failures of one group are listed in detail (all of them are counted).</summary>
        private const int MaxListedPerGroup = 12;

        internal sealed class GroupResult
        {
            internal string Name;
            internal int Checks;
            internal int Failures;
            internal readonly List<string> Listed = new List<string>();
        }

        internal static readonly List<GroupResult> Groups = new List<GroupResult>();
        private static GroupResult _current;

        internal static int TotalChecks
        {
            get
            {
                int n = 0;
                foreach (var g in Groups) n += g.Checks;
                return n;
            }
        }

        internal static int TotalFailures
        {
            get
            {
                int n = 0;
                foreach (var g in Groups) n += g.Failures;
                return n;
            }
        }

        /// <summary>Starts a group of checks (one line of the report).</summary>
        internal static void Group(string name)
        {
            _current = new GroupResult { Name = name };
            Groups.Add(_current);
        }

        private static GroupResult Current
        {
            get
            {
                if (_current == null) Group("(no group)");
                return _current;
            }
        }

        /// <summary>Counts one check; a failure is listed (up to the cap) with <paramref name="detail"/>.</summary>
        internal static bool Check(bool ok, string what, string detail)
        {
            var g = Current;
            g.Checks++;
            if (ok) return true;
            g.Failures++;
            if (g.Listed.Count < MaxListedPerGroup)
            {
                g.Listed.Add(detail == null ? what : what + ": " + detail);
            }
            return false;
        }

        /// <summary>Records a failure that is not a comparison (an unexpected exception, for example).</summary>
        internal static void Fail(string what)
        {
            Check(false, what, null);
        }

        internal static void Crashed(string suite, Exception ex)
        {
            if (_current == null || _current.Name != suite) Group(suite);
            Fail("the suite stopped with an exception: " + ex.GetType().Name + ": " + Show(ex.Message) +
                 " at " + Show(FirstLine(ex.StackTrace)));
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return (i < 0 ? s : s.Substring(0, i)).Trim();
        }

        internal static bool True(bool condition, string what)
        {
            return Check(condition, what, null);
        }

        internal static bool False(bool condition, string what)
        {
            return Check(!condition, what, null);
        }

        internal static bool Equal(string expected, string actual, string what)
        {
            bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
            return Check(ok, what, ok ? null : "expected [" + Show(expected) + "] but was [" + Show(actual) + "]");
        }

        internal static bool Equal(int expected, int actual, string what)
        {
            bool ok = expected == actual;
            return Check(ok, what, ok ? null : "expected " + expected + " but was " + actual);
        }

        internal static bool EqualInts(int[] expected, int[] actual, string what)
        {
            bool ok = SameInts(expected, actual);
            return Check(ok, what, ok ? null : "expected " + Show(expected) + " but was " + Show(actual));
        }

        private static bool SameInts(int[] a, int[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>Expects <paramref name="actual"/> to be null (a validator that found nothing to complain about).</summary>
        internal static bool Null(string actual, string what)
        {
            return Check(actual == null, what, actual == null ? null : "expected null but was [" + Show(actual) + "]");
        }

        /// <summary>Expects a non-null text that contains <paramref name="part"/> (case-insensitive).</summary>
        internal static bool Contains(string actual, string part, string what)
        {
            bool ok = actual != null && actual.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
            return Check(ok, what, ok ? null : "expected [" + Show(actual) + "] to contain [" + Show(part) + "]");
        }

        /// <summary>ASCII rendering of a text: non-ASCII characters and control characters as \uXXXX.</summary>
        internal static string Show(string s)
        {
            if (s == null) return "<null>";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string Show(int[] values)
        {
            if (values == null) return "<null>";
            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>The code points of a text as hexadecimal numbers, for readable failure messages.</summary>
        internal static string Hex(string s)
        {
            if (s == null) return "<null>";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
