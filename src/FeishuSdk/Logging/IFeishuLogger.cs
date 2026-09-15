namespace Feishu;

public enum FeishuLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 轻量日志抽象，主库保持零依赖。需要接入 Microsoft.Extensions.Logging
/// 时由 FeishuSdk.AspNetCore 包提供适配器。
/// </summary>
public interface IFeishuLogger
{
    bool IsEnabled(FeishuLogLevel level);

    void Log(FeishuLogLevel level, string message, Exception? exception = null);
}

public static class FeishuLoggerExtensions
{
    public static void Debug(this IFeishuLogger logger, string message) => logger.Log(FeishuLogLevel.Debug, message);

    public static void Info(this IFeishuLogger logger, string message) => logger.Log(FeishuLogLevel.Info, message);

    public static void Warn(this IFeishuLogger logger, string message) => logger.Log(FeishuLogLevel.Warn, message);

    public static void Error(this IFeishuLogger logger, string message, Exception? ex = null) =>
        logger.Log(FeishuLogLevel.Error, message, ex);
}

/// <summary>控制台默认实现。Info 及以上输出，Debug 默认丢弃。</summary>
public sealed class ConsoleFeishuLogger : IFeishuLogger
{
    public static readonly ConsoleFeishuLogger InfoOnly = new(FeishuLogLevel.Info);
    public static readonly ConsoleFeishuLogger Verbose = new(FeishuLogLevel.Debug);

    private readonly FeishuLogLevel _minLevel;

    public ConsoleFeishuLogger(FeishuLogLevel minLevel = FeishuLogLevel.Info) => _minLevel = minLevel;

    public bool IsEnabled(FeishuLogLevel level) => level >= _minLevel;

    public void Log(FeishuLogLevel level, string message, Exception? exception = null)
    {
        if (!IsEnabled(level)) return;
        var tag = level switch
        {
            FeishuLogLevel.Debug => "DEBUG",
            FeishuLogLevel.Info => "INFO",
            FeishuLogLevel.Warn => "WARN",
            _ => "ERROR",
        };
        Console.Error.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}][feishu][{tag}] {message}");
        if (exception != null) Console.Error.WriteLine(exception);
    }
}

/// <summary>委托实现，便于在不引入日志框架时快速桥接。</summary>
public sealed class DelegatingFeishuLogger(Action<FeishuLogLevel, string, Exception?> sink) : IFeishuLogger
{
    public bool IsEnabled(FeishuLogLevel level) => true;

    public void Log(FeishuLogLevel level, string message, Exception? exception = null) => sink(level, message, exception);
}

/// <summary>静默实现（NOP）。</summary>
public sealed class NullFeishuLogger : IFeishuLogger
{
    public static readonly NullFeishuLogger Instance = new();

    public bool IsEnabled(FeishuLogLevel level) => false;

    public void Log(FeishuLogLevel level, string message, Exception? exception = null)
    {
    }
}
