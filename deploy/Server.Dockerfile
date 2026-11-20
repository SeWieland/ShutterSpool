FROM node:26.13.1-bookworm-slim@sha256:5df79989ed685e09f0a98019db4cebfb22fb9df1a53c0161664a011f7ff6b0f1 AS web
WORKDIR /source/src/ShutterSpool.Web
COPY src/ShutterSpool.Web/package*.json ./
RUN npm install --global --ignore-scripts "npm@$(node -p 'require("./package.json").engines.npm')" && \
    npm ci --engine-strict --ignore-scripts
COPY src/ShutterSpool.Web/ ./
COPY tools/generate-attribution.mjs /source/tools/generate-attribution.mjs
COPY deploy/images/compose.yaml /source/deploy/images/compose.yaml
COPY THIRD-PARTY-NOTICES.md /source/THIRD-PARTY-NOTICES.md
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /source
COPY Directory.Build.props global.json ./
COPY src/ShutterSpool.Domain/ src/ShutterSpool.Domain/
COPY src/ShutterSpool.Application/ src/ShutterSpool.Application/
COPY src/ShutterSpool.Infrastructure/ src/ShutterSpool.Infrastructure/
COPY src/ShutterSpool.ServiceDefaults/ src/ShutterSpool.ServiceDefaults/
COPY src/ShutterSpool.Server/ src/ShutterSpool.Server/
COPY --from=web /source/src/ShutterSpool.Server/wwwroot/ src/ShutterSpool.Server/wwwroot/
ARG VERSION
ARG COMMIT
RUN test -n "$VERSION" && test -n "$COMMIT" && \
    dotnet restore src/ShutterSpool.Server --locked-mode && \
    dotnet publish src/ShutterSpool.Server --no-restore -c Release -o /out \
      -p:OpenApiGenerateDocuments=false -p:Version="$VERSION" -p:SourceRevisionId="$COMMIT"

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled-extra@sha256:cea9d5494f358fe43082300c24d1cd9f651066b6b7de1e5735bca8d3ade68649
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/ ./
COPY LICENSE ./
COPY --from=web /source/artifacts/attribution/THIRD-PARTY-NOTICES.md /source/artifacts/attribution/sftpgo.json ./
ARG VERSION
ARG COMMIT
LABEL org.opencontainers.image.version=$VERSION org.opencontainers.image.revision=$COMMIT
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShutterSpool.Server.dll"]
