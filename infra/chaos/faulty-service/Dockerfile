# C10 faulty-service container image.
#
# BUILD CONTEXT MUST BE THE REPO ROOT, not this directory. The project inherits
# Central Package Management from /Directory.Packages.props and its TFM from
# /Directory.Build.props, so both must be inside the context or `dotnet restore`
# fails with NU1008 ("Projects that use central package version management
# should not define the version on the PackageReference items").
#
#   cd /Users/flo/watchtower
#   docker build -f infra/chaos/faulty-service/Dockerfile -t watchtower/faulty-service:dev .
#
# ARM64: both mcr.microsoft.com/dotnet/sdk:10.0-alpine and
# mcr.microsoft.com/dotnet/aspnet:10.0-alpine publish linux/arm64 manifests
# (verified with `docker manifest inspect`), so this builds natively on the
# Apple-silicon node with no emulation and no --platform flag.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore layer: props + csproj only, so a Program.cs edit does not re-restore.
COPY Directory.Build.props Directory.Packages.props ./
COPY infra/chaos/faulty-service/faulty-service.csproj infra/chaos/faulty-service/
RUN dotnet restore infra/chaos/faulty-service/faulty-service.csproj

COPY infra/chaos/faulty-service/ infra/chaos/faulty-service/
RUN dotnet publish infra/chaos/faulty-service/faulty-service.csproj \
        --configuration Release \
        --no-restore \
        --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./

# Kestrel binds 8080 on all interfaces; the manifest's containerPort matches.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

# Non-root. The dotnet Alpine images ship the `app` user as UID 1654.
USER 1654

ENTRYPOINT ["dotnet", "/app/faulty-service.dll"]
