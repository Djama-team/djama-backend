FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Djama.Api/Djama.Api.csproj src/Djama.Api/
RUN dotnet restore src/Djama.Api/Djama.Api.csproj
COPY src/ src/
RUN dotnet publish src/Djama.Api/Djama.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "Djama.Api.dll"]
