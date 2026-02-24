FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore magent.sln && dotnet publish src/Magent.Cli/Magent.Cli.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/out .
COPY config ./config
COPY data ./data
COPY out ./out
ENTRYPOINT ["dotnet", "Magent.Cli.dll"]
