FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build

COPY *.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

ENTRYPOINT ["dotnet", "app/Runner.dll"]