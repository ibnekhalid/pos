# syntax=docker/dockerfile:1.4
# =============================================================================
# Restaurant POS production images
#
#   rp-web (target: web) — Blazor WebAssembly PWA (public site + /app) served by nginx;
#                          proxies /api, /hubs, /uploads, /health → rp-api
#   rp-api (target: api) — ASP.NET Core API (auth, POS, kitchen hub, receipts, reports)
#
# Public traffic: Nginx Proxy Manager (host :443) → rp-web :8096 → /api/* → rp-api:8080
# Build context is the repo root: docker build -f devops/Dockerfile --target api .
# =============================================================================

# --- ASP.NET runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# --- Build ASP.NET API ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Project files first so `dotnet restore` stays cached until a dependency changes.
COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/api/Restaurant.Domain/Restaurant.Domain.csproj", "./src/api/Restaurant.Domain/"]
COPY ["src/api/Restaurant.Contracts/Restaurant.Contracts.csproj", "./src/api/Restaurant.Contracts/"]
COPY ["src/api/Restaurant.Application/Restaurant.Application.csproj", "./src/api/Restaurant.Application/"]
COPY ["src/api/Restaurant.Infrastructure/Restaurant.Infrastructure.csproj", "./src/api/Restaurant.Infrastructure/"]
COPY ["src/api/Restaurant.Api/Restaurant.Api.csproj", "./src/api/Restaurant.Api/"]
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "./src/api/Restaurant.Api/Restaurant.Api.csproj"

# Backend-only copy — client changes must not invalidate the API build layer.
COPY src/api/ src/api/

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "src/api/Restaurant.Api/Restaurant.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# Receipt PDFs need a font with Urdu/Arabic glyphs for item and customer names. Only the Noto
# Naskh files are copied into the runtime image (the QuestPDF renderer registers ./fonts).
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-noto-core \
    && mkdir -p /app/publish/fonts \
    && cp /usr/share/fonts/truetype/noto/NotoNaskhArabic-*.ttf /app/publish/fonts/ \
    && rm -rf /var/lib/apt/lists/*

# --- ASP.NET API runtime ---
FROM base AS api
WORKDIR /app

RUN useradd --uid 5678 --user-group --create-home appuser \
    && mkdir -p /app/uploads /app/logs \
    && chown -R appuser:appuser /app

COPY --from=publish --chown=appuser:appuser /app/publish .
COPY --chown=appuser:appuser scripts/docker-entrypoint.sh ./docker-entrypoint.sh
RUN sed -i 's/\r$//' ./docker-entrypoint.sh && chmod +x ./docker-entrypoint.sh

USER appuser
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

HEALTHCHECK --interval=10s --timeout=5s --retries=6 --start-period=40s \
  CMD curl -f http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["./docker-entrypoint.sh"]

# --- Build Blazor WebAssembly PWA ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS webbuild
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/api/Restaurant.Contracts/Restaurant.Contracts.csproj", "./src/api/Restaurant.Contracts/"]
COPY ["src/clients/Restaurant.Client.Core/Restaurant.Client.Core.csproj", "./src/clients/Restaurant.Client.Core/"]
COPY ["src/clients/Restaurant.Client.UI/Restaurant.Client.UI.csproj", "./src/clients/Restaurant.Client.UI/"]
COPY ["src/clients/Restaurant.Web/Restaurant.Web.csproj", "./src/clients/Restaurant.Web/"]
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "./src/clients/Restaurant.Web/Restaurant.Web.csproj"

# The clients share only the dependency-free Contracts project with the API.
COPY src/api/Restaurant.Contracts/ src/api/Restaurant.Contracts/
COPY src/clients/Restaurant.Client.Core/ src/clients/Restaurant.Client.Core/
COPY src/clients/Restaurant.Client.UI/ src/clients/Restaurant.Client.UI/
COPY src/clients/Restaurant.Web/ src/clients/Restaurant.Web/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "src/clients/Restaurant.Web/Restaurant.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish --no-restore

# --- Web runtime (nginx: static files + reverse proxy to the API) ---
FROM nginx:1.27-alpine AS web

COPY devops/nginx/web.conf /etc/nginx/conf.d/default.conf
COPY --from=webbuild /app/publish/wwwroot /usr/share/nginx/html

EXPOSE 80

HEALTHCHECK --interval=10s --timeout=5s --retries=6 --start-period=10s \
  CMD wget -q -O /dev/null http://127.0.0.1/web-health || exit 1
