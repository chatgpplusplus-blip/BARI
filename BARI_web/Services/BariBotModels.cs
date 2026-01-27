using System.Text.Json.Serialization;

namespace BARI_web.Services;

public sealed class ChatMessage
{
    public string Role { get; set; } = "user"; // user | assistant | system
    public string Content { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}

public sealed class RouterDecision
{

    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "db_query"; // db_query | general_help | needs_clarification

    [JsonPropertyName("clarifying_question")]
    public string? ClarifyingQuestion { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class SqlPlan
{
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "db_query"; // db_query | general_help | needs_clarification

    [JsonPropertyName("sql")]
    public string? Sql { get; set; }

    [JsonPropertyName("clarifying_question")]
    public string? ClarifyingQuestion { get; set; }

    [JsonPropertyName("explain")]
    public string? Explain { get; set; }
}

public sealed class DbQueryResult
{
    public List<string> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public long? ScalarCount { get; set; }
    public bool IsEmpty => (ScalarCount is null || ScalarCount == 0) && Rows.Count == 0;
}

public sealed class BariBotResponse
{
    public string Answer { get; set; } = "";
    public bool UsedDatabase { get; set; }
    public RouterDecision? Decision { get; set; }
    public SqlPlan? Plan { get; set; }
    public DbQueryResult? Data { get; set; }
}
