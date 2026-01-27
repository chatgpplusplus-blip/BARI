# ===== Build =====
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src


# Copiamos todo el proyecto (como el Dockerfile está dentro de BARI_web, el contexto es esa carpeta)
COPY . .


# Restaura y publica
RUN dotnet restore "./BARI_web.csproj"
RUN dotnet publish "./BARI_web.csproj" -c Release -o /app/publish --no-restore


# ===== Run =====
FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS final
WORKDIR /app
COPY --from=build /app/publish .


# Render suele exponer el servicio por el puerto 10000
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000


# OJO: si tu csproj se llama distinto, la dll también.
ENTRYPOINT ["dotnet", "BARI_web.dll"]
