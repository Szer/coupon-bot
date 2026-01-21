FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG FORCE_REBUILD=0

# copy solution and restore/build
COPY . .
RUN dotnet publish "tests/FakeAzureOcrApi/FakeAzureOcrApi.fsproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://*:8081
EXPOSE 8081
ENTRYPOINT ["dotnet", "FakeAzureOcrApi.dll"]

