FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["SilverCommandRefGen/SilverCommandRefGen.csproj", "SilverCommandRefGen/"]
RUN dotnet restore "SilverCommandRefGen/SilverCommandRefGen.csproj"
COPY . .
WORKDIR "/src/SilverCommandRefGen"
RUN dotnet build "SilverCommandRefGen.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SilverCommandRefGen.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SilverCommandRefGen.dll"]
