// Synthetic file-only tests. No vendor app directory, DLL execution, GUI or hosts.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MiuiCompatExperiment
{
    internal static class PureSourceTests
    {
        private static readonly List<object> Results = new List<object>();

        private static void Pass(string name)
        {
            Results.Add(new Dictionary<string, object> { { "name", name }, { "passed", true } });
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { Pass(name); return; }
            throw new InvalidOperationException("Expected rejection: " + name);
        }

        private static string ReferenceHash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static int ReferenceCount(byte[] bytes, byte[] pattern)
        {
            int count = 0;
            for (int i = 0; i <= bytes.Length - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && bytes[i + j] == pattern[j]) j++;
                if (j == pattern.Length) count++;
            }
            return count;
        }

        private static void CheckScan(string fixture, byte[] bytes, byte[][] patterns, string name)
        {
            File.WriteAllBytes(fixture, bytes);
            Candidate.FileScanResult scan = Candidate.InspectFile(fixture, CancellationToken.None, patterns);
            Require(scan.Hash == ReferenceHash(bytes), "SHA-256 mismatch: " + name);
            Require(scan.Matches.Length == patterns.Length, "Pattern count mismatch.");
            for (int i = 0; i < patterns.Length; i++)
                Require(scan.Matches[i] == ReferenceCount(bytes, patterns[i]), "Boundary/overlap mismatch: " + name + "/" + i);
            Pass(name);
        }

        private static int Main()
        {
            string scratch = Path.Combine(Program.BaseDirectory, "pure-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            string fixture = Path.Combine(scratch, "synthetic.bin");
            string copy = Path.Combine(scratch, "copy.bin");
            var report = new Dictionary<string, object> {
                { "scope", "Own synthetic files and argument parsing only" },
                { "vendorDirectoryRequired", false }, { "vendorDllsExecuted", false },
                { "uiStarted", false }, { "results", Results }
            };
            try
            {
                CheckScan(fixture, new byte[0], new byte[][] { new byte[] { 0 } }, "empty file SHA-256 and zero matches");
                Require(Candidate.Sha256(fixture) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "Empty known vector mismatch.");
                CheckScan(fixture, Encoding.ASCII.GetBytes("abc"), new byte[][] { Encoding.ASCII.GetBytes("abc") }, "abc known vector and full match");
                Require(Candidate.Sha256(fixture) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "abc known vector mismatch.");
                CheckScan(fixture, Encoding.ASCII.GetBytes("aaaaabaaba"), new byte[][] {
                    Encoding.ASCII.GetBytes("aa"), Encoding.ASCII.GetBytes("aaa"),
                    Encoding.ASCII.GetBytes("aaba"), Encoding.ASCII.GetBytes("a")
                }, "overlapping and one-byte patterns");
                byte[][] pipes = {
                    Encoding.ASCII.GetBytes(Program.IsolatedPipe),
                    Encoding.ASCII.GetBytes(Program.OriginalPipe),
                    Encoding.Unicode.GetBytes(Program.OriginalPipe)
                };
                byte[] boundary = new byte[3 * 1024 * 1024 + 173];
                new Random(731).NextBytes(boundary);
                Array.Copy(pipes[0], 0, boundary, 1024 * 1024 - 13, pipes[0].Length);
                Array.Copy(pipes[1], 0, boundary, 2 * 1024 * 1024 - 1, pipes[1].Length);
                Array.Copy(pipes[2], 0, boundary, 3 * 1024 * 1024 - 45, pipes[2].Length);
                CheckScan(fixture, boundary, pipes, "ASCII and UTF-16 pipes spanning three read boundaries");
                byte[] repeated = new byte[2 * 1024 * 1024 + 7];
                for (int i = 0; i < repeated.Length; i++) repeated[i] = 65;
                CheckScan(fixture, repeated, new byte[][] { new byte[] { 65 }, new byte[] { 65, 65, 65 } },
                    "carried-tail matches counted once including overlaps");
                Random random = new Random(112358);
                for (int trial = 0; trial < 32; trial++)
                {
                    byte[] bytes = new byte[random.Next(0, 32768)];
                    random.NextBytes(bytes);
                    byte[][] patterns = new byte[4][];
                    for (int n = 0; n < patterns.Length; n++)
                    {
                        patterns[n] = new byte[random.Next(1, 65)];
                        random.NextBytes(patterns[n]);
                        if (bytes.Length >= patterns[n].Length)
                            Array.Copy(patterns[n], 0, bytes, random.Next(bytes.Length - patterns[n].Length + 1), patterns[n].Length);
                    }
                    CheckScan(fixture, bytes, patterns, "seeded random match/reference " + trial);
                }
                Reject<ArgumentException>(delegate { Candidate.InspectFile(fixture, CancellationToken.None, new byte[][] { new byte[0] }); }, "empty pattern rejected");
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    Reject<OperationCanceledException>(delegate { Candidate.InspectFile(fixture, cancellation.Token, pipes); }, "cancelled scan reads no fixture");
                    Reject<OperationCanceledException>(delegate { Candidate.CopyVerifiedExecutable(fixture, copy, cancellation.Token); }, "cancelled copy creates no destination");
                    Require(!File.Exists(copy), "Cancelled copy wrote a destination.");
                }
                Require(Candidate.CopyVerifiedExecutable(fixture, copy, CancellationToken.None), "Missing copy was not created.");
                Require(Candidate.Sha256(fixture) == Candidate.Sha256(copy), "Created copy failed hash equality.");
                Pass("missing copy created and verified");
                using (FileStream locked = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Require(!Candidate.CopyVerifiedExecutable(fixture, copy, CancellationToken.None), "Equal copy was rewritten despite read-only sharing.");
                Pass("equal full-content copy skips write while write access is denied");
                byte[] stale = File.ReadAllBytes(copy);
                Require(stale.Length != 0, "Random fixture unexpectedly empty.");
                stale[stale.Length / 2] ^= 1;
                File.WriteAllBytes(copy, stale);
                File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(fixture));
                Require(new FileInfo(copy).Length == new FileInfo(fixture).Length, "Size fixture mismatch.");
                Require(Candidate.CopyVerifiedExecutable(fixture, copy, CancellationToken.None), "Same metadata hid a changed byte.");
                Require(Candidate.Sha256(fixture) == Candidate.Sha256(copy), "Changed copy was not repaired.");
                Pass("same-size same-timestamp changed byte forces verified replacement");
                Options direct = Options.Parse(new string[0]);
                Require(!direct.Diagnostics && !direct.SelfTest && !direct.RuntimeHost && !direct.UiHost, "Default entry changed.");
                Pass("default entry remains direct native startup");
                Require(Options.Parse(new string[] { "--diagnostics" }).Diagnostics, "Diagnostics opt-in missing.");
                Pass("diagnostics remain explicit opt-in");
                Reject<ArgumentException>(delegate { Options.Parse(new string[] { "--offline-self-test", "--diagnostics" }); }, "mixed modes rejected before UI");
                Reject<ArgumentException>(delegate { Options.Parse(new string[] { "--runtime-host" }); }, "orphan runtime mode rejected");
                Reject<ArgumentException>(delegate { Options.Parse(new string[] { "--ui-host" }); }, "orphan UI mode rejected");
                Reject<ArgumentException>(delegate { Options.Parse(new string[] { "--unknown" }); }, "unknown incomplete argument rejected");
                report["success"] = true;
                report["passedCount"] = Results.Count;
                Json.Write(Path.Combine(Program.BaseDirectory, "pure-source-tests.json"), report);
                Console.WriteLine("PASS " + Results.Count + " pure source tests; no vendor or GUI execution.");
                return 0;
            }
            catch (Exception ex)
            {
                report["success"] = false;
                report["error"] = ex.ToString();
                Json.Write(Path.Combine(Program.BaseDirectory, "pure-source-tests.json"), report);
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                if (File.Exists(fixture)) File.Delete(fixture);
                if (File.Exists(copy)) File.Delete(copy);
                Directory.Delete(scratch, false);
            }
        }
    }
}
