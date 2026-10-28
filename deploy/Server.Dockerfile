FROM node:26.12.0-bookworm-slim@sha256:742a0fe7320e09e0321222b68165e6ac731d30470e7ea7f98f9bfc3a6e1034fe AS web
WORKDIR /source/src/ShutterSpool.Web
COPY src/ShutterSpool.Web/package*.json ./
RUN npm ci
COPY src/ShutterSpool.Web/ ./
COPY tools/generate-attribution.mjs /source/tools/generate-attribution.mjs
COPY deploy/images/compose.yaml /source/deploy/images/compose.yaml
COPY THIRD-PARTY-NOTICES.md /source/THIRD-PARTY-NOTICES.md
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/ ./
COPY LICENSE ./
COPY --from=web /source/artifacts/attribution/THIRD-PARTY-NOTICES.md /source/artifacts/attribution/sftpgo.json ./
COPY --from=build --chown=1654:1654 /runtime/var/lib/shutterspool/ /var/lib/shutterspool/
COPY --from=build --chown=1654:1654 /runtime/var/cache/shutterspool/ /var/cache/shutterspool/
ARG VERSION
ARG COMMIT
LABEL org.opencontainers.image.version=$VERSION org.opencontainers.image.revision=$COMMIT
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShutterSpool.Server.dll"]
