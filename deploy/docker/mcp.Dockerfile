# syntax=docker/dockerfile:1
# Build context: repository root.
#
# HTTP (default):  docker run -p 127.0.0.1:8090:8090 -e KITTYPARTY_API_URL=http://host.docker.internal:8081 kittyparty-mcp
# stdio:           docker run -i --rm -e KITTYPARTY_API_URL=... kittyparty-mcp --stdio
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Mirror.Mcp/Mirror.Mcp.csproj src/Mirror.Mcp/
RUN dotnet restore src/Mirror.Mcp/Mirror.Mcp.csproj
COPY src/Mirror.Mcp/ src/Mirror.Mcp/
RUN dotnet publish src/Mirror.Mcp/Mirror.Mcp.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
# Inside a container the server must listen on all interfaces; publish the port on 127.0.0.1 only
# (see docker-compose.yml) unless you put authentication in front of it.
ENV KITTYPARTY_MCP_TRANSPORT=http \
    KITTYPARTY_MCP_HOST=0.0.0.0 \
    KITTYPARTY_MCP_PORT=8090 \
    KITTYPARTY_API_URL=http://api:8080
COPY --from=build /app .
USER app
EXPOSE 8090
ENTRYPOINT ["dotnet", "kittyparty-mcp.dll"]
