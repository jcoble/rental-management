using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestStepType
{
    Setup = 0,
    Action = 1,
    Assertion = 2,
    Teardown = 3
}
