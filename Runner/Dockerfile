FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy as build-jitutils
WORKDIR /source
RUN git clone --no-tags --single-branch https://github.com/dotnet/jitutils.git && cd jitutils && ./bootstrap.sh

FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy as build-runner
WORKDIR /source
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /runner --no-restore

FROM mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-22.04
COPY --from=build-runner /runner /runner
COPY --from=build-jitutils /source/jitutils/bin /runner/bin
RUN apt-get update && apt-get install zip -y
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 6.0

WORKDIR /runner
ENTRYPOINT ["dotnet", "Runner.dll"]