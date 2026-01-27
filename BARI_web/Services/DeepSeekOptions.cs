namespace BARI_web.Services;

public sealed class DeepSeekOptions
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string ApiKey { get; set; } = "";
    public string ModelPlanner { get; set; } = "deepseek-chat";
    public string ModelWriter { get; set; } = "deepseek-chat";
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Límite por defecto para listados (si el usuario no especifica).
    /// </summary>
    public int DefaultListLimit { get; set; } = 20;

    /// <summary>
    /// Cantidad de mensajes previos que se envían como contexto al LLM.
    /// </summary>
    public int HistoryWindow { get; set; } = 10;

    public int MaxListLimit { get; set; } = 100;    
    public int MaxSqlSteps { get; set; } = 4;       

}
