using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

namespace BARI_web.General_Services.GoogleSheets;

public class SheetsContext
{
    private readonly SheetsService _service;
    private readonly string _spreadsheetId;
    private string _activeSheetName = "Sheet1";

    private Dictionary<string, int>? _headerCache;
    private Dictionary<string, int>? _sheetNameToIdCache;

    private SheetsContext(SheetsService service, string spreadsheetId, string appName)
    {
        _service = service;
        _spreadsheetId = spreadsheetId;
    }

    public static SheetsContext Create(string credentialsPath, string spreadsheetId, string appName)
    {
        GoogleCredential credential;
        using (var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read))
        {
            credential = GoogleCredential.FromStream(stream)
                .CreateScoped(SheetsService.Scope.Spreadsheets);
        }

        var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = appName
        });

        return new SheetsContext(service, spreadsheetId, appName);
    }

    public SheetsService Service => _service;
    public string SpreadsheetId => _spreadsheetId;
    public string ActiveSheetName => _activeSheetName;

    public void UseSheet(string sheetName)
    {
        _activeSheetName = sheetName;
        _headerCache = null;
    }

    // === Metadatos ===
    public async Task<IReadOnlyList<string>> ListSheetNamesAsync()
    {
        var meta = await _service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();
        return meta.Sheets?.Select(s => s.Properties.Title!).ToList() ?? new List<string>();
    }

    public async Task<int> GetActiveSheetIdAsync()
    {
        _sheetNameToIdCache ??= await LoadSheetIdsAsync();
        if (_sheetNameToIdCache.TryGetValue(_activeSheetName, out var id))
            return id;
        throw new InvalidOperationException($"No se encontró la hoja '{_activeSheetName}'.");
    }

    private async Task<Dictionary<string, int>> LoadSheetIdsAsync()
    {
        var meta = await _service.Spreadsheets.Get(_spreadsheetId).ExecuteAsync();
        return meta.Sheets!.ToDictionary(s => s.Properties.Title!, s => (int)s.Properties.SheetId!);
    }

    // === Encabezados ===
    public async Task<Dictionary<string, int>> GetHeaderMapAsync()
    {
        if (_headerCache != null) return _headerCache;

        var res = await _service.Spreadsheets.Values.Get(_spreadsheetId, $"{_activeSheetName}!1:1").ExecuteAsync();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (res.Values != null && res.Values.Count > 0)
        {
            for (int i = 0; i < res.Values[0].Count; i++)
            {
                var key = res.Values[0][i]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
                    map[key] = i;
            }
        }
        _headerCache = map;
        return map;
    }

    // === R/W básicos ===
    public async Task<IList<IList<object>>> GetValuesAsync(string rangeA1)
    {
        var result = await _service.Spreadsheets.Values.Get(_spreadsheetId, rangeA1).ExecuteAsync();
        return result.Values ?? new List<IList<object>>();
    }

    public async Task AppendRowAsync(string rangeStartA1, IList<object> row)
    {
        var body = new ValueRange { Values = new List<IList<object>> { row } };
        var req = _service.Spreadsheets.Values.Append(body, _spreadsheetId, rangeStartA1);
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
        req.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        await req.ExecuteAsync();
    }

    public async Task UpdateRangeAsync(string rangeA1, IList<IList<object>> rows)
    {
        var body = new ValueRange { Values = rows };
        var req = _service.Spreadsheets.Values.Update(body, _spreadsheetId, rangeA1);
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await req.ExecuteAsync();
    }

    public async Task ClearRangeAsync(string rangeA1)
    {
        var req = new ClearValuesRequest();
        await _service.Spreadsheets.Values.Clear(req, _spreadsheetId, rangeA1).ExecuteAsync();
    }
}
