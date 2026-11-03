# syntax=docker/dockerfile:1
ARG DOTNET_VERSION=10.0
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src
COPY backend/Starport.Domain/Starport.Domain.csproj Starport.Domain/
COPY backend/Starport.Application/Starport.Application.csproj Starport.Application/
COPY backend/Starport.Processor/Starport.Processor.csproj Starport.Processor/
RUN dotnet restore Starport.Processor/Starport.Processor.csproj -a "$TARGETARCH"
COPY backend/ ./
ARG VERSION=0.0.0-dev
RUN dotnet publish Starport.Processor/Starport.Processor.csproj \
      --configuration Release --arch "$TARGETARCH" --self-contained false --no-restore \
      --output /app -p:UseAppHost=false -p:Version="$VERSION" \
      -p:InformationalVersion="$VERSION" -p:IncludeSourceRevisionInInformationalVersion=false

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}
ARG VERSION=0.0.0-dev
ARG REVISION=""
LABEL org.opencontainers.image.title="starport-processor" \
      org.opencontainers.image.source="https://github.com/jzingh98/starport" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=build --link /app ./
USER 10001
ENTRYPOINT ["dotnet", "Starport.Processor.dll"]
