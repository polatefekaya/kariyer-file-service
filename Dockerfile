# syntax=docker/dockerfile:1

# ---- build stage ---------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as its own layer so it only re-runs when the csproj changes.
COPY src/Kariyer.FileService/Kariyer.FileService.csproj src/Kariyer.FileService/
RUN dotnet restore src/Kariyer.FileService/Kariyer.FileService.csproj

# Copy the project sources and publish a framework-dependent build.
# Defensively scrub build output that may have come in from the host context
# (podman doesn't reliably honor .dockerignore/.containerignore):
#   - obj/ and bin/ : normal build output
#   - bin\Debug / obj\Debug : dirs with a LITERAL backslash that the editor's
#     Roslyn BuildHost writes on macOS. The name isn't matched by bin/obj
#     ignores, and the backslash corrupts MSBuild globbing, so the SDK keeps
#     "**/*.resx" (and *.razor/*.cshtml) literal -> "MSB3552 ... cannot be found".
#     ('bin?*' matches the backslash via ? without needing to quote it.)
COPY src/Kariyer.FileService/ src/Kariyer.FileService/
RUN rm -rf src/Kariyer.FileService/obj src/Kariyer.FileService/bin \
 && find src/Kariyer.FileService -maxdepth 1 '(' -name 'bin?*' -o -name 'obj?*' ')' -exec rm -rf {} + \
 && dotnet publish src/Kariyer.FileService/Kariyer.FileService.csproj \
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
