namespace BARI_web.General_Services;

public sealed class LaboratorioState
{
    public event Action? OnChange;

    public int LaboratorioId { get; private set; } = 1;

    public void SetLaboratorio(int laboratorioId)
    {
        if (LaboratorioId == laboratorioId) return;
        LaboratorioId = laboratorioId;
        OnChange?.Invoke();
    }
}
