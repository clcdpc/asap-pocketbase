# syntax=docker/dockerfile:1.7

ARG DOTNET_SDK_VERSION=10.0.303
ARG DOTNET_RUNTIME_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS app-build
WORKDIR /src
COPY . .
RUN dotnet restore src/Asap.Web/Asap.Web.csproj
RUN dotnet publish src/Asap.Web/Asap.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /out/web \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS harness-build
WORKDIR /src
COPY .review-harness/src/Asap.ReviewInit .review-harness/src/Asap.ReviewInit
COPY .review-harness/src/Asap.ReviewBootstrap .review-harness/src/Asap.ReviewBootstrap
RUN dotnet restore .review-harness/src/Asap.ReviewInit/Asap.ReviewInit.csproj
RUN dotnet publish .review-harness/src/Asap.ReviewInit/Asap.ReviewInit.csproj \
    --configuration Release \
    --no-restore \
    --output /out/init \
    -p:UseAppHost=false
RUN dotnet restore .review-harness/src/Asap.ReviewBootstrap/Asap.ReviewBootstrap.csproj
RUN dotnet publish .review-harness/src/Asap.ReviewBootstrap/Asap.ReviewBootstrap.csproj \
    --configuration Release \
    --no-restore \
    --output /out/bootstrap \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_RUNTIME_VERSION} AS db-init
ARG REVIEW_COMMIT_SHA=unknown
ARG REVIEW_TOOLING_VERSION=unknown
LABEL org.opencontainers.image.revision="${REVIEW_COMMIT_SHA}" \
      org.opencontainers.image.version="${REVIEW_TOOLING_VERSION}"
WORKDIR /review
COPY --from=harness-build /out/init /review/init
COPY --from=app-build /out/web/Database/Asap.Database.dacpac /review/assets/Asap.Database.dacpac
COPY --from=app-build /out/web/Database/Hangfire /review/assets/Hangfire
ENTRYPOINT ["dotnet", "/review/init/Asap.ReviewInit.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION} AS web
ARG REVIEW_COMMIT_SHA=unknown
ARG REVIEW_TOOLING_VERSION=unknown
LABEL org.opencontainers.image.revision="${REVIEW_COMMIT_SHA}" \
      org.opencontainers.image.version="${REVIEW_TOOLING_VERSION}"
WORKDIR /app
COPY --from=app-build /out/web .
COPY --from=harness-build /out/bootstrap /review/bootstrap
COPY .review-harness/docker/appsettings.Testing.json /app/appsettings.Testing.json
COPY .review-harness/docker/web-entrypoint.sh /review/web-entrypoint.sh
RUN sed -i 's/\r$//' /review/web-entrypoint.sh
ENV ASPNETCORE_ENVIRONMENT=Testing
ENV ASPNETCORE_URLS=https://+:8443
RUN mkdir -p /home/app/.dotnet /review/dpkeys /review/logs /review/tls /app/.artifacts/dev-email \
    && chmod 755 /review/web-entrypoint.sh \
    && chown -R $APP_UID:$APP_UID /home/app /review/dpkeys /review/logs /review/tls /app/.artifacts
USER $APP_UID
EXPOSE 8443
ENTRYPOINT ["/review/web-entrypoint.sh"]
