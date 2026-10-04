namespace MulLangCSharp.Debugging;

/// <summary>
/// 偵錯執行階段：以原始碼形式編入受偵錯程式。
/// 透過具名管道與編輯器溝通（一行一則訊息），負責中斷、逐步與變數快照。
/// </summary>
internal static class DebugRuntimeSource
{
    public const string PipeEnvironmentVariable = "MULLANG_DBG_PIPE";

    public const string Code = """
namespace __MulLangDbg
{
    using System;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.IO.Pipes;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Text.Json;
    using System.Threading;

    internal sealed class Frame
    {
        public string Name;
        public Frame Parent;
        public int Depth;
        public int Line;
        public string[] Names;
        public object[] Values;
    }

    public sealed class VarNode
    {
        public string N { get; set; }
        public string T { get; set; }
        public string V { get; set; }
        public List<VarNode> C { get; set; }
    }

    public sealed class FrameDto
    {
        public string Name { get; set; }
        public int Line { get; set; }
        public List<VarNode> Vars { get; set; }
    }

    public sealed class PausedDto
    {
        public string Reason { get; set; }
        public int Line { get; set; }
        public string Message { get; set; }
        public List<FrameDto> Frames { get; set; }
    }

    internal sealed class RefEq : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    /// <summary>與讀取執行緒共用的狀態；獨立成類別，避免讀取執行緒等待 Rt 的靜態建構函式而死結。</summary>
    internal static class S
    {
        public static readonly BlockingCollection<string> Commands = new BlockingCollection<string>();
        public static readonly ManualResetEventSlim Go = new ManualResetEventSlim();
        public static volatile HashSet<int> Breakpoints = new HashSet<int>();
        public static volatile bool PauseRequested;

        public static void ReadLoop(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("BP", StringComparison.Ordinal))
                    {
                        var set = new HashSet<int>();
                        foreach (var p in line.Substring(2).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            if (int.TryParse(p, out var n)) set.Add(n);
                        Breakpoints = set;
                    }
                    else if (line == "GO") Go.Set();
                    else if (line == "PAUSE") PauseRequested = true;
                    else Commands.Add(line);
                }
            }
            catch { }
            // 編輯器已中斷連線：結束程式。
            Environment.Exit(-1);
        }
    }

    public static class Rt
    {
        private static readonly AsyncLocal<Frame> Current = new AsyncLocal<Frame>();
        private static volatile Frame _last;
        private static StreamWriter _writer;
        private static readonly object WriteLock = new object();
        private static readonly object PauseLock = new object();
        private static volatile int _mode;    // 0 執行, 1 逐步執行, 2 不進入函式, 3 跳離函式
        private static volatile int _target;
        private static volatile Frame _stepFrame;
        private static readonly bool Enabled;
        [ThreadStatic] private static bool _inSnapshot;
        private static int _budget;

        static Rt()
        {
            var name = Environment.GetEnvironmentVariable("MULLANG_DBG_PIPE");
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(10000);
                var utf8 = new UTF8Encoding(false);
                var reader = new StreamReader(pipe, utf8);
                _writer = new StreamWriter(pipe, utf8) { AutoFlush = true };
                
                var t = new Thread(() => S.ReadLoop(reader)) { IsBackground = true, Name = "MulLangDbg" };
                t.Start();
                S.Go.Wait();
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                Enabled = true;
            }
            catch
            {
                Enabled = false;
            }
        }

        public static void Enter(string name)
        {
            var p = Current.Value;
            Current.Value = new Frame { Name = name, Parent = p, Depth = (p == null ? 0 : p.Depth) + 1, Line = p == null ? 0 : p.Line };
        }

        public static void Exit()
        {
            var c = Current.Value;
            if (c != null)
            {
                Current.Value = c.Parent;
                if (c.Parent != null) _last = c.Parent;
            }
        }

        public static void H(int line, string[] names, object[] values)
        {
            if (!Enabled || _inSnapshot) return;
            var f = Current.Value;
            if (f == null)
            {
                f = new Frame { Name = "（頂層陳述式）", Depth = 1 };
                Current.Value = f;
            }
            f.Line = line;
            f.Names = names;
            f.Values = values;
            _last = f;

            string reason = null;
            if (S.PauseRequested) reason = "pause";
            else if (_mode == 1) reason = "step";
            else if (_mode == 2 && (f == _stepFrame || f.Depth < _target)) reason = "step";
            else if (_mode == 3 && f.Depth < _target) reason = "step";
            if (reason == null && S.Breakpoints.Contains(line)) reason = "breakpoint";
            if (reason != null) Pause(reason, f, null);
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            var f = _last ?? new Frame { Name = "?", Depth = 1 };
            var ex = e.ExceptionObject as Exception;
            Pause("exception", f, ex == null ? "未處理的例外" : ex.GetType().FullName + ": " + ex.Message);
        }

        private static void Pause(string reason, Frame f, string message)
        {
            lock (PauseLock)
            {
                S.PauseRequested = false;
                string cmd;
                while (S.Commands.TryTake(out cmd)) { }

                var dto = new PausedDto { Reason = reason, Line = f.Line, Message = message, Frames = new List<FrameDto>() };
                _inSnapshot = true;
                try
                {
                    _budget = 4000;
                    for (var x = f; x != null; x = x.Parent)
                        dto.Frames.Add(new FrameDto { Name = x.Name, Line = x.Line, Vars = Snapshot(x) });
                }
                finally { _inSnapshot = false; }

                Send("PAUSED " + JsonSerializer.Serialize(dto));
                cmd = S.Commands.Take();
                switch (cmd)
                {
                    case "IN": _mode = 1; break;
                    case "OVER": _mode = 2; _target = f.Depth; _stepFrame = f; break;
                    case "OUT": _mode = 3; _target = f.Depth; break;
                    default: _mode = 0; break;
                }
            }
        }

        private static void Send(string line)
        {
            lock (WriteLock)
            {
                try { _writer.WriteLine(line); } catch { }
            }
        }

        // ── 變數快照 ──

        private static List<VarNode> Snapshot(Frame f)
        {
            var list = new List<VarNode>();
            if (f.Names == null) return list;
            for (int i = 0; i < f.Names.Length && i < f.Values.Length; i++)
                list.Add(Node(f.Names[i], f.Values[i], 0, new HashSet<object>(new RefEq())));
            return list;
        }

        private static VarNode Node(string name, object value, int depth, HashSet<object> path)
        {
            _budget--;
            var n = new VarNode { N = name };
            if (value == null) { n.T = ""; n.V = "空 (null)"; return n; }
            var t = value.GetType();
            n.T = TypeName(t);
            try
            {
                if (value is string s) { n.V = Quote(s, '"'); return n; }
                if (value is char ch) { n.V = Quote(ch.ToString(), '\''); return n; }
                if (value is bool b) { n.V = b ? "真 (true)" : "假 (false)"; return n; }
                if (t.IsPrimitive || t.IsEnum || value is decimal || value is DateTime || value is TimeSpan || value is Guid || value is DateTimeOffset)
                {
                    n.V = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return n;
                }
                if (value is Delegate d) { n.V = "{委派 " + d.Method.Name + "}"; return n; }

                bool canExpand = depth < 4 && _budget > 0 && (t.IsValueType || !path.Contains(value));
                if (!t.IsValueType) path.Add(value);
                try
                {
                    if (value is IEnumerable e)
                    {
                        var items = new List<VarNode>();
                        int count = 0;
                        bool more = false;
                        foreach (var item in e)
                        {
                            if (count >= 100) { more = true; break; }
                            if (canExpand && _budget > 0)
                            {
                                if (item != null && item.GetType().IsGenericType && item.GetType().GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                                {
                                    var k = item.GetType().GetProperty("Key").GetValue(item);
                                    var v = item.GetType().GetProperty("Value").GetValue(item);
                                    items.Add(Node("[" + Short(k) + "]", v, depth + 1, path));
                                }
                                else items.Add(Node("[" + count + "]", item, depth + 1, path));
                            }
                            count++;
                        }
                        n.V = "數量 = " + (value is ICollection c ? c.Count.ToString() : count + (more ? "+" : ""));
                        if (items.Count > 0) n.C = items;
                        return n;
                    }

                    n.V = Display(value, t);
                    if (canExpand)
                    {
                        var children = Members(value, t, depth, path);
                        if (children.Count > 0) n.C = children;
                    }
                }
                finally
                {
                    if (!t.IsValueType) path.Remove(value);
                }
            }
            catch (Exception ex)
            {
                n.V = "<無法評估：" + ex.GetType().Name + ">";
            }
            return n;
        }

        private static List<VarNode> Members(object value, Type t, int depth, HashSet<object> path)
        {
            var list = new List<VarNode>();
            if (t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal)) return list;
            var names = new HashSet<string>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var ty = t; ty != null && ty != typeof(object) && ty != typeof(ValueType); ty = ty.BaseType)
            {
                foreach (var fi in ty.GetFields(flags))
                {
                    string name = fi.Name;
                    if (name.StartsWith("<", StringComparison.Ordinal))
                    {
                        int end = name.IndexOf(">k__BackingField", StringComparison.Ordinal);
                        if (end < 0) continue;
                        name = name.Substring(1, end - 1);
                    }
                    if (!names.Add(name) || _budget <= 0) continue;
                    list.Add(Node(name, fi.GetValue(value), depth + 1, path));
                }
                foreach (var pi in ty.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!pi.CanRead || pi.GetIndexParameters().Length > 0 || names.Contains(pi.Name) || _budget <= 0) continue;
                    names.Add(pi.Name);
                    object v;
                    try { v = pi.GetValue(value); }
                    catch (TargetInvocationException ex) { list.Add(new VarNode { N = pi.Name, T = TypeName(pi.PropertyType), V = "<例外：" + ex.InnerException?.GetType().Name + ">" }); continue; }
                    list.Add(Node(pi.Name, v, depth + 1, path));
                }
            }
            return list;
        }

        private static string Display(object value, Type t)
        {
            var m = t.GetMethod("ToString", Type.EmptyTypes);
            if (m != null && m.DeclaringType != typeof(object) && m.DeclaringType != typeof(ValueType))
            {
                var s = value.ToString();
                return s != null && s.Length > 300 ? s.Substring(0, 300) + "…" : s;
            }
            return "{" + TypeName(t) + "}";
        }

        private static string Short(object k)
        {
            if (k == null) return "null";
            if (k is string s) return Quote(s, '"');
            return Convert.ToString(k, CultureInfo.InvariantCulture);
        }

        private static string Quote(string s, char q)
        {
            if (s.Length > 1000) s = s.Substring(0, 1000) + "…";
            var sb = new StringBuilder();
            sb.Append(q);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (c == q) sb.Append('\\');
                        sb.Append(c);
                        break;
                }
            }
            sb.Append(q);
            return sb.ToString();
        }

        private static readonly Dictionary<Type, string> Aliases = new Dictionary<Type, string>
        {
            { typeof(int), "int" }, { typeof(long), "long" }, { typeof(short), "short" }, { typeof(byte), "byte" },
            { typeof(sbyte), "sbyte" }, { typeof(uint), "uint" }, { typeof(ulong), "ulong" }, { typeof(ushort), "ushort" },
            { typeof(float), "float" }, { typeof(double), "double" }, { typeof(decimal), "decimal" }, { typeof(bool), "bool" },
            { typeof(char), "char" }, { typeof(string), "string" }, { typeof(object), "object" },
        };

        private static string TypeName(Type t)
        {
            if (Aliases.TryGetValue(t, out var a)) return a;
            if (t.IsArray) return TypeName(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsGenericType)
            {
                var n = t.Name;
                int tick = n.IndexOf('`');
                if (tick >= 0) n = n.Substring(0, tick);
                if (n.StartsWith("<", StringComparison.Ordinal)) n = "匿名型別";
                return n + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
            }
            return t.Name;
        }
    }
}
""";
}
