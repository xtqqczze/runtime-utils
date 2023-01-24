FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy as build-jitutils
WORKDIR /source
RUN git clone --no-tags --single-branch https://github.com/dotnet/jitutils.git && cd jitutils && ./bootstrap.sh

FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy as build-runner
WORKDIR /source
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /runner --no-restore

FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy
COPY --from=build-runner /runner /runner
COPY --from=build-jitutils /source/jitutils/bin /runner/bin

WORKDIR /runner
ENTRYPOINT ["dotnet", "Runner.dll"]