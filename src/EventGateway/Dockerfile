# Multi-stage build for the Event Gateway.
# Build context is the repository root (see docker-compose.yml), so the shared
# project and Directory.Build.props resolve during restore and build.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the build props and restore against the project graph for layer caching.
COPY Directory.Build.props ./
COPY global.json ./
COPY src/EventLedger.Shared/EventLedger.Shared.csproj src/EventLedger.Shared/
COPY src/EventGateway/EventGateway.csproj src/EventGateway/
RUN dotnet restore src/EventGateway/EventGateway.csproj

# Copy the rest of the sources needed by the Gateway and publish.
COPY src/EventLedger.Shared/ src/EventLedger.Shared/
COPY src/EventGateway/ src/EventGateway/
RUN dotnet publish src/EventGateway/EventGateway.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for the container health check used by docker-compose.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "EventGateway.dll"]
