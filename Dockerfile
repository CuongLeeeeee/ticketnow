FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src
COPY . .
RUN dotnet publish "$PROJECT" -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ARG DLL
ENV DLL_NAME=$DLL ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT dotnet $DLL_NAME