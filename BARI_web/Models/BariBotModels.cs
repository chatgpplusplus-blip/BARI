using System.Text.Json.Serialization;

namespace BARI_web.Models;

public enum BariChatRole { User, Assistant }

public sealed class ChatVmMessage
{
    public BariChatRole Role { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// HTML ya sanitizado (encode + <br/>). Se renderiza como MarkupString en el componente.
    /// </summary>
    public string Html { get; init; } = "";

    /// <summary>
    /// Debug SQL (opcional).
    /// </summary>
    public string? DebugSql { get; init; }

    public string DisplayRole => Role == BariChatRole.User ? "Tú" : "BARI BOT";
}

public sealed class BariBotResponse
{
    public string Text { get; init; } = "";
    public string? DebugSql { get; init; }
}

public sealed class SqlPlan
{
    [JsonPropertyName("sql")]
    public string Sql { get; set; } = "";

    [JsonPropertyName("parameters")]
    public List<SqlParam> Parameters { get; set; } = new();

    [JsonPropertyName("needsClarification")]
    public bool NeedsClarification { get; set; }

    [JsonPropertyName("clarifyingQuestion")]
    public string? ClarifyingQuestion { get; set; }

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; set; }
}

public sealed class SqlParam
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("pgType")]
    public string PgType { get; set; } = "text";
}
