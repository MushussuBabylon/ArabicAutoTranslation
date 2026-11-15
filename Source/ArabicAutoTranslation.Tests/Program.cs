using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using ArabicAutoTranslation.Arabic;

namespace ArabicAutoTranslation.Tests
{
    /// <summary>
    /// Test runner of the Arabic engine. No test framework: every suite is a static method that records its checks in
    /// <see cref="T"/>; the runner prints a report (also written to the file given with --report) and returns 1 when any
    /// check failed, so the CI workflow stops before building.
    ///
    ///     dotnet run --project Source/ArabicAutoTranslation.Tests/ArabicAutoTranslation.Tests.csproj -c Release -f net8.0 -- --report build/test-report.txt
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string reportPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--report" && i + 1 < args.Length)
                {
                    reportPath = args[i + 1];
                    i++;
                }
            }

            var suites = new List<KeyValuePair<string, Action>>();
            suites.Add(new KeyValuePair<string, Action>("tables", TableTests.Run));
            suites.Add(new KeyValuePair<string, Action>("shaping", ShapingTests.Run));
            suites.Add(new KeyValuePair<string, Action>("bidi", BidiTests.Run));
            suites.Add(new KeyValuePair<string, Action>("display", DisplayTests.Run));
            suites.Add(new KeyValuePair<string, Action>("text helpers", TextTests.Run));
            suites.Add(new KeyValuePair<string, Action>("pronoun variables", PronounTests.Run));
            suites.Add(new KeyValuePair<string, Action>("glossary", GlossaryTests.Run));
            suites.Add(new KeyValuePair<string, Action>("pipeline", PipelineTests.Run));
            suites.Add(new KeyValuePair<string, Action>("cache", CacheTests.Run));

            var watch = Stopwatch.StartNew();
            foreach (var suite in suites)
            {
                try
                {
                    suite.Value();
                }
                catch (Exception ex)
                {
                    T.Crashed(suite.Key, ex);
                }
            }
            watch.Stop();

            string report = BuildReport(watch.Elapsed);
            Console.Write(report);
            if (!string.IsNullOrEmpty(reportPath))
            {
                try
                {
                    string full = Path.GetFullPath(reportPath);
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(full, report, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("cannot write the report file: " + ex.Message);
                }
            }
            return T.TotalFailures == 0 ? 0 : 1;
        }

        private static string BuildReport(TimeSpan elapsed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Arabic Auto Translation - engine test report");
            sb.AppendLine("runtime: .NET " + Environment.Version + ", " + (IntPtr.Size * 8) + " bit, " + Environment.OSVersion.VersionString);
            sb.AppendLine("started: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC (run time " + elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s)");
            sb.AppendLine();

            int nameWidth = 10;
            foreach (var g in T.Groups)
            {
                if (g.Name.Length > nameWidth) nameWidth = g.Name.Length;
            }

            foreach (var g in T.Groups)
            {
                sb.Append(g.Failures == 0 ? "  PASS  " : "  FAIL  ");
                sb.Append(g.Name.PadRight(nameWidth));
                sb.Append("  ");
                sb.Append((g.Checks - g.Failures) + "/" + g.Checks);
                sb.AppendLine();
            }
            sb.AppendLine();

            foreach (var g in T.Groups)
            {
                if (g.Failures == 0) continue;
                sb.AppendLine("failures in \"" + g.Name + "\" (" + g.Failures + (g.Failures > g.Listed.Count ? ", first " + g.Listed.Count + " shown" : "") + "):");
                foreach (var line in g.Listed) sb.AppendLine("  - " + line);
                sb.AppendLine();
            }

            sb.AppendLine("checks: " + T.TotalChecks + ", failed: " + T.TotalFailures);
            sb.AppendLine(T.TotalFailures == 0 ? "RESULT: PASSED" : "RESULT: FAILED");
            return sb.ToString();
        }
    }
}
