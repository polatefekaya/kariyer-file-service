# syntax=docker/dockerfile:1

# ---- build stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as its own layer so it only re-runs when the csproj changes.
COPY src/Kariyer.FileService/Kariyer.FileService.csproj src/Kariyer.FileService/
RUN dotnet restore src/Kariyer.FileService/Kariyer.FileService.csproj

# Copy the project sources and publish a framework-dependent build.
COPY src/Kariyer.FileService/ src/Kariyer.FileService/
RUN dotnet publish src/Kariyer.FileService/Kariyer.FileService.csproj \
      -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime stage -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is used by the container/compose HEALTHCHECK to probe /health.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# Drop privileges to the non-root user shipped in the .NET images.
USER $APP_UID

EXPOSE 5290
ENV ASPNETCORE_URLS=http://0.0.0.0:5290 \
    ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
  CMD curl -fsS http://localhost:5290/health || exit 1

ENTRYPOINT ["dotnet", "Kariyer.FileService.dll"]
