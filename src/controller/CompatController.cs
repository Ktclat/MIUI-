using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("MIUI-")]
[assembly: AssemblyProduct("MIUI-")]
[assembly: AssemblyDescription("MIUI- personal-study native interconnection host")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

namespace MiuiCompatExperiment
{
    internal static class Program
    {
        internal const string ExpectedVersion = "3.1.494";
        internal const string OriginalDirectory = @"C:\Program Files\MI\AIoT\MiShare\2.5.5.643";
        internal const string OriginalPipe = @"\\.\Pipe\ADF09CB1-7CE4-470A-9C40-7EC6864F6A0F";
        internal const string IsolatedPipe = @"\\.\Pipe\F2EE729F-D41F-4A74-9E8A-C78092614DC2";
        internal const string MediaPipe = @"\\.\pipe\F7139760-383D-4B43-91A4-976F7F2E1D85";
        internal const string UiArgumentsVariable = "MIUI_COMPAT_PRIVATE_UI_ARGUMENTS";
        internal const string ActivateEvent = @"Local\MIUIMinusActivate_F2EE729F_D41F_4A74_9E8A_C78092614DC2";
        internal static readonly string BaseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        internal static readonly string ExePath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Options options = Options.Parse(args);
                if (options.SelfTest) return SelfTest(options);
                if (options.RuntimeHost) return RuntimeHost.Run(options);
                if (options.UiHost) return UiHost.Run(options);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (Mutex instance = new Mutex(false, @"Local\MIUIMinusController_F2EE729F_D41F_4A74_9E8A_C78092614DC2"))
                {
                    bool acquired;
                    try { acquired = instance.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        try
                        {
                            using (EventWaitHandle activate = EventWaitHandle.OpenExisting(ActivateEvent)) activate.Set();
                        }
                        catch (WaitHandleCannotBeOpenedException)
                        {
                            MessageBox.Show("MIUI- 已在运行，请使用已打开的窗口或托盘图标。", "MIUI-", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        return 0;
                    }
                    try
                    {
                        if (options.Diagnostics) Application.Run(new ControllerForm(options.Candidate));
                        else using (var context = new DirectApplicationContext(options.Candidate)) Application.Run(context);
                    }
                    finally { instance.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex)
            {
                string path = Path.Combine(BaseDirectory, "startup-error.json");
                Json.Write(path, new Dictionary<string, object> { { "error", ex.ToString() }, { "utc", DateTime.UtcNow.ToString("o") } });
                if (Array.IndexOf(args, "--runtime-host") < 0 && Array.IndexOf(args, "--ui-host") < 0 && Array.IndexOf(args, "--self-test") < 0 && Array.IndexOf(args, "--offline-self-test") < 0)
                    MessageBox.Show(ex.Message, "MIUI-", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int SelfTest(Options options)
        {
            var report = new Dictionary<string, object>();
            report["mode"] = "offline-self-test";
            report["startRequested"] = false;
            report["uiStarted"] = false;
            report["vendorDllsExecuted"] = false;
            report["pid"] = Process.GetCurrentProcess().Id;
            report["utc"] = DateTime.UtcNow.ToString("o");
            try
            {
                report["phase"] = "validating-files";
                Json.Write(options.Report, report);
                report["staticChecks"] = Candidate.Validate(options.Candidate, CancellationToken.None, delegate(string progress)
                {
                    report["progress"] = progress;
                    Json.Write(options.Report, report);
                });
                report["phase"] = "complete";
                report["success"] = true;
                // Strictly offline: no LoadLibrary, Start, Stop, logger, pipe, radio or UI calls.
                Json.Write(options.Report, report);
                return 0;
            }
            catch (Exception ex)
            {
                report["success"] = false;
                report["phase"] = "failed";
                report["error"] = ex.ToString();
                Json.Write(options.Report, report);
                return 1;
            }
        }

        internal static bool SamePath(string a, string b)
        {
            return String.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        internal static bool Within(string path, string directory)
        {
            string root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class Options
    {
        internal bool SelfTest, RuntimeHost, UiHost, Diagnostics;
        internal string Candidate = Directory.Exists(Path.Combine(Program.BaseDirectory, "app"))
            ? Path.GetFullPath(Path.Combine(Program.BaseDirectory, "app"))
            : Path.GetFullPath(Path.Combine(Program.BaseDirectory, "..", "MIUI-Plus-staging"));
        internal string Report = Path.Combine(Program.BaseDirectory, "self-test.json");
        internal string SessionDirectory, StopEvent;
        internal string OwnerExe = Program.ExePath;
        internal int ParentPid;
        internal long ParentStartTicks;

        internal static Options Parse(string[] args)
        {
            // MiDrop reads GetCommandLineW itself. Keep its process command line free of host options.
            if (args.Length == 0 && Path.GetFileName(Program.ExePath).Equals("MIUI-Compat-UIHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                string payload = Environment.GetEnvironmentVariable(Program.UiArgumentsVariable);
                Environment.SetEnvironmentVariable(Program.UiArgumentsVariable, null);
                if (String.IsNullOrEmpty(payload)) throw new ArgumentException("请从主目录启动原生适配，不要直接打开 app 内的组件。");
                args = new JavaScriptSerializer().Deserialize<string[]>(payload);
                if (args == null || Array.IndexOf(args, "--ui-host") < 0) throw new ArgumentException("缺少内部 UI 会话参数。");
            }
            Options result = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (key == "--self-test" || key == "--offline-self-test") result.SelfTest = true;
                else if (key == "--runtime-host") result.RuntimeHost = true;
                else if (key == "--ui-host") result.UiHost = true;
                else if (key == "--diagnostics") result.Diagnostics = true;
                else
                {
                    if (++i >= args.Length) throw new ArgumentException("缺少参数值：" + key);
                    string value = args[i];
                    switch (key)
                    {
                        case "--candidate-dir": result.Candidate = Path.GetFullPath(value); break;
                        case "--report": result.Report = Path.GetFullPath(value); break;
                        case "--session-dir": result.SessionDirectory = Path.GetFullPath(value); break;
                        case "--stop-event": result.StopEvent = value; break;
                        case "--parent-pid": result.ParentPid = Int32.Parse(value); break;
                        case "--parent-start-ticks": result.ParentStartTicks = Int64.Parse(value); break;
                        case "--owner-exe": result.OwnerExe = Path.GetFullPath(value); break;
                        default: throw new ArgumentException("未知参数：" + key);
                    }
                }
            }
            if ((result.SelfTest ? 1 : 0) + (result.RuntimeHost ? 1 : 0) + (result.UiHost ? 1 : 0) + (result.Diagnostics ? 1 : 0) > 1) throw new ArgumentException("运行模式不能同时选择。");
            if (!Program.Within(result.Report, Program.BaseDirectory)) throw new ArgumentException("诊断报告必须保存在控制器目录内。");
            if (result.RuntimeHost || result.UiHost)
            {
                if (result.ParentPid <= 0 || result.ParentStartTicks <= 0 || String.IsNullOrEmpty(result.StopEvent) || String.IsNullOrEmpty(result.SessionDirectory))
                    throw new ArgumentException("runtime-host 只能由控制器携带完整会话参数启动。");
                if (!Program.Within(result.SessionDirectory, Path.Combine(Path.GetDirectoryName(result.OwnerExe), "diagnostics")))
                    throw new ArgumentException("运行状态目录不在控制器 diagnostics 内。");
                if (!result.StopEvent.StartsWith("Local\\MIUICompatStop_", StringComparison.Ordinal))
                    throw new ArgumentException("无效的停止事件。");
            }
            return result;
        }
    }

    internal static class Candidate
    {
        private const int ScanBufferSize = 1024 * 1024;
        private static readonly byte[][] IdmPipePatterns = {
            Encoding.ASCII.GetBytes(Program.IsolatedPipe),
            Encoding.ASCII.GetBytes(Program.OriginalPipe),
            Encoding.Unicode.GetBytes(Program.OriginalPipe)
        };

        internal sealed class FileScanResult
        {
            internal string Hash;
            internal int[] Matches;
        }

        internal static Dictionary<string, object> Validate(string root)
        {
            return Validate(root, CancellationToken.None, null);
        }

        internal static Dictionary<string, object> Validate(string root, CancellationToken cancel, Action<string> progress)
        {
            root = Path.GetFullPath(root);
            if (Program.SamePath(root, Program.OriginalDirectory) || Program.Within(root, Program.OriginalDirectory))
                throw new InvalidOperationException("候选目录不能指向原安装。");
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            // Reject junctions/symlinks so the displayed candidate cannot redirect to the original installation.
            DirectoryInfo cursor = new DirectoryInfo(root);
            while (cursor != null)
            {
                if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("候选路径不能经过联接或符号链接：" + cursor.FullName);
                cursor = cursor.Parent;
            }
            var result = new Dictionary<string, object>();
            result["candidateDirectory"] = root;
            result["isolatedPipe"] = Program.IsolatedPipe;
            var validatedFiles = new Dictionary<string, FileScanResult>(StringComparer.OrdinalIgnoreCase);
            result["allowlist"] = VerifyAllowlist(root, cancel, progress, validatedFiles);
            var files = new List<object>();
            foreach (string name in new string[] { "AIoT.exe", "MiDrop.dll", "idmsdk.dll", "idmruntime.dll", "MiSSO.dll" })
            {
                cancel.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(root, name));
                if (!File.Exists(path)) throw new FileNotFoundException("候选文件缺失", path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("候选文件不能是链接：" + path);
                string version = FileVersionInfo.GetVersionInfo(path).FileVersion;
                FileScanResult inspected = validatedFiles[path];
                var row = new Dictionary<string, object> { { "path", path }, { "fileVersion", version }, { "sha256", inspected.Hash } };
                if (name == "idmsdk.dll" || name == "idmruntime.dll")
                {
                    // These counts came from the SAME byte stream as the full SHA-256.
                    // Do not reread either runtime DLL into a large temporary array.
                    int isolated = inspected.Matches[0];
                    int original = inspected.Matches[1] + inspected.Matches[2];
                    if (isolated != 1 || original != 0) throw new InvalidOperationException(name + " 的隔离管道校验失败。");
                    if (version != Program.ExpectedVersion) throw new InvalidOperationException(name + " 版本不是 " + Program.ExpectedVersion);
                    row["isolatedPipeCount"] = isolated;
                    row["originalPipeCount"] = original;
                }
                if (name == "MiSSO.dll" && !String.Equals((string)row["sha256"], "b4346306bba98f8bd5b378554466cd1ce85285caf1a9c597068f9b431304b6e5", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("MiSSO.dll 不是已核查的新版文件；旧版接口不兼容。");
                files.Add(row);
            }
            result["files"] = files;
            result["mediaEndpoints"] = VerifyMediaEndpoints(root, cancel);
            return result;
        }

        private static object VerifyMediaEndpoints(string root, CancellationToken cancel)
        {
            // All three endpoints must use the same private media pipe. The file allowlist
            // above validates the complete binaries; this check explains a broken chain.
            string[] names = { "MiDrop.dll", "MiScreenShare.exe", "MiScreenShareGuide.exe" };
            long[] offsets = { 0x31EEE0, 0x1B8D10, 0x297F60 };
            byte[] expected = Encoding.Unicode.GetBytes(Program.MediaPipe + "\0");
            var rows = new List<object>();
            for (int i = 0; i < names.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                byte[] actual = new byte[expected.Length];
                using (var file = new FileStream(Path.Combine(root, names[i]), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    file.Seek(offsets[i], SeekOrigin.Begin);
                    int total = 0;
                    while (total < actual.Length)
                    {
                        int count = file.Read(actual, total, actual.Length - total);
                        if (count == 0) throw new EndOfStreamException(names[i] + " 的媒体通道数据不完整。");
                        total += count;
                    }
                }
                for (int n = 0; n < actual.Length; n++)
                    if (actual[n] != expected[n]) throw new InvalidOperationException(names[i] + " 与本次媒体通道不匹配。");
                rows.Add(new Dictionary<string, object> { { "file", names[i] }, { "role", i == 2 ? "server" : "client" }, { "pipe", Program.MediaPipe } });
            }
            return rows;
        }

        internal static string Sha256(string path)
        {
            return Sha256(path, CancellationToken.None);
        }

        private static string Sha256(string path, CancellationToken cancel)
        {
            return InspectFile(path, cancel, null).Hash;
        }

        internal static FileScanResult InspectFile(string path, CancellationToken cancel, byte[][] patterns)
        {
            return InspectFile(path, cancel, patterns, null);
        }

        private static FileScanResult InspectFile(string path, CancellationToken cancel, byte[][] patterns, byte[] sharedBuffer)
        {
            cancel.ThrowIfCancellationRequested();
            PatternSearch[] searches = new PatternSearch[patterns == null ? 0 : patterns.Length];
            int overlap = 0;
            for (int i = 0; i < searches.Length; i++)
            {
                searches[i] = new PatternSearch(patterns[i]);
                overlap = Math.Max(overlap, patterns[i].Length - 1);
            }
            byte[] buffer = sharedBuffer ?? new byte[ScanBufferSize + overlap];
            if (buffer.Length < ScanBufferSize + overlap) throw new ArgumentException("Scan buffer is too small.");
            int[] matches = new int[searches.Length];
            // CNG performs the same complete SHA-256 through the Windows crypto
            // provider; there is no metadata cache and no skipped file region.
            using (SHA256 sha = new SHA256Cng())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ScanBufferSize, FileOptions.SequentialScan))
            {
                int carried = 0;
                int count;
                while ((count = stream.Read(buffer, carried, ScanBufferSize)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, carried, count, buffer, carried);
                    int available = carried + count;
                    for (int i = 0; i < searches.Length; i++)
                        matches[i] = checked(matches[i] + searches[i].Count(buffer, available, carried));
                    carried = Math.Min(overlap, available);
                    if (carried != 0) Buffer.BlockCopy(buffer, available - carried, buffer, 0, carried);
                }
                cancel.ThrowIfCancellationRequested();
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return new FileScanResult { Hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant(), Matches = matches };
            }
        }

        private sealed class PatternSearch
        {
            private readonly byte[] pattern;
            private readonly int[] shifts = new int[256];

            internal PatternSearch(byte[] value)
            {
                if (value == null || value.Length == 0 || value.Length > ScanBufferSize)
                    throw new ArgumentException("A nonempty bounded pattern is required.");
                pattern = value;
                for (int i = 0; i < shifts.Length; i++) shifts[i] = pattern.Length;
                for (int i = 0; i < pattern.Length - 1; i++) shifts[pattern[i]] = pattern.Length - 1 - i;
            }

            internal int Count(byte[] bytes, int length, int alreadyScanned)
            {
                int count = 0;
                // Count a boundary-spanning match once; matches wholly in the
                // carried tail were already counted in the previous chunk.
                int at = Math.Max(0, alreadyScanned - pattern.Length + 1);
                while (at <= length - pattern.Length)
                {
                    int j = pattern.Length - 1;
                    while (j >= 0 && bytes[at + j] == pattern[j]) j--;
                    if (j < 0) { count++; at++; } // Include overlapping matches.
                    else at += shifts[bytes[at + pattern.Length - 1]];
                }
                return count;
            }
        }

        internal static bool CopyVerifiedExecutable(string source, string destination, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            string sourceHash = Sha256(source, cancel);
            if (File.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("UI host 目标不能是链接。");
                if (String.Equals(Sha256(destination, cancel), sourceHash, StringComparison.Ordinal)) return false;
            }
            cancel.ThrowIfCancellationRequested();
            File.Copy(source, destination, true);
            if (!String.Equals(Sha256(destination, cancel), sourceHash, StringComparison.Ordinal))
                throw new InvalidOperationException("UI host 副本校验失败。");
            return true;
        }

        private static Dictionary<string, object> VerifyAllowlist(string root, CancellationToken cancel, Action<string> progress, Dictionary<string, FileScanResult> inspectedFiles)
        {
            List<Dictionary<string, object>> list;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CandidateAllowlist.json"))
            {
                if (resource == null) throw new InvalidOperationException("缺少编译时固定的候选文件白名单。");
                using (StreamReader reader = new StreamReader(resource, Encoding.UTF8))
                    list = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(reader.ReadToEnd());
            }
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Reuse one small buffer for the complete allowlist instead of one
            // large-object allocation for every file. No result survives this call.
            byte[] scanBuffer = new byte[ScanBufferSize + Encoding.Unicode.GetByteCount(Program.OriginalPipe) - 1];
            foreach (Dictionary<string, object> entry in list)
            {
                cancel.ThrowIfCancellationRequested();
                string relative = Convert.ToString(entry["path"]);
                if (progress != null) progress(relative);
                string file = Path.GetFullPath(Path.Combine(root, relative));
                if (!Program.Within(file, root)) throw new InvalidOperationException("白名单路径越界。");
                if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("白名单文件缺失或是链接：" + relative);
                DirectoryInfo directory = new FileInfo(file).Directory;
                while (directory != null && Program.Within(directory.FullName, root))
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("白名单路径经过链接：" + relative);
                    directory = directory.Parent;
                }
                string name = Path.GetFileName(file);
                bool inspectPipes = String.Equals(name, "idmsdk.dll", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(name, "idmruntime.dll", StringComparison.OrdinalIgnoreCase);
                FileScanResult inspected = InspectFile(file, cancel, inspectPipes ? IdmPipePatterns : null, scanBuffer);
                if (!String.Equals(inspected.Hash, Convert.ToString(entry["sha256"]), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("候选文件哈希不匹配：" + relative);
                allowed.Add(file);
                inspectedFiles.Add(file, inspected);
            }
            foreach (string file in EnumerateFiles(root, cancel))
            {
                string ext = Path.GetExtension(file);
                if (!String.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase) && !String.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (Program.SamePath(file, Path.Combine(root, "MIUI-Compat-UIHost.exe"))) continue;
                if (!allowed.Contains(Path.GetFullPath(file))) throw new InvalidOperationException("候选目录含未经核查的可执行文件：" + file);
            }
            return new Dictionary<string, object> { { "verifiedFileCount", allowed.Count }, { "source", "embedded fixed SHA256 allowlist" } };
        }

        private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancel)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                string directory = pending.Pop();
                foreach (string child in System.IO.Directory.GetFileSystemEntries(directory))
                {
                    cancel.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("候选目录内不能包含链接：" + child);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                    else yield return child;
                }
            }
        }

        internal static void RejectExistingUi()
        {
            // Guide can remain after the visible AIoT window exits. Old native code
            // locates and terminates media helpers by name, so check the whole family.
            foreach (string name in new string[] { "AIoT", "MIUI-Compat-UIHost", "MiScreenShare", "MiScreenShareGuide", "XiaomiPcManager", "MiPcContinuity" })
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        if (process.HasExited) continue;
                        string path;
                        try { path = process.MainModule.FileName; }
                        catch (InvalidOperationException) { continue; }
                        catch { throw new InvalidOperationException("检测到互联组件 " + name + "（PID " + process.Id + "），无法核实路径。请先正常退出相关互联程序。"); }
                        throw new InvalidOperationException("互联组件仍在运行：" + name + "（PID " + process.Id + "）。请先正常退出旧版 MIUI+、其他 MIUI- 或小米电脑管家，再启动新版。\r\n" + path);
                    }
                }
            }
        }
    }

    internal sealed class NativeRuntime
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr VersionCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgCall();
        private readonly List<IntPtr> libraries = new List<IntPtr>();
        private readonly string root;
        private readonly NoArgCall start, stop, started;
        internal readonly string Version;

        internal NativeRuntime(string directory, Action<string> progress)
        {
            root = Path.GetFullPath(directory);
            if (!Native.SetDefaultDllDirectories(0x1000)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Native.SetDllDirectory(root)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (progress != null) progress("loading-MiSSO.dll");
            Load("MiSSO.dll");
            if (progress != null) progress("loading-idmruntime.dll");
            IntPtr module = Load("idmruntime.dll");
            if (progress != null) progress("loading-idmsdk.dll");
            Load("idmsdk.dll");
            if (progress != null) progress("checking-runtime-version");
            Version = Marshal.PtrToStringAnsi(Get<VersionCall>(module, "idmrt_version")());
            if (Version != Program.ExpectedVersion) throw new InvalidOperationException("runtime 实际版本不匹配：" + Version);
            start = Get<NoArgCall>(module, "idmrt_start");
            stop = Get<NoArgCall>(module, "idmrt_stop");
            started = Get<NoArgCall>(module, "idmrt_is_started");
            // Keep loaded modules alive until this bounded host exits. No unload occurs during active native work.
        }

        private IntPtr Load(string name)
        {
            string path = Path.GetFullPath(Path.Combine(root, name));
            IntPtr handle = Native.LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00001000);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 " + path);
            libraries.Add(handle);
            StringBuilder actual = new StringBuilder(32768);
            if (Native.GetModuleFileName(handle, actual, actual.Capacity) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Program.SamePath(path, actual.ToString())) throw new InvalidOperationException("DLL 实际加载路径不一致：" + actual);
            return handle;
        }

        private static T Get<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = Native.GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        internal List<string> LoadedModules()
        {
            var paths = new List<string>();
            using (Process current = Process.GetCurrentProcess())
                foreach (ProcessModule module in current.Modules)
                {
                    string n = module.ModuleName.ToLowerInvariant();
                    if (n == "idmruntime.dll" || n == "idmsdk.dll" || n == "misso.dll" || n.StartsWith("msvcp140") || n.StartsWith("vcruntime140"))
                        paths.Add(module.FileName);
                }
            return paths;
        }

        internal int Start() { return start(); }
        internal int Stop() { return stop(); }
        internal int IsStarted() { return started(); }
    }

    internal static class RuntimeHost
    {
        internal static int Run(Options options)
        {
            string statusPath = Path.Combine(options.SessionDirectory, "runtime-status.json");
            var status = new Dictionary<string, object> { { "mode", "runtime-host" }, { "pid", Process.GetCurrentProcess().Id }, { "candidate", options.Candidate } };
            NativeRuntime runtime = null;
            bool startAttempted = false;
            try
            {
                using (Process parent = Process.GetProcessById(options.ParentPid))
                using (EventWaitHandle stopEvent = EventWaitHandle.OpenExisting(options.StopEvent))
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != options.ParentStartTicks || !Program.SamePath(parent.MainModule.FileName, options.OwnerExe))
                        throw new InvalidOperationException("启动控制器的身份已改变。");
                    status["phase"] = "validating-files";
                    Json.Write(statusPath, status);
                    status["staticChecks"] = Candidate.Validate(options.Candidate);
                    runtime = new NativeRuntime(options.Candidate, delegate(string phase)
                    {
                        status["phase"] = phase;
                        Json.Write(statusPath, status);
                    });
                    status["runtimeVersion"] = runtime.Version;
                    status["loadedModules"] = runtime.LoadedModules();
                    // Do not opt ordinary users into the vendor's detailed file logger.
                    // Vendor-internal default logging is a separate, bounded audit.
                    status["nativeDetailedLogConfigured"] = false;
                    status["phase"] = "loaded";
                    Json.Write(statusPath, status);
                    if (stopEvent.WaitOne(0) || parent.HasExited) return 0;
                    startAttempted = true;
                    status["phase"] = "starting";
                    Json.Write(statusPath, status);
                    status["startReturn"] = runtime.Start();
                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                    bool ready = false;
                    while (DateTime.UtcNow < deadline && !stopEvent.WaitOne(100) && !parent.HasExited)
                    {
                        uint serverPid;
                        if (runtime.IsStarted() == 1 && Native.TryGetPipeServer(Program.IsolatedPipe, out serverPid) && serverPid == (uint)Process.GetCurrentProcess().Id)
                        {
                            status["pipeServerPid"] = serverPid;
                            status["isolatedPipe"] = Program.IsolatedPipe;
                            status["isStarted"] = 1;
                            ready = true;
                            break;
                        }
                    }
                    if (!ready) throw new InvalidOperationException("runtime 未在15秒内确认其自身隔离管道。原版后台或网络端口可能仍在冲突。");
                    status["phase"] = "ready";
                    Json.Write(statusPath, status);
                    while (!stopEvent.WaitOne(250) && !parent.HasExited) { }
                }
            }
            catch (Exception ex)
            {
                status["error"] = ex.ToString();
                status["phase"] = "failed";
                Json.Write(statusPath, status);
            }
            finally
            {
                if (runtime != null && startAttempted)
                {
                    status["phase"] = "stopping";
                    Json.Write(statusPath, status);
                    // May block in the vendor DLL. The owning controller terminates only this job after 3 seconds.
                    status["stopReturn"] = runtime.Stop();
                    status["phase"] = "stopped";
                    Json.Write(statusPath, status);
                }
            }
            return status.ContainsKey("error") ? 1 : 0;
        }
    }

    internal static class UiHost
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int MainCall(IntPtr module, IntPtr previous, IntPtr commandLine, int showCommand);

        internal static int Run(Options options)
        {
            var report = new Dictionary<string, object> { { "mode", "ui-host" }, { "pid", Process.GetCurrentProcess().Id }, { "exePath", Program.ExePath }, { "candidate", options.Candidate } };
            string reportPath = Path.Combine(options.SessionDirectory, "ui-status.json");
            try
            {
                using (Process parent = Process.GetProcessById(options.ParentPid))
                    if (parent.StartTime.ToUniversalTime().Ticks != options.ParentStartTicks || !Program.SamePath(parent.MainModule.FileName, options.OwnerExe))
                        throw new InvalidOperationException("UI host 的启动控制器身份已改变。");
                if (!Program.SamePath(Program.BaseDirectory, options.Candidate)) throw new InvalidOperationException("UI host 必须位于候选目录中运行。");
                if (!String.Equals(Candidate.Sha256(Program.ExePath), Candidate.Sha256(options.OwnerExe), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("UI host 与本次控制器二进制不一致。");
                report["phase"] = "validating-files";
                Json.Write(reportPath, report);
                report["staticChecks"] = Candidate.Validate(options.Candidate);
                using (EventWaitHandle stopped = EventWaitHandle.OpenExisting(options.StopEvent))
                    if (stopped.WaitOne(0)) return 0;
                if (!Native.SetDefaultDllDirectories(0x1000) || !Native.SetDllDirectory(options.Candidate)) throw new Win32Exception(Marshal.GetLastWin32Error());
                string path = Path.GetFullPath(Path.Combine(options.Candidate, "MiDrop.dll"));
                report["phase"] = "loading-original-ui";
                Json.Write(reportPath, report);
                IntPtr library = Native.LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000);
                if (library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载候选 MiDrop.dll");
                StringBuilder loaded = new StringBuilder(32768);
                if (Native.GetModuleFileName(library, loaded, loaded.Capacity) == 0 || !Program.SamePath(path, loaded.ToString()))
                    throw new InvalidOperationException("MiDrop.dll 实际加载路径不匹配。");
                report["loadedMiDrop"] = loaded.ToString();
                var modules = new List<string>();
                using (Process current = Process.GetCurrentProcess())
                    foreach (ProcessModule module in current.Modules)
                    {
                        if (Program.Within(module.FileName, options.Candidate)) modules.Add(module.FileName);
                        if (module.ModuleName.Equals("idmsdk.dll", StringComparison.OrdinalIgnoreCase) && !Program.SamePath(module.FileName, Path.Combine(options.Candidate, "idmsdk.dll")))
                            throw new InvalidOperationException("UI 加载了非候选 idmsdk.dll。");
                    }
                report["loadedCandidateModules"] = modules;
                IntPtr entry = Native.GetProcAddress(library, "wWinMainDll");
                if (entry == IntPtr.Zero) throw new EntryPointNotFoundException("wWinMainDll");
                MainCall main = (MainCall)Marshal.GetDelegateForFunctionPointer(entry, typeof(MainCall));
                report["phase"] = "entering-original-ui";
                Json.Write(reportPath, report);
                IntPtr emptyCommandLine = Marshal.StringToHGlobalUni("");
                int result;
                try { result = main(library, IntPtr.Zero, emptyCommandLine, 1); }
                finally { Marshal.FreeHGlobal(emptyCommandLine); }
                report["phase"] = "original-ui-returned";
                report["returnCode"] = result;
                Json.Write(reportPath, report);
                return result;
            }
            catch (Exception ex)
            {
                report["phase"] = "failed"; report["error"] = ex.ToString(); Json.Write(reportPath, report); return 1;
            }
        }
    }

    internal sealed class TestSession : IDisposable
    {
        internal readonly string DirectoryPath;
        private readonly string candidate, eventName;
        private readonly EventWaitHandle stopEvent;
        private readonly OwnedJob hostJob;
        private readonly OwnedJob uiJob;
        private readonly object gate = new object();
        private OwnedProcess host, ui;
        private Task stopTask;
        private bool disposed;
        internal readonly List<string> Messages = new List<string>();

        internal TestSession(string path)
        {
            candidate = path;
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            DirectoryPath = Path.Combine(Program.BaseDirectory, "diagnostics", id);
            Directory.CreateDirectory(DirectoryPath);
            eventName = "Local\\MIUICompatStop_" + id;
            try
            {
                stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
                hostJob = new OwnedJob();
                uiJob = new OwnedJob();
            }
            catch
            {
                if (uiJob != null) uiJob.Dispose();
                if (hostJob != null) hostJob.Dispose();
                if (stopEvent != null) stopEvent.Dispose();
                throw;
            }
        }

        internal async Task StartAsync(CancellationToken cancel)
        {
            Candidate.RejectExistingUi();
            var checks = await Task.Run(delegate { return Candidate.Validate(candidate, cancel, null); }, cancel);
            if (Native.WaitNamedPipe(Program.IsolatedPipe, 0)) throw new InvalidOperationException("检测到已有实验管道；请先结束此前实验。控制器不会终止不属于本次会话的进程。");
            Json.Write(Path.Combine(DirectoryPath, "candidate-checks.json"), checks);
            cancel.ThrowIfCancellationRequested();
            using (Process current = Process.GetCurrentProcess())
            {
                string args = "--runtime-host --candidate-dir " + Native.Quote(candidate) + " --session-dir " + Native.Quote(DirectoryPath) + " --owner-exe " + Native.Quote(Program.ExePath)
                    + " --stop-event " + Native.Quote(eventName) + " --parent-pid " + current.Id + " --parent-start-ticks " + current.StartTime.ToUniversalTime().Ticks;
                host = hostJob.Start(Program.ExePath, args, candidate, true);
            }
            Log("runtime 子进程 PID " + host.Id);
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            string lastPhase = "waiting-for-host";
            while (DateTime.UtcNow < deadline)
            {
                cancel.ThrowIfCancellationRequested();
                Dictionary<string, object> status = Json.TryRead(Path.Combine(DirectoryPath, "runtime-status.json"));
                if (status != null)
                {
                    string nextPhase = Value(status, "phase");
                    if (nextPhase != lastPhase) { lastPhase = nextPhase; Log("连接组件状态：" + lastPhase); }
                    if (status.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(status["error"]));
                    if (Value(status, "phase") == "ready")
                    {
                        if (Value(status, "runtimeVersion") != Program.ExpectedVersion || Value(status, "pipeServerPid") != host.Id.ToString() || Value(status, "isStarted") != "1")
                            throw new InvalidOperationException("runtime 就绪身份或版本校验失败。");
                        Candidate.RejectExistingUi();
                        cancel.ThrowIfCancellationRequested();
                        string uiHost = Path.GetFullPath(Path.Combine(candidate, "MIUI-Compat-UIHost.exe"));
                        Candidate.CopyVerifiedExecutable(Program.ExePath, uiHost, cancel);
                        using (Process current = Process.GetCurrentProcess())
                        {
                            string[] privateArgs = new string[] { "--ui-host", "--candidate-dir", candidate, "--session-dir", DirectoryPath,
                                "--owner-exe", Program.ExePath, "--stop-event", eventName, "--parent-pid", current.Id.ToString(),
                                "--parent-start-ticks", current.StartTime.ToUniversalTime().Ticks.ToString() };
                            ui = uiJob.Start(uiHost, "", candidate, false, privateArgs);
                        }
                        Log("已确认 runtime " + Program.ExpectedVersion + " 和隔离管道，候选 UI PID " + ui.Id);
                        DateTime uiDeadline = DateTime.UtcNow.AddSeconds(45);
                        while (DateTime.UtcNow < uiDeadline)
                        {
                            cancel.ThrowIfCancellationRequested();
                            var uiStatus = Json.TryRead(Path.Combine(DirectoryPath, "ui-status.json"));
                            if (uiStatus != null)
                            {
                                if (uiStatus.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(uiStatus["error"]));
                                if (Value(uiStatus, "phase") == "entering-original-ui" && Native.HasVisibleWindow(ui.Id))
                                {
                                    if (ui.HasExited || host.HasExited) throw new InvalidOperationException("连接组件启动后退出，请查看本次诊断。");
                                    Log("已检测到本次界面进程的可见窗口；二维码、手机确认和控制效果仍需人工验证。");
                                    return;
                                }
                            }
                            if (ui.HasExited) throw new InvalidOperationException("手机连接窗口未能启动，请查看 ui-status.json。");
                            if (host.HasExited) throw new InvalidOperationException("连接组件在等待界面期间退出，请查看 runtime-status.json。");
                            await Task.Delay(200, cancel);
                        }
                        throw new TimeoutException("手机连接窗口启动超时；本次进程将自动结束。");
                    }
                }
                if (host.HasExited) throw new InvalidOperationException("runtime 子进程已退出，候选 UI 未启动。");
                await Task.Delay(200, cancel);
            }
            throw new TimeoutException("连接组件启动超时（" + lastPhase + "），手机连接窗口未启动。");
        }

        private static string Value(Dictionary<string, object> obj, string key) { return obj.ContainsKey(key) ? Convert.ToString(obj[key]) : ""; }
        internal bool AnyExited { get { return (host != null && host.HasExited) || (ui != null && ui.HasExited); } }

        internal void ShowConnectionWindow()
        {
            // Only the exact UI-host process owned by this session can be activated.
            if (ui != null && !ui.HasExited) Native.ShowOwnedWindow(ui.Id);
        }

        internal bool ShowPhonePanel()
        {
            if (ui == null || ui.HasExited) return false;
            return Native.ShowOwnedPhonePanel(uiJob, Path.Combine(candidate, "MiScreenShareGuide.exe"));
        }

        internal Task StopAsync()
        {
            lock (gate)
            {
                if (stopTask == null) stopTask = Task.Run((Action)StopCore);
                return stopTask;
            }
        }

        private void StopCore()
        {
            // Jobs are assigned before either owned process resumes, so only this session and its descendants are affected.
            uiJob.Terminate();
            if (ui != null) Log("已结束本次候选 UI 及其子进程（根 PID " + ui.Id + "）。");
            stopEvent.Set();
            if (host != null && !host.Wait(3000))
            {
                Log("runtime 停止超过3秒，仅终止本次 runtime 子进程（PID " + host.Id + "）。");
                hostJob.Terminate();
                host.Wait(1000);
            }
            Log("本次实验已结束；没有操作系统服务或其他 MIUI- 进程。");
        }

        private void Log(string text)
        {
            lock (Messages)
            {
                Messages.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text);
                try { File.WriteAllLines(Path.Combine(DirectoryPath, "controller.log"), Messages.ToArray(), new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { stopEvent.Set(); } catch { }
            uiJob.Dispose();
            hostJob.Dispose();
            if (ui != null) ui.Dispose();
            if (host != null) host.Dispose();
            stopEvent.Dispose();
        }
    }

    internal sealed class DirectApplicationContext : ApplicationContext
    {
        private readonly string candidate;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly EventWaitHandle activate = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEvent);
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem open = new ToolStripMenuItem("打开 MIUI-");
        private readonly ToolStripMenuItem panel = new ToolStripMenuItem("打开手机面板");
        private readonly ToolStripMenuItem quit = new ToolStripMenuItem("退出并断开连接");
        private readonly Icon icon;
        private TestSession session;
        private Task startup;
        private bool closing, ready, showAfterStart, panelAfterStart, disposed;
        private string failure;

        internal DirectApplicationContext(string directory)
        {
            candidate = directory;
            icon = Icon.ExtractAssociatedIcon(Program.ExePath);
            tray.Icon = icon ?? SystemIcons.Application;
            tray.Text = "MIUI- · 正在启动";
            menu.Items.Add(open);
            menu.Items.Add(panel);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            open.Click += delegate { ShowConnection(); };
            panel.Click += delegate { ShowPhonePanel(); };
            tray.DoubleClick += delegate { ShowConnection(); };
            quit.Click += delegate { BeginExit(); };
            timer.Interval = 300;
            timer.Tick += Tick;
            timer.Start();
            Application.Idle += BeginStart;
        }

        private void BeginStart(object sender, EventArgs args)
        {
            Application.Idle -= BeginStart;
            if (!closing) startup = StartCore();
        }

        private async Task StartCore()
        {
            try
            {
                session = new TestSession(candidate);
                await session.StartAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                ready = true;
                tray.Text = "MIUI- · 后台运行";
                // The DLL-hosted UI does not reliably supply its own tray icon.
                // Keep our icon for the entire session, including after closing a window.
                tray.Visible = true;
                if (panelAfterStart) ShowPhonePanel();
                else if (showAfterStart) session.ShowConnectionWindow();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex.Message; }
            // C# 5 does not allow await in a catch/finally block.
            if (!ready && !closing) BeginExit();
        }

        private void Tick(object sender, EventArgs args)
        {
            if (closing) return;
            if (activate.WaitOne(0)) ShowConnection();
            // Projection windows are children of the native UI; closing one does
            // not stop this session. Only an actual UI-host/runtime exit does.
            if (ready && session != null && session.AnyExited) BeginExit();
        }

        private void ShowConnection()
        {
            if (closing) return;
            if (!ready) { showAfterStart = true; return; }
            session.ShowConnectionWindow();
        }

        private void ShowPhonePanel()
        {
            if (closing) return;
            if (!ready) { panelAfterStart = true; return; }
            if (!session.ShowPhonePanel())
            {
                session.ShowConnectionWindow();
                tray.ShowBalloonTip(3500, "MIUI-", "手机面板尚未就绪，请在连接窗口确认设备已连接。", ToolTipIcon.Info);
            }
        }

        private async void BeginExit()
        {
            if (closing) return;
            closing = true;
            ready = false;
            timer.Stop();
            open.Enabled = false;
            panel.Enabled = false;
            quit.Enabled = false;
            tray.Text = "MIUI- · 正在退出";
            cancellation.Cancel();
            try
            {
                // StartCore never awaits BeginExit; no circular startup/shutdown wait.
                if (startup != null) await startup;
                if (session != null) await session.StopAsync();
            }
            catch (Exception ex) { if (failure == null) failure = ex.Message; }
            finally
            {
                if (session != null) { session.Dispose(); session = null; }
                tray.Visible = false;
            }
            if (failure != null)
                MessageBox.Show("MIUI- 未能正常启动或已退出。\r\n\r\n" + failure, "MIUI-", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                Application.Idle -= BeginStart;
                cancellation.Cancel();
                timer.Stop();
                timer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                if (session != null) { session.Dispose(); session = null; }
                activate.Dispose();
                cancellation.Dispose();
                if (icon != null) icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ControllerForm : Form
    {
        private readonly string candidate;
        private readonly Button start = new Button(), end = new Button(), check = new Button(), details = new Button();
        private readonly Label status = new Label();
        private readonly TextBox diagnostics = new TextBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private TestSession session;
        private CancellationTokenSource operationCancel;
        private Task activeOperation;
        private bool closing, allowClose, stopRequested;
        private string lastFailure = "";

        internal ControllerForm(string directory)
        {
            candidate = directory;
            Text = "MIUI- 原生适配 · 开发预览版";
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(710, 460);
            MinimumSize = new Size(650, 470);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;

            var header = new Panel { Dock = DockStyle.Top, Height = 102, Padding = new Padding(22, 17, 18, 0) };
            header.Controls.Add(new Label { Text = "目标：小米 15 / 澎湃 OS 3；扫码兼容尚未完成实机验证", ForeColor = Color.FromArgb(139, 86, 15), Dock = DockStyle.Bottom, Height = 31 });
            header.Controls.Add(new Label { Text = "MIUI- 原生连接", Font = new Font(Font.FontFamily, 20F, FontStyle.Bold), Dock = DockStyle.Top, Height = 42 });
            var help = new Label { Text = "使用 MIUI- 原生扫码、手机控制和剪贴板功能。\r\n先退出旧版 MIUI+、其他 MIUI- 或小米电脑管家，再打开连接窗口。", Dock = DockStyle.Top, Height = 78, Padding = new Padding(22, 12, 18, 0) };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 68, Padding = new Padding(18, 12, 0, 0) };
            start.Text = "打开 MIUI- 连接窗口"; start.Size = new Size(240, 40); start.BackColor = Color.FromArgb(236, 103, 34); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
            end.Text = "结束连接"; end.Size = new Size(118, 40); end.Enabled = false;
            check.Text = "检查文件"; check.Size = new Size(118, 40);
            actions.Controls.Add(start); actions.Controls.Add(end); actions.Controls.Add(check);
            status.Text = "尚未启动"; status.Dock = DockStyle.Top; status.Height = 56; status.Padding = new Padding(22, 6, 18, 0);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(18, 2, 0, 0) };
            details.Text = "查看诊断"; details.Size = new Size(112, 30);
            footer.Controls.Add(details);
            footer.Controls.Add(new Label { Text = "关闭本窗口会结束本次启动的连接。", AutoSize = true, Margin = new Padding(9, 7, 0, 0), ForeColor = Color.DimGray });
            diagnostics.Multiline = true; diagnostics.ReadOnly = true; diagnostics.ScrollBars = ScrollBars.Vertical; diagnostics.Dock = DockStyle.Fill; diagnostics.Visible = false; diagnostics.BackColor = Color.FromArgb(247, 247, 247);
            Controls.Add(diagnostics); Controls.Add(footer); Controls.Add(status); Controls.Add(actions); Controls.Add(help); Controls.Add(header);
            details.Click += delegate { diagnostics.Visible = !diagnostics.Visible; details.Text = diagnostics.Visible ? "收起诊断" : "查看诊断"; };
            start.Click += async delegate { await BeginOperation(false); };
            check.Click += async delegate { await BeginOperation(true); };
            end.Click += async delegate { await RequestStop(); };
            FormClosing += CloseRequested;
            timer.Interval = 500;
            timer.Tick += async delegate
            {
                RefreshMessages();
                if (activeOperation == null && !stopRequested && session != null && session.AnyExited)
                {
                    await RequestStop();
                    if (!closing) status.Text = "连接窗口或组件已退出，本次连接已结束。";
                }
            };
            timer.Start();
        }

        private async Task BeginOperation(bool checkOnly)
        {
            if (activeOperation != null || session != null || closing || stopRequested) return;
            lastFailure = "";
            operationCancel = new CancellationTokenSource();
            start.Enabled = false; check.Enabled = false; end.Enabled = true;
            status.Text = checkOnly ? "正在离线检查文件，不会启动连接…" : "正在检查文件并准备扫码窗口…";
            activeOperation = checkOnly ? CheckFiles(operationCancel.Token) : StartSession(operationCancel.Token);
            try { await activeOperation; }
            finally
            {
                activeOperation = null;
                operationCancel.Dispose(); operationCancel = null;
                UpdateButtons();
            }
        }

        private async Task CheckFiles(CancellationToken cancel)
        {
            try
            {
                var checks = await Task.Run(delegate { return Candidate.Validate(candidate, cancel, null); }, cancel);
                string file = Path.Combine(Program.BaseDirectory, "diagnostics", "offline-check.json");
                Json.Write(file, new Dictionary<string, object> { { "success", true }, { "vendorDllsExecuted", false }, { "checks", checks } });
                if (!closing) { status.Text = "文件检查通过。手机连接和操作效果仍需实测。"; diagnostics.Text = "离线检查报告：" + file; }
            }
            catch (OperationCanceledException) { if (!closing) status.Text = "已取消文件检查。"; }
            catch (Exception ex) { ShowFailure(ex); }
        }

        private async Task StartSession(CancellationToken cancel)
        {
            bool ready = false;
            try
            {
                session = new TestSession(candidate);
                await session.StartAsync(cancel);
                cancel.ThrowIfCancellationRequested();
                ready = true;
                if (!closing) status.Text = "已打开连接窗口。请选择“扫码连接”，并在手机上确认。";
            }
            catch (OperationCanceledException) { if (!closing) status.Text = "已取消启动。"; }
            catch (Exception ex) { ShowFailure(ex); }
            if (!ready) await CleanupSession();
        }

        private void ShowFailure(Exception ex)
        {
            lastFailure = ex.Message;
            if (closing || IsDisposed) return;
            status.Text = "未能完成操作：" + ex.Message;
            diagnostics.Visible = true; details.Text = "收起诊断";
            diagnostics.AppendText("\r\n" + ex.ToString() + "\r\n");
        }

        private async Task RequestStop()
        {
            if (stopRequested) return;
            stopRequested = true; UpdateButtons();
            try
            {
                if (operationCancel != null) operationCancel.Cancel();
                // Wait for cancelled startup before disposing jobs: a late startup cannot create orphan processes.
                Task operation = activeOperation;
                if (operation != null) await operation;
                await CleanupSession();
                if (!closing) status.Text = String.IsNullOrEmpty(lastFailure) ? "本次连接已结束。" : "本次进程已清理，请查看诊断。";
            }
            finally { stopRequested = false; UpdateButtons(); }
        }

        private async Task CleanupSession()
        {
            TestSession current = session;
            if (current == null) return;
            try { await current.StopAsync(); RefreshMessages(); }
            catch (Exception ex) { ShowFailure(ex); }
            finally { current.Dispose(); if (session == current) session = null; }
        }

        private void UpdateButtons()
        {
            if (IsDisposed || closing) return;
            start.Enabled = activeOperation == null && session == null && !stopRequested;
            check.Enabled = start.Enabled;
            end.Enabled = !stopRequested && (activeOperation != null || session != null);
        }

        private void RefreshMessages()
        {
            if (session == null || IsDisposed) return;
            lock (session.Messages)
            {
                diagnostics.Text = "诊断目录：" + session.DirectoryPath + "\r\n" + String.Join("\r\n", session.Messages.ToArray())
                    + (String.IsNullOrEmpty(lastFailure) ? "" : "\r\n错误：" + lastFailure);
            }
        }

        private async void CloseRequested(object sender, FormClosingEventArgs e)
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; start.Enabled = false; end.Enabled = false; check.Enabled = false; timer.Stop();
            if (operationCancel != null) operationCancel.Cancel();
            if (activeOperation != null) await activeOperation;
            await CleanupSession();
            allowClose = true;
            timer.Dispose();
            Close();
        }
    }

    internal sealed class OwnedJob : IDisposable
    {
        private IntPtr handle;
        internal OwnedJob()
        {
            handle = Native.CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.ExtendedLimits limits = new Native.ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!Native.SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(Native.ExtendedLimits))))
            { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
        }

        internal OwnedProcess Start(string executable, string arguments, string workingDirectory, bool hidden, string[] privateUiArguments = null)
        {
            Native.StartupInfo startup = new Native.StartupInfo();
            startup.cb = Marshal.SizeOf(typeof(Native.StartupInfo));
            if (hidden) { startup.dwFlags = 1; startup.wShowWindow = 0; }
            Native.ProcessInformation info;
            StringBuilder command = new StringBuilder(Native.Quote(executable) + (String.IsNullOrEmpty(arguments) ? "" : " " + arguments));
            IntPtr environment = IntPtr.Zero;
            try
            {
                if (privateUiArguments != null)
                {
                    var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
                        variables[(string)pair.Key] = (string)pair.Value;
                    variables[Program.UiArgumentsVariable] = new JavaScriptSerializer().Serialize(privateUiArguments);
                    StringBuilder block = new StringBuilder();
                    foreach (KeyValuePair<string, string> pair in variables) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
                    block.Append('\0');
                    environment = Marshal.StringToHGlobalUni(block.ToString());
                }
                uint flags = 0x00000004u | (hidden ? 0x08000000u : 0u) | (environment != IntPtr.Zero ? 0x00000400u : 0u);
                if (!Native.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, flags, environment, workingDirectory, ref startup, out info))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动 " + executable);
            }
            finally { if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment); }
            try
            {
                if (!Native.AssignProcessToJobObject(handle, info.hProcess)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法隔离本次子进程，已取消启动。");
                if (Native.ResumeThread(info.hThread) == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                return new OwnedProcess(info.hProcess, info.dwProcessId);
            }
            catch { Native.TerminateProcess(info.hProcess, 1); Native.CloseHandle(info.hProcess); throw; }
            finally { Native.CloseHandle(info.hThread); }
        }

        internal bool ContainsProcessHandle(IntPtr process)
        {
            bool contained;
            // Never pass a null job: that would test membership in any job instead.
            return handle != IntPtr.Zero && Native.IsProcessInJob(process, handle, out contained) && contained;
        }

        internal void Terminate() { if (handle != IntPtr.Zero) Native.TerminateJobObject(handle, 0); }
        public void Dispose() { if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; } }
    }

    internal sealed class OwnedProcess : IDisposable
    {
        private IntPtr handle;
        internal readonly uint Id;
        internal OwnedProcess(IntPtr processHandle, uint id) { handle = processHandle; Id = id; }
        internal bool HasExited { get { return handle == IntPtr.Zero || Native.WaitForSingleObject(handle, 0) == 0; } }
        internal bool Wait(uint milliseconds) { return handle == IntPtr.Zero || Native.WaitForSingleObject(handle, milliseconds) == 0; }
        public void Dispose() { if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; } }
    }

    internal static class Json
    {
        internal static void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        internal static Dictionary<string, object> TryRead(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits { internal long PerProcessUserTimeLimit, PerJobUserTimeLimit; internal uint LimitFlags; internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; internal uint ActiveProcessLimit; internal UIntPtr Affinity; internal uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits { internal BasicLimits Basic; internal IoCounters Io; internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo { internal int cb; internal string lpReserved, lpDesktop, lpTitle; internal uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; internal short wShowWindow, cbReserved2; internal IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr hProcess, hThread; internal uint dwProcessId, dwThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetDllDirectory(string path);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetDefaultDllDirectories(uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] internal static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetModuleFileName(IntPtr module, StringBuilder filename, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool WaitNamedPipe(string name, uint timeout);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcess(string app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation info);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        internal static bool ShowOwnedPhonePanel(OwnedJob job, string expectedExecutable)
        {
            bool queued = false;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                var className = new StringBuilder(256);
                if (GetClassName(window, className, className.Capacity) == 0 || className.ToString() != "ScreenShareGuideWindow") return true;
                uint pid;
                if (GetWindowThreadProcessId(window, out pid) == 0 || pid == 0) return true;
                IntPtr process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                if (process == IntPtr.Zero) return true;
                try
                {
                    if (!job.ContainsProcessHandle(process)) return true;
                    var path = new StringBuilder(32768);
                    uint size = (uint)path.Capacity;
                    if (!QueryFullProcessImageName(process, 0, path, ref size) || !Program.SamePath(path.ToString(), expectedExecutable)) return true;
                    uint currentPid;
                    if (GetWindowThreadProcessId(window, out currentPid) == 0 || currentPid != pid) return true;
                    // Grant foreground permission only to the verified owned Guide.
                    // This preserves its native panel action; it never starts casting itself.
                    AllowSetForegroundWindow(pid);
                    queued = PostMessage(window, 0x5F6, IntPtr.Zero, IntPtr.Zero);
                    if (queued)
                    {
                        ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
                        SetForegroundWindow(window);
                    }
                    return !queued;
                }
                finally { CloseHandle(process); }
            }, IntPtr.Zero);
            return queued;
        }

        internal static void ShowOwnedWindow(uint processId)
        {
            IntPtr selected = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint pid;
                GetWindowThreadProcessId(window, out pid);
                if (pid != processId || GetWindow(window, 4) != IntPtr.Zero || GetWindowTextLength(window) == 0) return true;
                if (selected == IntPtr.Zero || IsWindowVisible(window)) selected = window;
                return !IsWindowVisible(window);
            }, IntPtr.Zero);
            if (selected != IntPtr.Zero)
            {
                ShowWindowAsync(selected, IsIconic(selected) ? 9 : 5);
                SetForegroundWindow(selected);
            }
        }

        internal static bool HasVisibleWindow(uint processId)
        {
            bool found = false;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint pid;
                GetWindowThreadProcessId(window, out pid);
                if (pid != processId || !IsWindowVisible(window)) return true;
                found = true;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        internal static bool TryGetPipeServer(string name, out uint pid)
        {
            pid = 0;
            if (!WaitNamedPipe(name, 100)) return false;
            IntPtr handle = CreateFile(name, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return false;
            try { return GetNamedPipeServerProcessId(handle, out pid); }
            finally { CloseHandle(handle); }
        }

        internal static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(c);
            }
            result.Append('\\', slashes * 2); result.Append('"');
            return result.ToString();
        }
    }
}
