# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first so dependency layers cache independently of source changes.
COPY src/MediaServer.Api/MediaServer.Api.csproj src/MediaServer.Api/
RUN dotnet restore src/MediaServer.Api/MediaServer.Api.csproj
COPY src/ src/
RUN dotnet publish src/MediaServer.Api/MediaServer.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Debian's ffmpeg includes NVENC support (it dlopens libnvidia-encode, which the NVIDIA container runtime injects).
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data /transcode /media \
 && chown "$APP_UID" /data /transcode
WORKDIR /app
COPY --from=build /out .

ARG VERSION=dev
ENV APP_VERSION=$VERSION \
    ASPNETCORE_URLS=http://+:8080 \
    Library__Root=/media \
    Database__Path=/data/mediaserver.db \
    Images__CacheDir=/data/images \
    Hls__TempDir=/transcode

LABEL org.opencontainers.image.description="Single-user self-hosted media server"

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MediaServer.Api.dll"]
