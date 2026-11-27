# Contexte de build : racine du dépôt (le projet API référence src/Janus.Domain et Directory.Build.props).
#   docker build -f src/Janus.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1. Restauration en couche séparée : invalidée uniquement si un fichier projet change.
COPY Directory.Build.props ./
COPY src/Janus.Domain/Janus.Domain.csproj src/Janus.Domain/
COPY src/Janus.Api/Janus.Api.csproj src/Janus.Api/
RUN dotnet restore src/Janus.Api/Janus.Api.csproj

# 2. Sources et publication.
COPY src/Janus.Domain/ src/Janus.Domain/
COPY src/Janus.Api/ src/Janus.Api/
RUN dotnet publish src/Janus.Api/Janus.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet","Janus.Api.dll"]
