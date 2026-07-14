using NewLife.Agent.Command;

namespace OllamaHub.Interop;

internal static class AgentServiceMode
{
    public static bool IsServiceRun(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandConst.RunService, StringComparison.OrdinalIgnoreCase));
}
