using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using BARI_web.Services;

namespace BARI_web.Components;

public sealed class ChatMessageVm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "user"; // user | assistant
    public string Content { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public bool? UsedDatabase { get; set; }
}

public partial class BariBot : ComponentBase
{
    [Inject] public BariBotOrchestrator Bot { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    protected List<ChatMessageVm> Messages { get; } = new();
    protected string Draft { get; set; } = "";
    protected bool IsBusy { get; set; }
    protected string? ErrorMessage { get; set; }

    private CancellationTokenSource? _cts;
    private ElementReference _bottom;

    protected async Task SendAsync()
    {
        ErrorMessage = null;

        var text = (Draft ?? "").Trim();
        if (IsBusy || string.IsNullOrWhiteSpace(text))
            return;

        Draft = "";

        Messages.Add(new ChatMessageVm
        {
            Role = "user",
            Content = text,
            At = DateTimeOffset.Now
        });

        IsBusy = true;
        StateHasChanged();
        await ScrollToBottomAsync();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var history = Messages
                .TakeLast(14)
                .Select(m => new ChatMessage
                {
                    Role = m.Role == "user" ? "user" : "assistant",
                    Content = m.Content,
                    At = m.At
                })
                .ToList();

            var resp = await Bot.AskAsync(text, history, _cts.Token);

            Messages.Add(new ChatMessageVm
            {
                Role = "assistant",
                Content = resp.Answer ?? "",
                At = DateTimeOffset.Now,
                UsedDatabase = resp.UsedDatabase
            });

            await ScrollToBottomAsync();
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ChatMessageVm
            {
                Role = "assistant",
                Content = "Cancelé la respuesta. Si quieres, reformula la consulta o hazla más específica.",
                At = DateTimeOffset.Now,
                UsedDatabase = false
            });

            await ScrollToBottomAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;

            Messages.Add(new ChatMessageVm
            {
                Role = "assistant",
                Content = "Tuve un problema procesando esa consulta. Prueba con: **nombre**, **CAS**, **QR**, **laboratorio**, **área**, **mesón**, o **fecha de vencimiento**.",
                At = DateTimeOffset.Now,
                UsedDatabase = false
            });

            await ScrollToBottomAsync();
        }
        finally
        {
            IsBusy = false;
            StateHasChanged();
        }
    }

    protected void Stop()
    {
        if (!IsBusy) return;
        _cts?.Cancel();
    }

    protected Task QuickAsk(string text)
    {
        Draft = text;
        return SendAsync();
    }

    protected async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (IsBusy) return;

        if (e.Key == "Enter" && !e.ShiftKey)
            await SendAsync();
    }

    protected async Task CopyAsync(string text)
    {
        try
        {
            await JS.InvokeVoidAsync("bariBot.copyText", text);
        }
        catch
        {
            // Silencioso: si no hay JS, no rompemos UI
        }
    }

    private async Task ScrollToBottomAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("bariBot.scrollToBottom", _bottom);
        }
        catch
        {
            // Silencioso si no está el JS
        }
    }

    // ✅ Render más “bonito” sin depender de librerías:
    // - Respeta saltos de línea
    // - Convierte ```bloques``` en <pre><code>
    // - Negrita para texto entre comillas y **doble asterisco**
    // - Linkify básico http/https
    // - Links a inventario por ID o referencias comunes
    protected MarkupString RenderContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new MarkupString("");

        var parts = content.Split("```", StringSplitOptions.None);
        var sb = new StringBuilder();

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i] ?? "";

            if (i % 2 == 0)
            {
                // Texto normal
                var encoded = WebUtility.HtmlEncode(part);
                encoded = ApplyBold(encoded);
                encoded = LinkifyInventory(encoded);
                encoded = Linkify(encoded);
                encoded = encoded.Replace("\r\n", "\n").Replace("\n", "<br/>");
                sb.Append(encoded);
            }
            else
            {
                // Bloque de código
                var code = part.Replace("\r\n", "\n");

                // Si trae "sql\nSELECT..." (lenguaje), lo removemos
                var firstNl = code.IndexOf('\n');
                if (firstNl >= 0)
                {
                    var firstLine = code[..firstNl].Trim();
                    if (firstLine.Length <= 20 && Regex.IsMatch(firstLine, @"^[a-zA-Z0-9#+.\-]+$"))
                        code = code[(firstNl + 1)..];
                }

                sb.Append("<pre class=\"bb-code\"><code>");
                sb.Append(WebUtility.HtmlEncode(code.Trim()));
                sb.Append("</code></pre>");
            }
        }

        return new MarkupString(sb.ToString());
    }

    private static string Linkify(string encodedText)
    {
        // encodedText ya está HTML-encoded => seguro.
        // Reemplazo simple de URLs por <a>.
        return Regex.Replace(
            encodedText,
            @"(https?:\/\/[^\s<]+)",
            "<a class=\"bb-link\" href=\"$1\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>",
            RegexOptions.IgnoreCase);
    }

    private static string ApplyBold(string encodedText)
    {
        if (string.IsNullOrWhiteSpace(encodedText))
            return encodedText;

        var withQuoted = Regex.Replace(
            encodedText,
            "&quot;(.+?)&quot;",
            "<strong>&quot;$1&quot;</strong>",
            RegexOptions.Singleline);

        return Regex.Replace(
            withQuoted,
            "\\*\\*(.+?)\\*\\*",
            "<strong>$1</strong>",
            RegexOptions.Singleline);
    }

    private static string LinkifyInventory(string encodedText)
    {
        var result = encodedText;

        result = ReplaceIfNotLinked(result, new Regex(@"\b(mat_[a-z0-9]{6,})\b", RegexOptions.IgnoreCase),
            id => $"/inventario-materiales/item/{id}");
        result = ReplaceIfNotLinked(result, new Regex(@"\b(eq_[a-z0-9]{6,})\b", RegexOptions.IgnoreCase),
            id => $"/inventario-equipo/item/{id}");
        result = ReplaceIfNotLinked(result, new Regex(@"\b(mod_[a-z0-9]{6,})\b", RegexOptions.IgnoreCase),
            id => $"/inventario-equipos/modelo/{id}");
        result = ReplaceIfNotLinked(result, new Regex(@"\b(sus_[a-z0-9]{6,})\b", RegexOptions.IgnoreCase),
            id => $"/inventario-sustancias/{id}");
        result = ReplaceIfNotLinked(result, new Regex(@"\b(cont_[a-z0-9]{6,})\b", RegexOptions.IgnoreCase),
            id => $"/inventario-sustancias/contenedor/{id}");
        result = ReplaceIfNotLinked(result, new Regex(@"\b(inst_[a-z0-9]{6,})\b", RegexOptions.IgnoreCase),
            id => $"/inventario-instalaciones/item/{id}");
        result = ReplaceIfNotLinked(result, new Regex(@"\b(mes_[a-z0-9]{6,})\b", RegexOptions.IgnoreCase),
            id => $"/inventario-mesones/item/{id}");

        result = ReplaceIfNotLinked(result, new Regex(@"\btodos los equipos\b", RegexOptions.IgnoreCase),
            _ => "/inventario-equipo/todos");
        result = ReplaceIfNotLinked(result, new Regex(@"\btodas las sustancias\b|\btodos las sustancias\b|\btodos los reactivos\b", RegexOptions.IgnoreCase),
            _ => "/inventario-sustancias/todos");
        result = ReplaceIfNotLinked(result, new Regex(@"\btodas las documentaciones\b|\btodos los documentos\b", RegexOptions.IgnoreCase),
            _ => "/documentaciones/todos");

        result = ReplaceIfNotLinked(result, new Regex(@"\bmateriales\b", RegexOptions.IgnoreCase),
            _ => "/inventario-materiales");
        result = ReplaceIfNotLinked(result, new Regex(@"\bequipos\b", RegexOptions.IgnoreCase),
            _ => "/inventario-equipo");
        result = ReplaceIfNotLinked(result, new Regex(@"\bsustancias\b|\breactivos\b", RegexOptions.IgnoreCase),
            _ => "/inventario-sustancias");
        result = ReplaceIfNotLinked(result, new Regex(@"\binstalaciones\b", RegexOptions.IgnoreCase),
            _ => "/inventario-instalaciones");
        result = ReplaceIfNotLinked(result, new Regex(@"\bmesones\b", RegexOptions.IgnoreCase),
            _ => "/inventario-mesones");
        result = ReplaceIfNotLinked(result, new Regex(@"\bdocumentos\b|\bdocumentaciones\b", RegexOptions.IgnoreCase),
            _ => "/documentaciones");

        return result;
    }

    private static string ReplaceIfNotLinked(string text, Regex regex, Func<string, string> hrefBuilder)
    {
        return regex.Replace(text, match =>
        {
            if (IsInsideAnchor(text, match.Index))
                return match.Value;

            var href = hrefBuilder(match.Groups[1].Success ? match.Groups[1].Value : match.Value);
            return $"<a class=\"bb-link\" href=\"{href}\">{match.Value}</a>";
        });
    }

    private static bool IsInsideAnchor(string text, int index)
    {
        var open = text.LastIndexOf("<a", index, StringComparison.OrdinalIgnoreCase);
        if (open < 0)
            return false;

        var close = text.LastIndexOf("</a>", index, StringComparison.OrdinalIgnoreCase);
        return close < open;
    }
}
