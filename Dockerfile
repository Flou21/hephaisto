# Production image. Note what is absent: no nuget.config, no private feed, no PAT build
# args. Watchtower references no internal package, which is why this Dockerfile takes no
# credentials at all - and therefore why the Tiltfile never has to default one.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifests alone so a source-only change reuses the layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Watchtower.Core/Watchtower.Core.csproj                     src/Watchtower.Core/
COPY src/Watchtower.ServiceDefaults/Watchtower.ServiceDefaults.csproj src/Watchtower.ServiceDefaults/
COPY src/Watchtower.Agent/Watchtower.Agent.csproj                   src/Watchtower.Agent/
RUN dotnet restore src/Watchtower.Agent/Watchtower.Agent.csproj

# Only these three projects are copied. The AppHost is dev-time orchestration and the
# Simulator is a dev fault generator; neither belongs in the pod, and leaving them out is
# what guarantees no Aspire.Hosting.* assembly ships.
COPY src/Watchtower.Core/          src/Watchtower.Core/
COPY src/Watchtower.ServiceDefaults/ src/Watchtower.ServiceDefaults/
COPY src/Watchtower.Agent/         src/Watchtower.Agent/

RUN dotnet publish src/Watchtower.Agent/Watchtower.Agent.csproj \
        -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Non-root. The agent holds a ServiceAccount token that can delete pods in one namespace;
# there is no reason for the process to also be root inside its own container.
RUN adduser --system --uid 64198 watchtower && chown -R watchtower /app
USER watchtower

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "Watchtower.Agent.dll"]
