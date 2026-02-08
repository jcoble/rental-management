using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestExecutionStatus
{
    NotStarted = 0,
    Running = 1,
    Passed = 2,
    Failed = 3,
    Blocked = 4,
    Cancelled = 5
}
