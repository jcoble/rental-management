using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestStatus
{
    NotCreated = 0,
    Created = 1,
    Passing = 2,
    Failing = 3,
    Skipped = 4
}
