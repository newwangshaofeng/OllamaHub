using Microsoft.Extensions.Logging;
using OllamaHub.Logging;
using Xunit;

namespace OllamaHub.Tests.Logging;

public sealed class FileLoggerTests
{
    [Fact]
    public void Log_ErrorLevel_DoesNotWriteInformationMessage()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.log");

        try
        {
            var logger = new FileLoggerProvider(logPath, LogLevel.Error).CreateLogger("TestLogger");

            logger.LogInformation("info message");

            Assert.False(File.Exists(logPath));
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public void Log_ErrorLevel_WritesErrorMessage()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.log");

        try
        {
            var logger = new FileLoggerProvider(logPath, LogLevel.Error).CreateLogger("TestLogger");

            logger.LogError("error message");

            var content = File.ReadAllText(logPath);
            Assert.Contains("[Error]", content);
            Assert.Contains("error message", content);
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public void Log_WarningLevel_DoesNotWriteInformationMessage()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.log");

        try
        {
            var logger = new FileLoggerProvider(logPath, LogLevel.Warning).CreateLogger("TestLogger");

            logger.LogInformation("info message");

            Assert.False(File.Exists(logPath));
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public void Log_NoneLevel_DoesNotWriteErrorMessage()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.log");

        try
        {
            var logger = new FileLoggerProvider(logPath, LogLevel.None).CreateLogger("TestLogger");

            logger.LogError("error message");

            Assert.False(File.Exists(logPath));
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public void Log_InformationLevel_WritesInformationWarningAndErrorMessages()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.log");

        try
        {
            var logger = new FileLoggerProvider(logPath, LogLevel.Information).CreateLogger("TestLogger");

            logger.LogInformation("info message");
            logger.LogWarning("warning message");
            logger.LogError("error message");

            var content = File.ReadAllText(logPath);
            Assert.Contains("[Information]", content);
            Assert.Contains("info message", content);
            Assert.Contains("[Warning]", content);
            Assert.Contains("warning message", content);
            Assert.Contains("[Error]", content);
            Assert.Contains("error message", content);
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    [Fact]
    public void Log_ErrorLevel_WritesExceptionDetails()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.log");

        try
        {
            var logger = new FileLoggerProvider(logPath, LogLevel.Error).CreateLogger("TestLogger");
            var exception = new InvalidOperationException("boom");

            logger.LogError(exception, "error message");

            var content = File.ReadAllText(logPath);
            Assert.Contains("[Error]", content);
            Assert.Contains("error message", content);
            Assert.Contains(nameof(InvalidOperationException), content);
            Assert.Contains("boom", content);
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }
}