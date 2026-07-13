FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Web10.Radio.sln ./
COPY Web10.Radio.API/Web10.Radio.API.fsproj Web10.Radio.API/
COPY Web10.Radio.Application/Web10.Radio.Application.fsproj Web10.Radio.Application/
COPY Web10.Radio.Database/Web10.Radio.Database.fsproj Web10.Radio.Database/
COPY Web10.Radio.Telegram/Web10.Radio.Telegram.fsproj Web10.Radio.Telegram/
COPY Web10.Radio.Tests/Web10.Radio.Tests.fsproj Web10.Radio.Tests/
COPY Web10.Radio.Migrator/Web10.Radio.Migrator.fsproj Web10.Radio.Migrator/
RUN dotnet restore Web10.Radio.sln

COPY . .
RUN dotnet publish Web10.Radio.API/Web10.Radio.API.fsproj -c Release -o /app/publish --no-restore
# Empty writable dirs owned by uid 1000 so fresh named volumes mounted over them
# inherit that ownership (the dev stack no longer runs a storage-init one-shot).
RUN mkdir -p /runtime-data/storage /runtime-data/data-protection \
        /runtime-data/cache/audio /runtime-data/cache/covers /runtime-data/cache/tmp

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER 1000:1000
COPY --from=build /app/publish .
COPY --from=build --chown=1000:1000 /runtime-data/storage /var/lib/web10/storage
COPY --from=build --chown=1000:1000 /runtime-data/cache /var/lib/web10/cache
COPY --from=build --chown=1000:1000 /runtime-data/data-protection /var/lib/web10/data-protection
ENTRYPOINT ["dotnet", "Web10.Radio.API.dll"]
