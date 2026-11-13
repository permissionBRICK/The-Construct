# The stand-alone record store (docs/plans/construct-platform-design.md 5.4): one image, one volume
# holding its SQLite file, plain HTTP behind the operator's TLS proxy.
# Build from service/:  docker build -f src/Constructd.Store.Host/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Constructd.Store/ src/Constructd.Store/
COPY src/Constructd.Store.Host/ src/Constructd.Store.Host/
RUN dotnet publish src/Constructd.Store.Host/Constructd.Store.Host.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /data && chown $APP_UID /data
VOLUME /data
ENV ASPNETCORE_URLS=http://+:8080 \
    Store__DatabasePath=/data/store.db
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["./constructd-store"]
