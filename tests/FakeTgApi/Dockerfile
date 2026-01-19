FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS build
WORKDIR /src
COPY tests/FakeTgApi/FakeTgApi.fsproj tests/FakeTgApi/
RUN dotnet restore tests/FakeTgApi/FakeTgApi.fsproj
COPY tests/FakeTgApi/ tests/FakeTgApi/
RUN dotnet publish tests/FakeTgApi/FakeTgApi.fsproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.2 AS runtime
WORKDIR /app
COPY --from=build /publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FakeTgApi.dll"]

