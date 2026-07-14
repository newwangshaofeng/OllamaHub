using OllamaHub.Interop;
using Xunit;

namespace OllamaHub.Tests.Interop;

public sealed class AgentServiceModeTests
{
    [Theory]
    [InlineData("-s")]
    [InlineData("-S")]
    public void IsServiceRun_ServiceArgument_ReturnsTrue(string argument)
    {
        Assert.True(AgentServiceMode.IsServiceRun([argument]));
    }

    [Fact]
    public void IsServiceRun_NoArguments_ReturnsFalse()
    {
        Assert.False(AgentServiceMode.IsServiceRun([]));
    }

    [Theory]
    [InlineData("-run")]
    [InlineData("-install")]
    [InlineData("SetApiKey")]
    [InlineData("--urls")]
    public void IsServiceRun_NonServiceArgument_ReturnsFalse(string argument)
    {
        Assert.False(AgentServiceMode.IsServiceRun([argument]));
    }
}
