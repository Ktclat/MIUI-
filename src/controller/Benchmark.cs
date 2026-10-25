// Offline file-validation benchmark. Does not construct UI, sessions or vendor hosts.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MiuiCompatExperiment
{
    internal static class Benchmark
    {
        private static int Main(string[] args)
        {
            string candidate = Path.GetFullPath(args[0]);
            string report = Path.Combine(Program.BaseDirectory, args[1]);
            if (!Program.Within(report, Program.BaseDirectory)) throw new ArgumentException("Report must be local.");
            int repeats = args.Length > 2 ? Int32.Parse(args[2]) : 3;
            var rows = new List<object>();
            for (int i = -1; i < repeats; i++)
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    TimeSpan beforeCpu = current.TotalProcessorTime;
                    Stopwatch clock = Stopwatch.StartNew();
                    Dictionary<string, object> result = Candidate.Validate(candidate);
                    clock.Stop();
                    current.Refresh();
                    rows.Add(new Dictionary<string, object> {
                        { "warmup", i == -1 }, { "iteration", i + 1 },
                        { "elapsedMs", clock.Elapsed.TotalMilliseconds },
                        { "cpuMs", (current.TotalProcessorTime - beforeCpu).TotalMilliseconds },
                        { "peakWorkingSetBytes", current.PeakWorkingSet64 },
                        { "allowlist", result["allowlist"] }
                    });
                }
            }
            Json.Write(report, new Dictionary<string, object> {
                { "scope", "Complete offline Candidate.Validate; Windows file cache not flushed" },
                { "vendorDllsExecuted", false }, { "uiStarted", false },
                { "candidate", candidate }, { "runtime", Environment.Version.ToString() },
                { "rows", rows }
            });
            Console.WriteLine(File.ReadAllText(report));
            return 0;
        }
    }
}
