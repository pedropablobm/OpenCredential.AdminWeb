// The compatibility test links Desktop's actual hash code; only logging is replaced.
namespace log4net;

public interface ILog
{
    void ErrorFormat(string format, params object[] values);
    void DebugFormat(string format, params object[] values);
    void WarnFormat(string format, params object[] values);
}

public static class LogManager
{
    public static ILog GetLogger(string name) => new NoopLogger();
    private sealed class NoopLogger : ILog
    {
        public void ErrorFormat(string format, params object[] values) { }
        public void DebugFormat(string format, params object[] values) { }
        public void WarnFormat(string format, params object[] values) { }
    }
}
