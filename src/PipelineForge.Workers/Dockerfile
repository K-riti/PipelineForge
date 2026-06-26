FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY src/PipelineForge.Core/PipelineForge.Core.csproj src/PipelineForge.Core/
COPY src/PipelineForge.Infrastructure/PipelineForge.Infrastructure.csproj src/PipelineForge.Infrastructure/
COPY src/PipelineForge.Workers/PipelineForge.Workers.csproj src/PipelineForge.Workers/
RUN dotnet restore src/PipelineForge.Workers/PipelineForge.Workers.csproj

# Copy everything else and build
COPY . .
RUN dotnet publish src/PipelineForge.Workers/PipelineForge.Workers.csproj -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Create non-root user
RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

ENTRYPOINT ["dotnet", "PipelineForge.Workers.dll"]
