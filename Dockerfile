# Use the official .NET 9.0 SDK image as base
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS base
WORKDIR /app

# Copy csproj and restore dependencies
COPY ["app/netRSS.csproj", "app/"]
RUN dotnet restore "app/netRSS.csproj"

# Copy everything else
COPY ["app/", "app/"]

# Set working directory to app folder
WORKDIR "/app/app"

# Set environment variables for dev mode
ENV ASPNETCORE_ENVIRONMENT=Development
ENV DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true

# Use dotnet watch for hot reload in development
ENTRYPOINT ["dotnet", "run", "--urls", "http://0.0.0.0:15001"]
