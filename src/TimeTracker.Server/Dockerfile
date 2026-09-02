# Build context is the repository root (needs both TimeTracker.Shared and TimeTracker.Server):
#   docker build -f src/TimeTracker.Server/Dockerfile -t timetracker-server .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/TimeTracker.Shared/TimeTracker.Shared.csproj TimeTracker.Shared/
COPY src/TimeTracker.Server/TimeTracker.Server.csproj TimeTracker.Server/
RUN dotnet restore TimeTracker.Server/TimeTracker.Server.csproj

COPY src/TimeTracker.Shared/ TimeTracker.Shared/
COPY src/TimeTracker.Server/ TimeTracker.Server/
RUN dotnet publish TimeTracker.Server/TimeTracker.Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TimeTracker.Server.dll"]
