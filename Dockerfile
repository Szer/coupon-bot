FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/sdk:10.0.2-noble-arm64v8 AS build
WORKDIR /src

COPY src/CouponHubBot/CouponHubBot.fsproj src/CouponHubBot/
RUN dotnet restore src/CouponHubBot/CouponHubBot.fsproj

COPY src/ src/
RUN dotnet publish src/CouponHubBot/CouponHubBot.fsproj -c Release -o /publish

FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/aspnet:10.0.2-noble-arm64v8 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish .
ENTRYPOINT ["dotnet", "CouponHubBot.dll"]
