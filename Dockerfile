# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/GameWallet.Api/GameWallet.Api.csproj src/GameWallet.Api/
RUN dotnet restore src/GameWallet.Api/GameWallet.Api.csproj
COPY src/ src/
RUN dotnet publish src/GameWallet.Api/GameWallet.Api.csproj --configuration Release --no-restore --output /app

# Runtime stage: small image, runs as a non-root user
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "GameWallet.Api.dll"]
