# syntax=docker/dockerfile:1
ARG DOTNET_VERSION=10.0
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src
COPY backend/Starport.Domain/Starport.Domain.csproj Starport.Domain/
COPY backend/Starport.Application/Starport.Application.csproj Starport.Application/
COPY backend/Starport.Infrastructure/Starport.Infrastructure.csproj Starport.Infrastructure/
COPY backend/Starport.Api/Starport.Api.csproj Starport.Api/
COPY backend/Starport.Processor/Starport.Processor.csproj Starport.Processor/
RUN dotnet restore Starport.Api/Starport.Api.csproj -a "$TARGETARCH"
RUN dotnet restore Starport.Processor/Starport.Processor.csproj -a "$TARGETARCH"
COPY backend/ ./
ARG VERSION=0.0.0-dev
RUN dotnet publish Starport.Api/Starport.Api.csproj \
      --configuration Release --arch "$TARGETARCH" --self-contained false --no-restore \
      --output /app -p:UseAppHost=false -p:Version="$VERSION" \
      -p:InformationalVersion="$VERSION" -p:IncludeSourceRevisionInInformationalVersion=false
RUN dotnet publish Starport.Processor/Starport.Processor.csproj \
      --configuration Release --arch "$TARGETARCH" --self-contained false --no-restore \
      --output /processor -p:UseAppHost=false -p:Version="$VERSION" \
      -p:InformationalVersion="$VERSION" -p:IncludeSourceRevisionInInformationalVersion=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
ARG VERSION=0.0.0-dev
ARG REVISION=""
LABEL org.opencontainers.image.title="starport-backend" \
      org.opencontainers.image.source="https://github.com/jzingh98/starport" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=build --link /app ./
COPY --from=build --link /processor ./
RUN mkdir -p /app/data && chown "$APP_UID:$APP_UID" /app/data
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD ["curl", "-fsS", "-o", "/dev/null", "http://127.0.0.1:8080/health/live"]
ENTRYPOINT ["dotnet", "Starport.Api.dll"]
