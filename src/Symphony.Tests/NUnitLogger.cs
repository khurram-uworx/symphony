using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Symphony.Tests;

internal class NUnitLogger<T> : ILogger<T>, IDisposable
{
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (logLevel != LogLevel.Information)
            TestContext.Out.WriteLine($"{logLevel}: {state}");
        else
            TestContext.Out.WriteLine(state);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable BeginScope<TState>(TState state) => this;

    public void Dispose() { }
}