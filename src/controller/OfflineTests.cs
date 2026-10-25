// File-only regression checks. Never constructs a form, native runtime, session, job or child process.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MiuiCompatExperiment
{
    internal static class OfflineTests
    {
        private static readonly List<object> Results = new List<object>();

        private static void ExpectReject(string name, Action action, string fragment)
        {
            try { action(); throw new InvalidOperationException("Expected rejection did not occur: " + name); }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) throw;
                Results.Add(new Dictionary<string, object> { { "name", name }, { "passed", true }, { "reason", ex.Message } });
            }
        }

        private static int Main(string[] args)
        {
            string candidate = Path.GetFullPath(args[0]);
            string scratch = Path.Combine(Program.BaseDirectory, "offline-fixtures-" + Guid.NewGuid().ToString("N"));
            string extra = Path.Combine(candidate, "offline-unexpected-" + Guid.NewGuid().ToString("N") + ".dll");
            Directory.CreateDirectory(scratch);
            var report = new Dictionary<string, object> { { "vendorDllsExecuted", false }, { "uiStarted", false }, { "results", Results } };
            try
            {
                Candidate.Validate(candidate);
                Results.Add(new Dictionary<string, object> { { "name", "reviewed candidate accepted" }, { "passed", true } });
                ExpectReject("original installation refused", delegate { Candidate.Validate(Program.OriginalDirectory); }, "候选目录不能指向原安装");
                ExpectReject("missing executable refused", delegate { Candidate.Validate(scratch); }, "白名单文件缺失");
                string copy = Path.Combine(scratch, "AIoT.exe");
                File.Copy(Path.Combine(candidate, "AIoT.exe"), copy);
                using (FileStream file = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
                {
                    file.Position = file.Length - 1;
                    int value = file.ReadByte();
                    file.Position = file.Length - 1;
                    file.WriteByte((byte)(value ^ 1));
                }
                ExpectReject("one-byte executable change refused", delegate { Candidate.Validate(scratch); }, "候选文件哈希不匹配");
                File.Delete(copy);
                using (FileStream file = new FileStream(extra, FileMode.CreateNew)) file.WriteByte(0);
                ExpectReject("unlisted executable refused", delegate { Candidate.Validate(candidate); }, "未经核查的可执行文件");
                File.Delete(extra);
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    bool cancelled = false;
                    try { Candidate.Validate(candidate, cancellation.Token, null); }
                    catch (OperationCanceledException) { cancelled = true; }
                    if (!cancelled) throw new InvalidOperationException("Cancellation was ignored.");
                    Results.Add(new Dictionary<string, object> { { "name", "cancelled scan stops before vendor loading" }, { "passed", true } });
                }
                ExpectReject("mixed execution modes refused", delegate { Options.Parse(new string[] { "--offline-self-test", "--runtime-host" }); }, "运行模式不能同时选择");
                ExpectReject("orphan runtime request refused", delegate { Options.Parse(new string[] { "--runtime-host" }); }, "完整会话参数");
                ExpectReject("report path escape refused", delegate { Options.Parse(new string[] { "--offline-self-test", "--report", Path.Combine(Path.GetPathRoot(candidate), "outside.json") }); }, "诊断报告必须保存在控制器目录内");
                Options direct = Options.Parse(new string[0]);
                if (direct.Diagnostics || direct.SelfTest || direct.RuntimeHost || direct.UiHost)
                    throw new InvalidOperationException("Default entry must open the native app directly.");
                Results.Add(new Dictionary<string, object> { { "name", "default entry selects direct native startup" }, { "passed", true } });
                if (!Options.Parse(new string[] { "--diagnostics" }).Diagnostics)
                    throw new InvalidOperationException("Diagnostics must require an explicit option.");
                Results.Add(new Dictionary<string, object> { { "name", "intermediate diagnostics require explicit opt-in" }, { "passed", true } });
                ExpectReject("offline mode cannot initialize diagnostics UI", delegate { Options.Parse(new string[] { "--offline-self-test", "--diagnostics" }); }, "运行模式不能同时选择");
                report["success"] = true;
                report["passedCount"] = Results.Count;
                Json.Write(Path.Combine(Program.BaseDirectory, "offline-regressions.json"), report);
                return 0;
            }
            catch (Exception ex)
            {
                report["success"] = false;
                report["error"] = ex.ToString();
                Json.Write(Path.Combine(Program.BaseDirectory, "offline-regressions.json"), report);
                return 1;
            }
            finally
            {
                // All targets are exact names created here, with no recursive deletion or path recomposition.
                if (File.Exists(extra)) File.Delete(extra);
                string copy = Path.Combine(scratch, "AIoT.exe");
                if (File.Exists(copy)) File.Delete(copy);
                Directory.Delete(scratch, false);
            }
        }
    }
}
