// /Services/SheetCrud.cs
using Google.Apis.Sheets.v4.Data;

namespace BARI_web.General_Services.GoogleSheets;

public class SheetCrud
{
    private readonly SheetsContext _ctx;
    public SheetCrud(SheetsContext ctx) => _ctx = ctx;

    public void UseSheet(string sheetName) => _ctx.UseSheet(sheetName);
    public Task<IReadOnlyList<string>> ListSheetNamesAsync() => _ctx.ListSheetNamesAsync();

    // Headers normalizados
    public async Task<IReadOnlyList<string>> GetHeadersAsync()
    {
        var map = await _ctx.GetHeaderMapAsync();
        var headers = new string[(map.Values.DefaultIfEmpty(-1).Max() + 1)];
        foreach (var kv in map)
            headers[kv.Value] = NormalizeHeader(kv.Key);
        return headers.Where(h => !string.IsNullOrWhiteSpace(h)).ToArray();
    }

    // Lee todas las filas
    public async Task<IList<Dictionary<string, string>>> ReadAllAsync()
    {
        var map = await _ctx.GetHeaderMapAsync();
        var indexToHeader = map.ToDictionary(
            kv => kv.Value,
            kv => NormalizeHeader(kv.Key));

        int maxIndex = indexToHeader.Keys.DefaultIfEmpty(-1).Max();
        if (maxIndex < 0) return new List<Dictionary<string, string>>();

        string endCol = ColumnLetter(maxIndex);
        var values = await _ctx.GetValuesAsync($"{_ctx.ActiveSheetName}!A2:{endCol}");

        var list = new List<Dictionary<string, string>>();
        foreach (var row in values)
        {
            var item = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i <= maxIndex; i++)
            {
                string header = indexToHeader.TryGetValue(i, out var h) ? h : $"col_{i}";
                string val = i < row.Count ? row[i]?.ToString() ?? string.Empty : string.Empty;
                item[header] = val;
            }
            list.Add(item);
        }
        return list;
    }

    public async Task CreateAsync(Dictionary<string, object> data)
    {
        var map = await _ctx.GetHeaderMapAsync();
        var row = new object[(map.Count == 0 ? 1 : map.Values.Max() + 1)];
        foreach (var kv in data)
        {
            var key = NormalizeHeader(kv.Key);
            var target = map.FirstOrDefault(p => NormalizeHeader(p.Key).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (!target.Equals(default(KeyValuePair<string, int>)))
                row[target.Value] = kv.Value;
        }
        await _ctx.AppendRowAsync($"{_ctx.ActiveSheetName}!A1", row);
    }

    public async Task<bool> UpdateByIdAsync(string idColName, string idValue, Dictionary<string, object> updates)
    {
        var map = await _ctx.GetHeaderMapAsync();
        var idEntry = map.FirstOrDefault(p => NormalizeHeader(p.Key).Equals(NormalizeHeader(idColName), StringComparison.OrdinalIgnoreCase));
        if (idEntry.Equals(default(KeyValuePair<string, int>))) return false;

        int row = await FindRowByIdAsync(idEntry.Key, idValue);
        if (row < 0) return false;

        int maxIndex = map.Values.DefaultIfEmpty(-1).Max();
        var current = await _ctx.GetValuesAsync($"{_ctx.ActiveSheetName}!A{row}:{ColumnLetter(maxIndex)}{row}");
        var rowArr = new object[maxIndex + 1];

        if (current.Count > 0)
            for (int i = 0; i < current[0].Count; i++) rowArr[i] = current[0][i];

        foreach (var kv in updates)
        {
            var normKey = NormalizeHeader(kv.Key);
            var target = map.FirstOrDefault(p => NormalizeHeader(p.Key).Equals(normKey, StringComparison.OrdinalIgnoreCase));
            if (!target.Equals(default(KeyValuePair<string, int>)))
                rowArr[target.Value] = kv.Value;
        }

        await _ctx.UpdateRangeAsync($"{_ctx.ActiveSheetName}!A{row}:{ColumnLetter(maxIndex)}{row}",
            new List<IList<object>> { rowArr });
        return true;
    }

    public async Task<bool> DeleteByIdAsync(string idColName, string idValue)
    {
        var map = await _ctx.GetHeaderMapAsync();
        var idEntry = map.FirstOrDefault(p => NormalizeHeader(p.Key).Equals(NormalizeHeader(idColName), StringComparison.OrdinalIgnoreCase));
        if (idEntry.Equals(default(KeyValuePair<string, int>))) return false;

        int row = await FindRowByIdAsync(idEntry.Key, idValue);
        if (row < 0) return false;

        int sheetId = await _ctx.GetActiveSheetIdAsync();
        var request = new Request
        {
            DeleteDimension = new DeleteDimensionRequest
            {
                Range = new DimensionRange
                {
                    SheetId = sheetId,
                    Dimension = "ROWS",
                    StartIndex = row - 1,
                    EndIndex = row
                }
            }
        };
        var body = new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { request } };
        await _ctx.Service.Spreadsheets.BatchUpdate(body, _ctx.SpreadsheetId).ExecuteAsync();
        return true;
    }

    // === LOOKUP helper (ID -> Nombre) ===
    public async Task<Dictionary<string, string>> GetLookupAsync(string sheetName, string keyCol, string valueCol)
    {
        var prev = _ctx.ActiveSheetName;
        _ctx.UseSheet(sheetName);

        var map = await _ctx.GetHeaderMapAsync();
        if (!map.TryGetValue(keyCol, out int k) || !map.TryGetValue(valueCol, out int v))
        {
            _ctx.UseSheet(prev);
            return new();
        }

        int maxIndex = Math.Max(k, v);
        string endCol = ColumnLetter(maxIndex);
        var rows = await _ctx.GetValuesAsync($"{sheetName}!A2:{endCol}");

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = k < row.Count ? row[k]?.ToString()?.Trim() : null;
            var val = v < row.Count ? row[v]?.ToString()?.Trim() : "";
            if (!string.IsNullOrWhiteSpace(key))
                dict[key] = val ?? "";
        }

        _ctx.UseSheet(prev);
        return dict;
    }

    // === helpers internos ===
    private async Task<int> FindRowByIdAsync(string idColNameOriginal, string idValue)
    {
        var map = await _ctx.GetHeaderMapAsync();
        if (!map.TryGetValue(idColNameOriginal, out int idCol)) return -1;

        int maxIndex = map.Values.DefaultIfEmpty(25).Max();
        string endCol = ColumnLetter(maxIndex);

        var all = await _ctx.GetValuesAsync($"{_ctx.ActiveSheetName}!A2:{endCol}");
        for (int i = 0; i < all.Count; i++)
        {
            if (idCol < all[i].Count && string.Equals(all[i][idCol]?.ToString(), idValue, StringComparison.Ordinal))
                return i + 2;
        }
        return -1;
    }

    private static string ColumnLetter(int index)
    {
        int i = index; string col = "";
        while (i >= 0) { col = (char)('A' + i % 26) + col; i = i / 26 - 1; }
        return col;
    }

    private static string NormalizeHeader(string s)
    {
        if (s is null) return "";
        var t = s.Trim().Replace('\u00A0', ' ');
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t;
    }
}
