FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src
COPY src/PoCiSys.Gateway/PoCiSys.Gateway.csproj src/PoCiSys.Gateway/
RUN dotnet restore src/PoCiSys.Gateway/PoCiSys.Gateway.csproj
COPY src/PoCiSys.Gateway/ src/PoCiSys.Gateway/
RUN dotnet publish src/PoCiSys.Gateway/PoCiSys.Gateway.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
RUN useradd --system --uid 10001 --home /nonexistent --shell /usr/sbin/nologin pocisys \
    && mkdir -p /data \
    && chown -R pocisys:pocisys /app /data
USER 10001:10001
EXPOSE 8719
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=5 \
  CMD curl --fail --silent --show-error http://127.0.0.1:8719/pocisys/api/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "PoCiSys Gateway.dll"]
