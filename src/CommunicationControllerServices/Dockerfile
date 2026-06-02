FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE
ARG OCTO_VERSION

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /src
COPY . .
ENV NUGET_XMLDOC_MODE=none
WORKDIR "/src/src/CommunicationControllerServices"
RUN dotnet build "CommunicationControllerServices.csproj" -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} -p:OctoVersion=${OCTO_VERSION}

FROM build AS publish
RUN dotnet publish "CommunicationControllerServices.csproj" -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} -p:OctoVersion=${OCTO_VERSION} -o /app /p:UseAppHost=false

FROM base AS final

# Install mongodb-database-tools so MongoBlueprintBackupService can spawn
# `mongodump` for the pre-migration backup that the engine's
# UpgradeModelsAsync runs before every CK model bump. Without it, the first
# CK migration crashes with "No such file or directory" and the host fails
# to start — observed on the 3.20.0 -> 3.21.0 additive upgrade. Pattern
# mirrors octo-bot-services' Dockerfile.
RUN apt-get update && \
    apt-get install -y wget curl unzip ca-certificates gnupg && \
    wget -qO- https://www.mongodb.org/static/pgp/server-8.0.asc | tee /etc/apt/trusted.gpg.d/server-8.0.asc && \
    echo "deb [ arch=amd64,arm64 ] https://repo.mongodb.org/apt/ubuntu noble/mongodb-org/8.0 multiverse" | tee /etc/apt/sources.list.d/mongodb-org-8.0.list && \
    apt-get update && \
    apt-get install -y mongodb-mongosh mongodb-database-tools && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=publish /app .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Backend.CommunicationControllerServices.dll"]