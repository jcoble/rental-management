using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestPlanSource
{
    Manual = 0,
    AI_Generated = 1,
    Template = 2
}
