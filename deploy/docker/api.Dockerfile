# syntax=docker/dockerfile:1
# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Mirror.Core/Mirror.Core.csproj src/Mirror.Core/
COPY src/Mirror.Data/Mirror.Data.csproj src/Mirror.Data/
COPY src/Mirror.Api/Mirror.Api.csproj src/Mirror.Api/
RUN dotnet restore src/Mirror.Api/Mirror.Api.csproj
COPY src/ src/
RUN dotnet publish src/Mirror.Api/Mirror.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1
ENV Api__ExportDir=/data/exports
RUN mkdir -p /data/exports && chown app:app /data/exports
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mirror.Api.dll"]
