using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bari.Sheets
{
    public class SheetsContext
    {
        private readonly SheetsService _service;
        private readonly string _spreadsheetId;
        private string _activeSheetName = "Sheet1";
        private Dictionary<string, int> _headerCache;
        private Dictionary<string, int> _sheetNameToIdCache;

        public SheetsContext(string serviceAccountJsonPath, string spreadsheetId, string appName = "BARI")
        {
            GoogleCredential credential;
            using (var stream = new FileStream(serviceAccountJsonPath, FileMode.Open, FileAccess.Read))
            {
                credential = GoogleCredential.FromStream(stream)
                    .CreateScoped(SheetsService.Scope.Spreadsheets);
            }

            _service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = appName
            });

            _spreadsheetId = spreadsheetId;
        }

        public SheetsService Service => _service;
        public string SpreadsheetId => _spreadsheetId;
        public string ActiveSheetName => _activeSheetName;

        public void UseSheet(string sheetName)
        {
            _activeSheetName = sheetName;
            _headerCache = null;           // invalidar cache de encabezados
        }

        // Lee encabezados (fila 1) y devuelve mapa nombre->índice (0-based)
        public Dictionary<string, int> GetHeaderMap()
        {
            if (_headerCache != null) return _headerCache;

            var res = _service.Spreadsheets.Values.Get(_spreadsheetId, $"{_activeSheetName}!1:1").Execute();
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

        // Devuelve el sheetId numérico (necesario para borrar filas)
        public int GetActiveSheetId()
        {
            _sheetNameToIdCache ??= LoadSheetIds();
            if (_sheetNameToIdCache.TryGetValue(_activeSheetName, out int id))
                return id;
            throw new InvalidOperationException($"No se encontró la hoja '{_activeSheetName}'.");
        }

        private Dictionary<string, int> LoadSheetIds()
        {
            var meta = _service.Spreadsheets.Get(_spreadsheetId).Execute();
            return meta.Sheets.ToDictionary(
                s => s.Properties.Title, s => (int)s.Properties.SheetId);
        }

        // Atajos de lectura/escritura por rango A1 (para usos puntuales)
        public IList<IList<object>> GetValues(string rangeA1)
            => _service.Spreadsheets.Values.Get(_spreadsheetId, rangeA1).Execute().Values
               ?? new List<IList<object>>();

        public void AppendRow(string rangeStartA1, IList<object> row)
        {
            var body = new ValueRange { Values = new List<IList<object>> { row } };
            var req = _service.Spreadsheets.Values.Append(body, _spreadsheetId, rangeStartA1);
            req.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
            req.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
            req.Execute();
        }

        public void UpdateRange(string rangeA1, IList<IList<object>> rows)
        {
            var body = new ValueRange { Values = rows };
            var req = _service.Spreadsheets.Values.Update(body, _spreadsheetId, rangeA1);
            req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
            req.Execute();
        }
    }
}
