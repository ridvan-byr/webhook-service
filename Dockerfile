# Builds the two .NET programs of the service. Pick one with --target:
#
#   docker build --target api    -t webhook-service-api .
#   docker build --target worker -t webhook-service-worker .
#
# docker-compose.yml does this for you.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Project files first, so the package restore is cached until a dependency changes.
COPY src/WebhookService.Core/WebhookService.Core.csproj src/WebhookService.Core/
COPY src/WebhookService.Infrastructure/WebhookService.Infrastructure.csproj src/WebhookService.Infrastructure/
COPY src/WebhookService.Api/WebhookService.Api.csproj src/WebhookService.Api/
COPY src/WebhookService.Worker/WebhookService.Worker.csproj src/WebhookService.Worker/
RUN dotnet restore src/WebhookService.Api/WebhookService.Api.csproj \
 && dotnet restore src/WebhookService.Worker/WebhookService.Worker.csproj

COPY src/ src/
RUN dotnet publish src/WebhookService.Api/WebhookService.Api.csproj \
      -c Release --no-restore -o /out/api /p:UseAppHost=false \
 && dotnet publish src/WebhookService.Worker/WebhookService.Worker.csproj \
      -c Release --no-restore -o /out/worker /p:UseAppHost=false

# Both run as the unprivileged "app" user that the ASP.NET runtime image provides.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "WebhookService.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
ENV DOTNET_ENVIRONMENT=Production
USER app
ENTRYPOINT ["dotnet", "WebhookService.Worker.dll"]
