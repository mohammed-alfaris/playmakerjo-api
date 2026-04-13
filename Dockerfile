# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY SportsVenueApi/SportsVenueApi.csproj SportsVenueApi/
RUN dotnet restore SportsVenueApi/SportsVenueApi.csproj

COPY SportsVenueApi/ SportsVenueApi/
WORKDIR /src/SportsVenueApi
RUN dotnet publish -c Release -o /app/publish --no-restore

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8000/health || exit 1

ENTRYPOINT ["dotnet", "SportsVenueApi.dll"]
