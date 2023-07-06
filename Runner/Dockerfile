FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy as build-runner
WORKDIR /source
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /runner --no-restore

FROM mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-22.04
COPY --from=build-runner /runner /runner
RUN apt-get update && apt-get install -y dotnet-sdk-6.0

WORKDIR /runner
ENTRYPOINT ["dotnet", "Runner.dll"]