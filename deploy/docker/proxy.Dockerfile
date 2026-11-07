# syntax=docker/dockerfile:1
# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Mirror.Core/Mirror.Core.csproj src/Mirror.Core/
COPY src/Mirror.Data/Mirror.Data.csproj src/Mirror.Data/
COPY src/Mirror.Proxy/Mirror.Proxy.csproj src/Mirror.Proxy/
RUN dotnet restore src/Mirror.Proxy/Mirror.Proxy.csproj
COPY src/ src/
RUN dotnet publish src/Mirror.Proxy/Mirror.Proxy.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1
ENV Mirror__SnapshotFile=/var/cache/mirror/snapshot.json
RUN mkdir -p /var/cache/mirror && chown app:app /var/cache/mirror
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mirror.Proxy.dll"]
