# Die Basis-Images folgen bewusst dem gleitenden 10.0-Tag, damit .NET-Sicherheitspatches bei einem Rebuild
# mitkommen. Ein fester Digest würde die Runtime einfrieren – ohne CI, die den Digest regelmäßig nachzieht,
# liefe der Container dauerhaft auf einem ungepatchten Basis-Image. Wer trotzdem pinnen will:
#   docker image inspect mcr.microsoft.com/dotnet/runtime:10.0 --format '{{index .RepoDigests 0}}'
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["ngxPMIpUpdater.csproj", "."]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Ablage für LastKnownIpStore (Aussperr-Schutz). Der Eigentümer ist entscheidend: Docker initialisiert ein
# leeres Named Volume aus dem Image-Pfad einschließlich Eigentümer – ohne dieses chown könnte der
# non-root-Prozess nicht in /data schreiben und der Schutz verlöre seine Historie bei jedem Restart.
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
VOLUME /data

USER $APP_UID
ENTRYPOINT ["dotnet", "ngxPMIpUpdater.dll"]
