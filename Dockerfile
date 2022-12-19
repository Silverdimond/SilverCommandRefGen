FROM mcr.microsoft.com/dotnet/sdk:7.0 as build-env
COPY . ./
RUN dotnet publish ./SilverCommandRefGen/SilverCommandRefGen.csproj -c Release -o out --no-self-contained
LABEL com.github.actions.name=".NET analyzer"
LABEL com.github.actions.description="Github action that maintains the CODE_METRICS.md file"
LABEL com.github.actions.icon="sliders"
LABEL com.github.actions.color="green"
FROM mcr.microsoft.com/dotnet/sdk:7.0
COPY --from=build-env /out .
ENTRYPOINT [ "dotnet", "/SilverCommandRefGen.dll" ]