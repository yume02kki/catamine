# syntax=docker/dockerfile:1
# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Mirror.TestUpstream/Mirror.TestUpstream.csproj src/Mirror.TestUpstream/
RUN dotnet restore src/Mirror.TestUpstream/Mirror.TestUpstream.csproj
COPY src/ src/
RUN dotnet publish src/Mirror.TestUpstream/Mirror.TestUpstream.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1

COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mirror.TestUpstream.dll"]
