# The e2e outbound notification receiver.
#
# BUILD CONTEXT MUST BE THE REPO ROOT, not this directory - the project inherits Central
# Package Management from /Directory.Packages.props and its TFM from /Directory.Build.props,
# so both must be inside the context or `dotnet restore` fails with NU1008. Same constraint
# as infra/chaos/faulty-service/Dockerfile, and for the same reason.
#
#   cd ~/hephaisto
#   docker build -f infra/e2e/notification-receiver/Dockerfile -t hephaisto/notification-receiver:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY infra/e2e/notification-receiver/notification-receiver.csproj infra/e2e/notification-receiver/
RUN dotnet restore infra/e2e/notification-receiver/notification-receiver.csproj

COPY infra/e2e/notification-receiver/ infra/e2e/notification-receiver/
RUN dotnet publish infra/e2e/notification-receiver/notification-receiver.csproj \
        --configuration Release \
        --no-restore \
        --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

USER 1654

ENTRYPOINT ["dotnet", "/app/notification-receiver.dll"]
