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

# Run as the image's built-in non-root account (APP_UID=1654 in the .NET 8 images) instead of
# root, so a compromise of the web process isn't already root inside the container.
# /app/keys must be created *and* chowned here, not just mounted: Program.cs persists Data
# Protection keys there, and the app has to be able to write new ones (initial creation, and
# rotation every ~90 days) - not merely read them. Docker seeds a brand-new named volume with
# the image directory's contents and ownership, so creating it correctly here is what makes the
# volume land owned by 1654. NOTE: that seeding only happens when the volume is *empty* - an
# existing dataprotection-keys volume from a previous root-owned deployment keeps its old
# ownership and will break key rotation. See installer/README or run:
#   docker compose down && docker volume rm <project>_dataprotection-keys
# (safe: it only invalidates existing login cookies, forcing users to sign in again).
RUN mkdir -p /app/keys && chown -R $APP_UID:$APP_UID /app/keys

# No ASPNETCORE_URLS default on purpose: docker-compose binds https://+:8443 and supplies the
# certificate. Baking in a plaintext default here would mean any run that forgot to override it
# silently served the admin console and agent ingest over HTTP.
EXPOSE 8443

USER $APP_UID

ENTRYPOINT ["dotnet", "TimeTracker.Server.dll"]
