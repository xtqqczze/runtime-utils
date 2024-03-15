FROM mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-22.04
ADD docker-entrypoint.sh /
RUN chmod +x /docker-entrypoint.sh
RUN apt-get update && apt-get install -y dotnet-sdk-6.0
ENTRYPOINT ["/bin/bash", "/docker-entrypoint.sh"]