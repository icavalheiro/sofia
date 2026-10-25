# syntax=docker/dockerfile:1

# 1. Web UI
FROM node:22-alpine AS ui
WORKDIR /src/src/Sofia.UI
COPY src/Sofia.UI/package.json src/Sofia.UI/package-lock.json ./
RUN npm ci
COPY src/Sofia.UI/ ./
RUN npm run build

# 2. Server (the UI is built above, so the publish-time npm build is skipped)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY Sofia.slnx ./
COPY src/Sofia.Server/Sofia.Server.csproj src/Sofia.Server/
COPY src/Sofia.Server.Tests/Sofia.Server.Tests.csproj src/Sofia.Server.Tests/
RUN dotnet restore src/Sofia.Server/Sofia.Server.csproj
COPY src/Sofia.Server/ src/Sofia.Server/
RUN dotnet publish src/Sofia.Server/Sofia.Server.csproj -c Release -o /out --no-restore -p:BuildUiOnPublish=false
COPY --from=ui /src/src/Sofia.UI/dist/ /out/wwwroot/

# 3. Runtime: server + UI on one port
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# git powers change tracking and worktrees; curl is used by the healthcheck and to create pairing codes.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data /workspaces \
    && chown -R app:app /data /workspaces
WORKDIR /app
COPY --from=server /out ./
ENV SOFIA_HOME=/data \
    SOFIA_URLS=http://0.0.0.0:5199
USER app
EXPOSE 5199
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5199/api/health || exit 1
ENTRYPOINT ["dotnet", "Sofia.Server.dll"]
