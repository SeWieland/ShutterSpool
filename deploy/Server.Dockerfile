ARG NODE_IMAGE=node:26.8.1-bookworm-slim@sha256:367679cf9792759492a486e4aa4b421764d71a9546a6dae8aab81a99eb797b3e
ARG SDK_VERSION=10.0.400
ARG SDK_IMAGE_DIGEST=sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df
FROM ${NODE_IMAGE} AS web
WORKDIR /source/src/ShutterSpool.Web
COPY src/ShutterSpool.Web/package*.json ./
RUN npm ci
COPY src/ShutterSpool.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:${SDK_VERSION}@${SDK_IMAGE_DIGEST} AS build
ARG SDK_VERSION
RUN test "$(dotnet --version)" = "$SDK_VERSION"
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
RUN mkdir -p /runtime/var/lib/shutterspool/spool /runtime/var/cache/shutterspool && \
    chown -R 1654:1654 /runtime/var/lib/shutterspool /runtime/var/cache/shutterspool

FROM ${RUNTIME_IMAGE}
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/ ./
COPY LICENSE THIRD-PARTY-NOTICES.md ./
COPY --from=build --chown=1654:1654 /runtime/var/lib/shutterspool/ /var/lib/shutterspool/
COPY --from=build --chown=1654:1654 /runtime/var/cache/shutterspool/ /var/cache/shutterspool/
ARG VERSION
ARG COMMIT
LABEL org.opencontainers.image.version=$VERSION org.opencontainers.image.revision=$COMMIT
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShutterSpool.Server.dll"]
