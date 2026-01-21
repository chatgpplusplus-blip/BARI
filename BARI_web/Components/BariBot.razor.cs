using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using BARI_web.Services;

namespace BARI_web.Components;

public sealed class ChatMessageVm
{
    public string Role { get; set; } = "user"; // user | assistant
    public string Content { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public bool? UsedDatabase { get; set; }
}

public partial class BariBot : ComponentBase
{
    [Inject] public BariBotOrchestrator Bot { get; set; } = default!;

    protected List<ChatMessageVm> Messages { get; } = new();
    protected string Draft { get; set; } = "";
    protected bool IsBusy { get; set; }
    protected string? ErrorMessage { get; set; }

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

        try
        {
            // Historial reducido para contexto del bot
            var history = Messages
                .TakeLast(12)
                .Select(m => new ChatMessage
                {
                    Role = m.Role == "user" ? "user" : "assistant",
                    Content = m.Content,
                    At = m.At
                })
                .ToList();

            var resp = await Bot.AskAsync(text, history);

            Messages.Add(new ChatMessageVm
            {
                Role = "assistant",
                Content = resp.Answer,
                At = DateTimeOffset.Now,
                UsedDatabase = resp.UsedDatabase
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;

            Messages.Add(new ChatMessageVm
            {
                Role = "assistant",
                Content = "Tuve un problema procesando esa consulta. Intenta reformular con: nombre / CAS / QR / área / mesón.",
                At = DateTimeOffset.Now,
                UsedDatabase = false
            });
        }
        finally
        {
            IsBusy = false;
            StateHasChanged();
        }
    }

    protected Task QuickAsk(string text)
    {
        Draft = text;
        return SendAsync();
    }

    protected async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (IsBusy) return;

        // Enter envía; Shift+Enter permite salto de línea
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            await SendAsync();
        }
    }
}
