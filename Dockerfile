# syntax=docker/dockerfile:1
# KnowledgeHub — multi-stage build (SPEC-20260913-install-deploy RF-001/RF-002)
# Stage 1: .NET SDK 10 → self-contained single-file publish for linux-x64.
# Stage 2: minimal runtime-deps image (Debian slim), non-root, /data volume, healthcheck.
#
# NOTE: linux-musl-x64/Alpine is not possible — the hosted Blazor WASM client
# restores Microsoft.NETCore.App.Runtime.Mono.<rid>, which is not published
# for musl. bookworm-slim is the smallest glibc base that keeps a shell for
# the HEALTHCHECK (noble-chiseled/distroless have none).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (layer cache) — only the projects the Server depends on.
# packages.lock.json + Directory.Build.props are dockerignored: lock files
# bind to the generating SDK band (NU1403 on a different SDK — see
# SPEC-20260914-locked-restore). Locked-mode enforcement is dev/CI-side.
COPY KnowledgeHub.slnx global.json ./
COPY src/KnowledgeHub.Shared/KnowledgeHub.Shared.csproj   src/KnowledgeHub.Shared/
COPY src/KnowledgeHub.Client/KnowledgeHub.Client.csproj   src/KnowledgeHub.Client/
COPY src/KnowledgeHub.McpEngine/KnowledgeHub.McpEngine.csproj src/KnowledgeHub.McpEngine/
COPY src/KnowledgeHub.Server/KnowledgeHub.Server.csproj   src/KnowledgeHub.Server/
# NOTE: no -r here — a RID on `dotnet restore` propagates to the Blazor WASM
# client, which then demands a nonexistent Mono.<rid> pack. The RID belongs
# to `publish`, whose implicit restore scopes it correctly.
RUN dotnet restore src/KnowledgeHub.Server/KnowledgeHub.Server.csproj

COPY src/ src/
# RID follows the image's own arch (native or --platform/QEMU): plain
# `docker build` doesn't inject TARGETARCH, so uname is the reliable source.
RUN case "$(uname -m)" in \
      x86_64)  RID=linux-x64 ;; \
      aarch64) RID=linux-arm64 ;; \
      *) echo "Unsupported arch: $(uname -m)" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/KnowledgeHub.Server/KnowledgeHub.Server.csproj \
      -c Release -r "$RID" -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime

# curl for HEALTHCHECK; base image already ships non-root `app` user (uid 1654).
RUN apt-get update \
 && apt-get upgrade -y \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data && chown app:app /data

WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    KnowledgeHub__DatabasePath=/data/knowledgehub.db

EXPOSE 8080
VOLUME ["/data"]
USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -fsS http://localhost:8080/ -o /dev/null || exit 1

ENTRYPOINT ["./KnowledgeHub"]
