# GlasFlow ERP — container image for cloud demos (Render, Fly.io, Azure Container Apps...).
# Runs the API + web UI on SQLite, so no database server is needed. Data is re-seeded on each fresh start.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/GlasFlow.Api/GlasFlow.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:10000 \
    Database__Provider=Sqlite \
    ConnectionStrings__GlasFlowSqlite="Data Source=/tmp/glasflow.db"
EXPOSE 10000
ENTRYPOINT ["dotnet", "GlasFlow.Api.dll"]
