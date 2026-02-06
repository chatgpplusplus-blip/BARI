# BARI

Aplicación web ASP.NET Core incluida en la solución `CONSOLE_TEST_BARI.sln`.

## Requisitos

- .NET SDK 8.0

## Estructura

- `CONSOLE_TEST_BARI.sln`: solución principal.
- `BARI_web/`: proyecto web (`net8.0`).

## Ejecución local

```bash
dotnet restore CONSOLE_TEST_BARI.sln
dotnet build CONSOLE_TEST_BARI.sln
dotnet run --project BARI_web/BARI_web.csproj
```
