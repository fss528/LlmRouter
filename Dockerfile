FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.config ./
COPY src/LlmRouter.Core/LlmRouter.Core.csproj src/LlmRouter.Core/
COPY src/LlmRouter.Infrastructure/LlmRouter.Infrastructure.csproj src/LlmRouter.Infrastructure/
COPY src/LlmRouter.Api/LlmRouter.Api.csproj src/LlmRouter.Api/

RUN dotnet restore src/LlmRouter.Api/LlmRouter.Api.csproj

COPY src/ src/
RUN dotnet publish src/LlmRouter.Api/LlmRouter.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

USER $APP_UID

HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "LlmRouter.Api.dll"]
