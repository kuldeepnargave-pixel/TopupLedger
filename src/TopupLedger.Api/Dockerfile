FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files and restore (layer cache).
COPY TopupLedger.sln ./
COPY src/TopupLedger.Domain/TopupLedger.Domain.csproj src/TopupLedger.Domain/
COPY src/TopupLedger.Application/TopupLedger.Application.csproj src/TopupLedger.Application/
COPY src/TopupLedger.Infrastructure/TopupLedger.Infrastructure.csproj src/TopupLedger.Infrastructure/
COPY src/TopupLedger.Api/TopupLedger.Api.csproj src/TopupLedger.Api/
COPY src/TopupLedger.Portal/TopupLedger.Portal.csproj src/TopupLedger.Portal/
RUN dotnet restore src/TopupLedger.Api/TopupLedger.Api.csproj

# Copy the rest of the source and publish.
COPY . .
RUN dotnet publish src/TopupLedger.Api/TopupLedger.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TopupLedger.Api.dll"]
