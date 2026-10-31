using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatProtocols;

public sealed class ProtocolMessage
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? Room { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RequestId { get; set; }
    public string? SenderId { get; set; }
    public long? BoardUpdateSequence { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }
}
