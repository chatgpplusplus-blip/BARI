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
    // - Linkify básico http/https
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
}
