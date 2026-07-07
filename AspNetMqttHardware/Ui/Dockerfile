# Етап 1: збірка. SDK-образ важкий, але лишається тільки тут.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Ui/Ui.csproj -c Release -o /app

# Етап 2: запуск. Легкий runtime-образ з ASP.NET Core.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ui.dll"]
