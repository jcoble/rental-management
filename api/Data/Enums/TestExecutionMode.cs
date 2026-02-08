using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestExecutionMode
{
    Manual = 0,
    AI_AgentBrowser = 1,
    Playwright = 2,
    Unit_xUnit = 3,
    Unit_Vitest = 4
}
