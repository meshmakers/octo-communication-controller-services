FROM mcr.microsoft.com/dotnet/aspnet:7.0-bullseye-slim AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:7.0-bullseye-slim AS build
WORKDIR /src
COPY . .
WORKDIR "/src/src/CommunicationControllerServices"
RUN dotnet build "CommunicationControllerServices.csproj" -c Release 

FROM build AS publish
RUN dotnet publish "CommunicationControllerServices.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Backend.CommunicationControllerServices.dll"]
