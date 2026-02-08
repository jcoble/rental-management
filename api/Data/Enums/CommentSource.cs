using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommentSource
{
    Manual = 0,
    Claude = 1,
    System = 2
}
