FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY TopupLedger.sln ./
COPY src/TopupLedger.Domain/TopupLedger.Domain.csproj src/TopupLedger.Domain/
COPY src/TopupLedger.Application/TopupLedger.Application.csproj src/TopupLedger.Application/
COPY src/TopupLedger.Infrastructure/TopupLedger.Infrastructure.csproj src/TopupLedger.Infrastructure/
COPY src/TopupLedger.Api/TopupLedger.Api.csproj src/TopupLedger.Api/
COPY src/TopupLedger.Portal/TopupLedger.Portal.csproj src/TopupLedger.Portal/
RUN dotnet restore src/TopupLedger.Portal/TopupLedger.Portal.csproj

COPY . .
RUN dotnet publish src/TopupLedger.Portal/TopupLedger.Portal.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8081
EXPOSE 8081
ENTRYPOINT ["dotnet", "TopupLedger.Portal.dll"]
