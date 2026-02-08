using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestPlanStatus
{
    Draft = 0,
    ReadyForExecution = 1,
    InProgress = 2,
    Passing = 3,
    Failing = 4
}
