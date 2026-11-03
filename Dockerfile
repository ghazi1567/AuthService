# syntax=docker/dockerfile:1

# AuthService container images.
#   API image (default target):   docker build -t authservice-api .
#   Migration image (one-off job): docker build --target migrate -t authservice-migrate .
#   Web console image:            docker build --target web -t authservice-web .
# The API never migrates on startup (docs/decisions/0003): run the migration image against the database before starting a new version.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on the project files alone, so a code change does not repeat it.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY .config/ .config/
COPY src/AuthService.Api/AuthService.Api.csproj src/AuthService.Api/
COPY src/AuthService.Infrastructure/AuthService.Infrastructure.csproj src/AuthService.Infrastructure/
RUN dotnet restore src/AuthService.Api/AuthService.Api.csproj

COPY src/ src/
RUN dotnet publish src/AuthService.Api/AuthService.Api.csproj -c Release --no-restore -o /app

# A self-contained executable that applies every migration: `efbundle --connection "<connection string>"`.
FROM build AS bundle
ARG TARGETARCH
RUN dotnet tool restore \
 && if [ "$TARGETARCH" = "arm64" ]; then rid=linux-arm64; else rid=linux-x64; fi \
 && dotnet ef migrations bundle \
      --project src/AuthService.Infrastructure --startup-project src/AuthService.Api \
      --self-contained --target-runtime "$rid" --configuration Release --force --output /bundle/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrate
WORKDIR /app
COPY --from=bundle /bundle/efbundle ./efbundle
USER $APP_UID
ENTRYPOINT ["./efbundle"]

# The web console (src/AuthService.Web). It is one more client of the API; it holds no database connection.
FROM build AS build-web
RUN dotnet restore src/AuthService.Web/AuthService.Web.csproj \
 && dotnet publish src/AuthService.Web/AuthService.Web.csproj -c Release --no-restore -o /app-web

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web
WORKDIR /app
COPY --from=build-web /app-web .
# A folder for the keys that protect the cookies, so they survive a restart; mount a volume on it. Not root.
RUN mkdir /keys && chown $APP_UID /keys
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "AuthService.Web.dll"]

# Last, so a plain `docker build .` still builds the API.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# Not root. The image listens on 8080 (the .NET default for containers); put a TLS-terminating proxy in front.
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "AuthService.Api.dll"]
