# Multi-stage build for the Account Service.
# Build context is the repository root (see docker-compose.yml), so the shared
# project and Directory.Build.props resolve during restore and build.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the build props and restore against the project graph for layer caching.
COPY Directory.Build.props ./
COPY global.json ./
COPY src/EventLedger.Shared/EventLedger.Shared.csproj src/EventLedger.Shared/
COPY src/AccountService/AccountService.csproj src/AccountService/
RUN dotnet restore src/AccountService/AccountService.csproj

# Copy the rest of the sources needed by the Account Service and publish.
COPY src/EventLedger.Shared/ src/EventLedger.Shared/
COPY src/AccountService/ src/AccountService/
RUN dotnet publish src/AccountService/AccountService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for the container health check used by docker-compose.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:5001
EXPOSE 5001

ENTRYPOINT ["dotnet", "AccountService.dll"]
