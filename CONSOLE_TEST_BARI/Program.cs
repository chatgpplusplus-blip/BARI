using System;
using System.Collections.Generic;
using Bari.Sheets;

class Program
{
    static void Main()
    {
        var ctx = new SheetsContext(@"credentials\bari-sa.json",
                                     "1ELRTDHQG05hrGP30Is8yK6xGxxvuPm4AIHbQZJoI69g");

        // Trabajar en la hoja Contenedores
        ctx.UseSheet("Reactivos");
        var crud = new SheetCrud(ctx);
        Console.WriteLine("CREATE");
        crud.Create(new Dictionary<string, object>
        {
            ["reactivo_id"] = "2",
            ["nombre_comercial"] = "SAL COMUN",
            ["nombre_quimico"] = "NaCl",
            ["CAS"] = "7647-14-5",
            ["forma_fisica"] = "solido",
            ["sustancia_controlada"] = "no",
            ["metodo_cuantificacion_preferido"] = "volumen",
            ["familia_peligro"] = "sales",
            ["sds_url"] = "www.www.www.www",
            ["observaciones"] = "ninguna"

        });
        Console.WriteLine("Creado el seugndo reactivo");
        Console.WriteLine("");
        Console.WriteLine("READ");
        Console.WriteLine("");
        Console.WriteLine("");

        var filaleer = crud.ReadRow(1);
        Console.WriteLine(string.Join(" | ", filaleer));

        Console.WriteLine("");
        Console.WriteLine("");
        Console.WriteLine("UPDATE");


        bool ok = crud.UpdateById("reactivo_id", "1", new Dictionary<string, object>
        {
            ["nombre_comercial"] = "Soda Caustica",
            ["observaciones"] = "EN STOCK"
        });
        Console.WriteLine($"Update: {ok}");
        Console.WriteLine("");
        Console.WriteLine("");
        Console.WriteLine("");

        ctx.UseSheet("Contenedores");
        Console.WriteLine("DELETE");
        Console.WriteLine("");
        Console.WriteLine("");
        bool deleted = crud.DeleteById("cont_id","CONT-0003");
        Console.WriteLine($"Deleted: {deleted}");


    }
}
