FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["DeviceManagementServices/DeviceManagementServices.csproj", "DeviceManagementServices/"]
RUN dotnet restore "DeviceManagementServices/DeviceManagementServices.csproj"
COPY . .
WORKDIR "/src/DeviceManagementServices"
RUN dotnet build "DeviceManagementServices.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "DeviceManagementServices.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DeviceManagementServices.dll"]
