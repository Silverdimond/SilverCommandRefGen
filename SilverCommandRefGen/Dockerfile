FROM mcr.microsoft.com/dotnet/sdk:7.0 as build-env
COPY . ./
RUN dotnet publish ./SilverCommandRefGen/SilverCommandRefGen.csproj -c Release -o out --no-self-contained
FROM mcr.microsoft.com/dotnet/sdk:7.0
COPY --from=build-env /out .
ENTRYPOINT [ "dotnet", "/SilverCommandRefGen.dll" ]