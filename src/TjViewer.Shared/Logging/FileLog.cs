using System.Diagnostics;
using System.Text;

namespace TjViewer.Shared.Logging;

/// <summary>日志级别（由低到高）。</summary>
public enum LogSeverity
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 客户端 / 服务端本地文件日志（无第三方依赖）。
///
/// 目录优先级：
///   1. 环境变量 <c>TJVIEWER_LOG_DIR</c>（部署/排查时可临时指定）
///   2. Windows：<c>%ProgramData%\TjViewer\logs</c>
///   3. Windows：<c>%LOCALAPPDATA%\TjViewer\logs</c>（ProgramData 不可写时的降级）
///   4. 非 Windows：<c>/var/lib/tjviewer/logs</c>
///   5. 应用目录 <c>logs</c>
///   6. 系统临时目录 <c>TjViewer\logs</c>
///
/// 文件名：<c>{应用名}-yyyyMMdd.log</c>，按天切割、UTF-8 追加、写后立即刷盘，
/// 每行格式：<c>yyyy-MM-dd HH:mm:ss.fff [LEVEL] [应用名] 消息</c>；异常附堆栈。
///
/// 可靠性：所有写入均捕获异常；首选目录不可用或写入失败时自动切换到下一个可用目录，
/// 并在目录内写入 <c>logging-alert.log</c> 告警（含失败原因与切换结果），
/// 同时通过 <see cref="Trace"/> 输出。日志系统自身绝不抛出异常影响业务。
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static string _app = "app";
    private static string _preferredDir = "";
    private static string _activeDir = "";
    private static LogSeverity _minLevel = LogSeverity.Debug;
    private static bool _initialized;

    private static FileStream? _stream;
    private static StreamWriter? _writer;
    private static DateTime _fileDate = DateTime.MinValue;

    private static long _writeFailures;
    private static long _lastAlertTicks;
    private static string _lastAlert = "";

    /// <summary>是否已完成初始化（失败也会完成，保证后续写入尝试兜底目录）。</summary>
    public static bool Initialized { get { lock (Gate) return _initialized; } }

    /// <summary>首选日志目录（未降级时与 <see cref="ActiveDirectory"/> 相同）。</summary>
    public static string PreferredDirectory { get { lock (Gate) return _preferredDir; } }

    /// <summary>当前实际写入目录。</summary>
    public static string ActiveDirectory { get { lock (Gate) return _activeDir; } }

    /// <summary>是否已降级到备选目录（首选目录不可写或运行中写入失败）。</summary>
    public static bool Degraded { get; private set; }

    /// <summary>写入失败累计次数（用于自检与诊断）。</summary>
    public static long WriteFailures => Interlocked.Read(ref _writeFailures);

    /// <summary>
    /// 初始化日志系统。可重复调用（以最后一次的应用名/级别为准，已打开的写入器复用）。
    /// <paramref name="app"/> 决定文件名前缀，例如 client / server。
    /// </summary>
    public static void Initialize(string app, LogSeverity minLevel = LogSeverity.Debug)
    {
        var degraded = false;
        Exception? lastError = null;

        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(app)) _app = app.Trim();
            _minLevel = minLevel;

            if (!_initialized || _writer is null)
            {
                var candidates = CandidateDirectories().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                _preferredDir = candidates.Length > 0 ? candidates[0] : Path.Combine(Path.GetTempPath(), "TjViewer", "logs");

                foreach (var cand in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(cand);
                        // 探针写入：不仅验证目录可创建，还验证可追加写入
                        var probe = Path.Combine(cand, $".{_app}.logprobe");
                        using (var fs = new FileStream(probe, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            fs.WriteByte((byte)'\n');
                        try { File.Delete(probe); } catch { }
                        _activeDir = cand;
                        degraded = !string.Equals(cand, _preferredDir, StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                if (string.IsNullOrEmpty(_activeDir))
                {
                    // 所有候选目录均不可用：保留首选目录，写入时继续尝试并在失败时告警
                    _activeDir = _preferredDir;
                    degraded = true;
                }

                _initialized = true;
                Degraded = degraded;
                try { EnsureWriterLocked(); } catch (Exception ex) { lastError = ex; }

                var line = $"日志初始化：应用={_app} 目录={_activeDir}" +
                           (degraded ? $"（已降级，首选目录={_preferredDir}）" : "") +
                           $" 最低级别={_minLevel}";
                if (lastError is not null) line += $" 最近错误={lastError.Message}";

                try { AppendLocked($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [INFO] [{_app}] {line}"); } catch { }

                if (degraded)
                    WriteAlertLocked("日志目录降级", new IOException(
                        $"首选目录不可用或不可写，已切换到 {_activeDir}。最近错误：{lastError?.Message ?? "无"}"));
            }
        }

        // 自检开关：一次性写入 DEBUG/INFO/WARN/ERROR 与异常样例，用于发布后验证
        if (Environment.GetEnvironmentVariable("TJVIEWER_LOG_SELFTEST") == "1")
            SelfTest();
    }

    public static void Debug(string message) => Write(LogSeverity.Debug, message);

    public static void Info(string message) => Write(LogSeverity.Info, message);

    public static void Warn(string message) => Write(LogSeverity.Warn, message);

    public static void Error(string message, Exception? exception = null)
        => Write(LogSeverity.Error, message, exception);

    /// <summary>写入一条日志；任何异常都被吞掉，绝不影响业务。</summary>
    public static void Write(LogSeverity level, string message, Exception? exception = null)
    {
        if (level < _minLevel) return;
        try
        {
            if (!_initialized) Initialize(_app);
            var head = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(level)}] [{_app}] {message}";
            var body = exception is null ? head : head + Environment.NewLine + exception;
            lock (Gate) { AppendLocked(body); }
        }
        catch
        {
            // 兜底：日志系统不允许向调用方抛异常
        }
    }

    /// <summary>日志自检：写入全部级别与异常样例，验证文件、格式与异常捕获链路。</summary>
    public static void SelfTest()
    {
        Debug("自检 DEBUG：调试级日志写入正常");
        Info("自检 INFO：信息级日志写入正常");
        Warn("自检 WARN：告警级日志写入正常");
        try
        {
            throw new InvalidOperationException("日志自检异常样例（非真实故障）");
        }
        catch (Exception ex)
        {
            Error("自检 ERROR：错误级日志与异常堆栈写入正常", ex);
        }
        Info($"自检完成：目录={ActiveDirectory} 降级={Degraded} 写入失败={WriteFailures}");
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var custom = Environment.GetEnvironmentVariable("TJVIEWER_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) yield return Path.GetFullPath(custom.Trim());

        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
                yield return Path.Combine(programData, "TjViewer", "logs");

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                yield return Path.Combine(localAppData, "TjViewer", "logs");
        }
        else
        {
            yield return "/var/lib/tjviewer/logs";
        }

        yield return Path.Combine(AppContext.BaseDirectory, "logs");
        yield return Path.Combine(Path.GetTempPath(), "TjViewer", "logs");
    }

    /// <summary>按天打开/切换日志文件；必须持有 <see cref="Gate"/>。</summary>
    private static void EnsureWriterLocked()
    {
        var today = DateTime.Now.Date;
        if (_writer is not null && _fileDate == today) return;

        CloseWriterLocked();
        Directory.CreateDirectory(_activeDir);
        var path = Path.Combine(_activeDir, $"{_app}-{today:yyyyMMdd}.log");
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
        _writer = new StreamWriter(_stream, Utf8NoBom);
        _fileDate = today;
    }

    /// <summary>追加一行并立即刷盘，保证异常退出也不丢日志；必须持有 <see cref="Gate"/>。</summary>
    private static void AppendLocked(string block)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                EnsureWriterLocked();
                if (_writer is null) return;
                _writer.WriteLine(block);
                _writer.Flush();
                return;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _writeFailures);
                CloseWriterLocked();
                if (attempt == 0 && SwitchToFallbackLocked(ex)) continue;
                if (attempt == 0) WriteAlertLocked("日志写入失败", ex);
                else return; // 多次失败：本次日志放弃，等待下次重试，绝不影响业务
            }
        }
    }

    /// <summary>写入失败时依次尝试其余的候选目录；成功返回 true。</summary>
    private static bool SwitchToFallbackLocked(Exception cause)
    {
        foreach (var cand in CandidateDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(cand, _activeDir, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.CreateDirectory(cand);
                var probe = Path.Combine(cand, $".{_app}.logprobe");
                using (var fs = new FileStream(probe, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    fs.WriteByte((byte)'\n');
                try { File.Delete(probe); } catch { }

                _activeDir = cand;
                Degraded = !string.Equals(cand, _preferredDir, StringComparison.OrdinalIgnoreCase);
                EnsureWriterLocked();
                var alert = $"{DateTime.Now:O} 日志写入失败（{cause.Message}），已切换到 {cand}";
                try
                {
                    _writer?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] [{_app}] {alert}");
                    _writer?.Flush();
                }
                catch { }
                WriteAlertLocked("日志目录切换", new IOException(alert));
                return true;
            }
            catch
            {
                // 尝试下一个候选目录
            }
        }
        return false;
    }

    /// <summary>写告警文件（首选目录、当前目录、临时目录依次尝试），同内容 30 秒内只写一次。</summary>
    private static void WriteAlertLocked(string title, Exception error)
    {
        var content = $"{DateTime.Now:O} [{title}] {error.Message}{Environment.NewLine}";
        try { Trace.WriteLine("[TjViewer 日志告警] " + title + "：" + error.Message); } catch { }

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastAlertTicks);
        if (content == _lastAlert && now - last < 30_000) return;
        Interlocked.Exchange(ref _lastAlertTicks, now);
        _lastAlert = content;

        foreach (var dir in new[] { _preferredDir, _activeDir, Path.Combine(Path.GetTempPath(), "TjViewer", "logs") })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "logging-alert.log"), content, Utf8NoBom);
                return;
            }
            catch { }
        }
    }

    private static void CloseWriterLocked()
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _writer = null;
        _stream = null;
        _fileDate = DateTime.MinValue;
    }

    private static string LevelText(LogSeverity level) => level switch
    {
        LogSeverity.Debug => "DEBUG",
        LogSeverity.Info => "INFO",
        LogSeverity.Warn => "WARN",
        _ => "ERROR",
    };
}
