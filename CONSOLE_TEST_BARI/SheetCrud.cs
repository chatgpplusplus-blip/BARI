using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bari.Sheets
{
    public class SheetCrud
    {
        private readonly SheetsContext _ctx;

        public SheetCrud(SheetsContext ctx) => _ctx = ctx;

        // Crea una fila a partir de un diccionario {columna: valor} (según encabezados)
        public void Create(Dictionary<string, object> data)
        {
            var map = _ctx.GetHeaderMap();
            var row = new object[(map.Count == 0 ? 1 : map.Values.Max() + 1)];
            foreach (var kv in data)
                if (map.TryGetValue(kv.Key, out int idx)) row[idx] = kv.Value;

            _ctx.AppendRow($"{_ctx.ActiveSheetName}!A1", row);
        }

        // Lee una fila exacta (número de fila 1-based)
        public IList<object> ReadRow(int rowNumber, int lastColumnIndex = 25) // 25 -> Z por defecto
        {
            string endCol = ColumnLetter(lastColumnIndex);
            var values = _ctx.GetValues($"{_ctx.ActiveSheetName}!A{rowNumber}:{endCol}{rowNumber}");
            return values.Count > 0 ? values[0] : new List<object>();
        }

        // Devuelve todas las filas (útil para listados)
        public IList<IList<object>> ReadAll(int lastColumnIndex = 25)
        {
            string endCol = ColumnLetter(lastColumnIndex);
            return _ctx.GetValues($"{_ctx.ActiveSheetName}!A2:{endCol}");
        }

        // Busca la fila (número) por ID en columna con nombre idColName
        public int FindRowById(string idColName, string idValue)
        {
            var map = _ctx.GetHeaderMap();
            if (!map.TryGetValue(idColName, out int idCol)) return -1;

            var all = _ctx.GetValues($"{_ctx.ActiveSheetName}!A2:Z");
            for (int i = 0; i < all.Count; i++)
                if (idCol < all[i].Count && Equals(all[i][idCol]?.ToString(), idValue))
                    return i + 2; // porque empezamos en la fila 2

            return -1;
        }

        // Actualiza columnas específicas de una fila (por número)
        public void UpdateRow(int rowNumber, Dictionary<string, object> updates)
        {
            var map = _ctx.GetHeaderMap();
            int maxIndex = map.Values.DefaultIfEmpty(-1).Max();
            var current = _ctx.GetValues($"{_ctx.ActiveSheetName}!A{rowNumber}:{ColumnLetter(maxIndex)}{rowNumber}");
            var row = new object[maxIndex + 1];

            if (current.Count > 0)
                for (int i = 0; i < current[0].Count; i++) row[i] = current[0][i];

            foreach (var kv in updates)
                if (map.TryGetValue(kv.Key, out int idx)) row[idx] = kv.Value;

            _ctx.UpdateRange($"{_ctx.ActiveSheetName}!A{rowNumber}:{ColumnLetter(maxIndex)}{rowNumber}",
                new List<IList<object>> { row });
        }

        // Actualiza por ID
        public bool UpdateById(string idColName, string idValue, Dictionary<string, object> updates)
        {
            int row = FindRowById(idColName, idValue);
            if (row < 0) return false;
            UpdateRow(row, updates);
            return true;
        }

        // Elimina por ID (borra la fila completa usando BatchUpdate/DeleteDimension)
        public bool DeleteById(string idColName, string idValue)
        {
            int row = FindRowById(idColName, idValue);
            if (row < 0) return false;

            int sheetId = _ctx.GetActiveSheetId();

            var request = new Request
            {
                DeleteDimension = new DeleteDimensionRequest
                {
                    Range = new DimensionRange
                    {
                        SheetId = sheetId,
                        Dimension = "ROWS",
                        StartIndex = row - 1, // 0-based inclusive
                        EndIndex = row       // 0-based exclusive
                    }
                }
            };

            var body = new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { request } };
            _ctx.Service.Spreadsheets.BatchUpdate(body, _ctx.SpreadsheetId).Execute();
            return true;
        }

        // Utilidad: 0->A, 25->Z, 26->AA...
        private static string ColumnLetter(int index)
        {
            int i = index;
            string col = "";
            while (i >= 0)
            {
                col = (char)('A' + (i % 26)) + col;
                i = i / 26 - 1;
            }
            return col;
        }
    }
}
