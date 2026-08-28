# Production image. Note what is absent: no nuget.config, no private feed, no PAT build
# args. Hephaisto references no internal package, which is why this Dockerfile takes no
# credentials at all - and therefore why the Tiltfile never has to default one.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifests alone so a source-only change reuses the layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Hephaisto.Core/Hephaisto.Core.csproj                     src/Hephaisto.Core/
COPY src/Hephaisto.ServiceDefaults/Hephaisto.ServiceDefaults.csproj src/Hephaisto.ServiceDefaults/
COPY src/Hephaisto.Agent/Hephaisto.Agent.csproj                   src/Hephaisto.Agent/
RUN dotnet restore src/Hephaisto.Agent/Hephaisto.Agent.csproj

# Only these three projects are copied. The AppHost is dev-time orchestration and the
# Simulator is a dev fault generator; neither belongs in the pod, and leaving them out is
# what guarantees no Aspire.Hosting.* assembly ships.
COPY src/Hephaisto.Core/          src/Hephaisto.Core/
COPY src/Hephaisto.ServiceDefaults/ src/Hephaisto.ServiceDefaults/
COPY src/Hephaisto.Agent/         src/Hephaisto.Agent/

# MinVer derives the version from git, and .dockerignore correctly excludes .git/ - so it
# cannot run in here, and left to itself it would stamp every image 0.0.0-alpha.0. The
# version is therefore computed ONCE outside and passed in:
#
#     V=$(dotnet minver -t v -p main.0)
#     docker build --build-arg VERSION=$V --build-arg COMMIT=$(git rev-parse HEAD) .
#
# `-p main.0` is not optional: minver-cli defaults to alpha.0 while this repo's MSBuild
# properties say main.0, so without it the CLI and the compiler disagree about what the same
# commit is called.
ARG VERSION=0.0.0-local
ARG COMMIT=unknown

RUN dotnet publish src/Hephaisto.Agent/Hephaisto.Agent.csproj \
        -c Release -o /app --no-restore \
        -p:MinVerSkip=true \
        -p:Version=${VERSION} \
        -p:InformationalVersion=${VERSION}+${COMMIT}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Non-root. The agent holds a ServiceAccount token that can delete pods in one namespace;
# there is no reason for the process to also be root inside its own container.
#
# The uid is set numerically and the files are chowned on the way in, rather than creating a
# named user: this base image ships neither adduser nor useradd (it is Ubuntu-derived but
# carries no shadow-utils), so `RUN adduser ...` fails with exit 127. Nothing needs a passwd
# entry - Kubernetes matches runAsUser: 64198 in infra/app/hephaisto.yaml by number, and a
# numeric USER still satisfies runAsNonRoot.
COPY --from=build --chown=64198:64198 /app .
USER 64198:64198

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "Hephaisto.Agent.dll"]
