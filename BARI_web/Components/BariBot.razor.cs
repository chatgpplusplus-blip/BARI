using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using BARI_web.Models;
using BARI_web.Services;
using Microsoft.Extensions.AI;

namespace BARI_web.Components;

public partial class BariBot : ComponentBase
{
    [Inject] public BariBotOrchestrator Orchestrator { get; set; } = default!;
    [Inject] public IConfiguration Cfg { get; set; } = default!;

    protected List<ChatVmMessage> Messages { get; set; } = new();
    protected string UserText { get; set; } = "";
    protected bool IsBusy { get; set; }
    protected bool ShowDebug { get; set; }

    protected bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(UserText);

    private int LabId => int.TryParse(Cfg["BariBot:LabIdDefault"], out var id) ? id : 1;

    protected override void OnInitialized()
    {
        Messages.Add(new ChatVmMessage
        {
            Role = BariChatRole.Assistant,
            Html = Html("Hola 👋 Soy BARI BOT. Pregúntame por inventario, vencimientos, ubicación o calibraciones.")
        });
    }

    protected async Task SendAsync()
    {
        var text = (UserText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        UserText = "";

        Messages.Add(new ChatVmMessage
        {
            Role = BariChatRole.User,
            Html = Html(text)
        });

        IsBusy = true;
        StateHasChanged();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var resp = await Orchestrator.AskAsync(text, LabId, cts.Token);

            Messages.Add(new ChatVmMessage
            {
                Role = BariChatRole.Assistant,
                Html = Html(resp.Text),
                DebugSql = resp.DebugSql
            });
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatVmMessage
            {
                Role = BariChatRole.Assistant,
                Html = Html("Ocurrió un error.\n\nDetalle: " + ex.Message)
            });
        }
        finally
        {
            IsBusy = false;
            StateHasChanged();
        }
    }

    protected Task ClearAsync()
    {
        Messages.Clear();
        OnInitialized();
        return Task.CompletedTask;
    }

    protected async Task HandleKeyDown(KeyboardEventArgs e)
    {
        // Enter envía, Shift+Enter hace salto de línea
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            await SendAsync();
        }
    }

    private static string Html(string text)
    {
        var safe = WebUtility.HtmlEncode(text ?? "");
        return safe.Replace("\r\n", "\n").Replace("\n", "<br />");
    }
}
