using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MulLangCSharp.Build;

namespace MulLangCSharp.Debugging;

public sealed class VarNode
{
    public string N { get; set; } = "";
    public string T { get; set; } = "";
    public string V { get; set; } = "";
    public List<VarNode>? C { get; set; }
}

public sealed class FrameInfo
{
    public string Name { get; set; } = "";
    public int Line { get; set; }
    public List<VarNode> Vars { get; set; } = new();
}

public sealed class PausedInfo
{
    public string Reason { get; set; } = "";
    public int Line { get; set; }
    public string? Message { get; set; }
    public List<FrameInfo> Frames { get; set; } = new();
}

/// <summary>
/// 以獨立主控台視窗執行編譯後的程式；偵錯模式下透過具名管道控制中斷與逐步。
/// </summary>
public sealed class DebugSession : IDisposable
{
    private readonly Process _process;
    private readonly NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private readonly CancellationTokenSource _cts = new();
    private int _ended;

    public event Action<PausedInfo>? Paused;
    public event Action? Ended;

    public bool IsDebugging => _pipe is not null;

    private DebugSession(Process process, NamedPipeServerStream? pipe)
    {
        _process = process;
        _pipe = pipe;
    }

    /// <summary>啟動但不偵錯：開一個主控台視窗執行，結束後暫停讓使用者看輸出。</summary>
    public static void RunDetached(string dllPath)
    {
        var psi = CreateStartInfo(dllPath);
        Process.Start(psi);
    }

    public static DebugSession StartDebugging(string dllPath, IEnumerable<int> breakpoints, bool stopAtEntry)
    {
        string pipeName = "MulLangDbg-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var psi = CreateStartInfo(dllPath);
        psi.Environment[DebugRuntimeSource.PipeEnvironmentVariable] = pipeName;
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var session = new DebugSession(process, pipe);
        process.Exited += (_, _) => session.OnEnded();
        process.Start();
        _ = session.ServeAsync(breakpoints.ToList(), stopAtEntry);
        return session;
    }

    private static ProcessStartInfo CreateStartInfo(string dllPath)
    {
        var dotnet = CodeCompiler.DotnetHostPath;
        // chcp 65001：讓主控台以 UTF-8 顯示中文；程式結束後顯示結束代碼並暫停。
        var cmdLine = $"chcp 65001 >nul & \"{dotnet}\" \"{dllPath}\" & echo. & call echo [程式已結束，結束代碼 %^errorlevel%] & pause";
        return new ProcessStartInfo("cmd.exe", $"/s /c \"{cmdLine}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(dllPath)!,
        };
    }

    private async Task ServeAsync(List<int> breakpoints, bool stopAtEntry)
    {
        try
        {
            await _pipe!.WaitForConnectionAsync(_cts.Token);
            var utf8 = new UTF8Encoding(false);
            _writer = new StreamWriter(_pipe, utf8) { AutoFlush = true };
            Send("BP " + string.Join(",", breakpoints));
            if (stopAtEntry) Send("PAUSE");
            Send("GO");

            using var reader = new StreamReader(_pipe, utf8);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (line.StartsWith("PAUSED ", StringComparison.Ordinal))
                {
                    var info = JsonSerializer.Deserialize<PausedInfo>(line.AsSpan(7));
                    if (info is not null) Paused?.Invoke(info);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        OnEnded();
    }

    private void OnEnded()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        Ended?.Invoke();
    }

    private void Send(string line)
    {
        try { _writer?.WriteLine(line); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void SetBreakpoints(IEnumerable<int> lines) => Send("BP " + string.Join(",", lines));
    public void Continue() => Send("CONT");
    public void StepInto() => Send("IN");
    public void StepOver() => Send("OVER");
    public void StepOut() => Send("OUT");
    public void RequestPause() => Send("PAUSE");

    public void Stop()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _cts.Cancel();
        OnEnded();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipe?.Dispose();
        _process.Dispose();
    }
}
