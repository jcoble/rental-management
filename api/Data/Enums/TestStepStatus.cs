using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestStepStatus
{
    Passed = 0,
    Failed = 1,
    Skipped = 2,
    Blocked = 3
}
