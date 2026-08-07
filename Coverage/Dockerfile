FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Node 22 for the Angular production build
RUN apt-get update && apt-get install -y curl ca-certificates gnupg \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y nodejs \
 && rm -rf /var/lib/apt/lists/*
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY Coverage/Coverage.csproj Coverage/
COPY Coverage.Library/Coverage.Library.csproj Coverage.Library/
RUN dotnet restore Coverage/Coverage.csproj
COPY . .
WORKDIR /src/Coverage/ClientApp
RUN npm ci && npx ng build
WORKDIR /src/Coverage
RUN dotnet build Coverage.csproj -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# EnableSpaBuilder=false: the Angular bundle was already built above
RUN dotnet publish Coverage.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnableSpaBuilder=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /src/Coverage/ClientApp/dist ./ClientApp/dist
ENTRYPOINT ["dotnet", "Coverage.dll"]
