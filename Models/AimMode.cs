using System.Text.Json.Serialization;

namespace GunGameBotAI.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AimMode
{
    Mixed,
    Head,
    Body
}
