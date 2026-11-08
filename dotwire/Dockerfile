FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["dotwire/dotwire.csproj", "dotwire/"]
RUN dotnet restore "dotwire/dotwire.csproj"
COPY . .
WORKDIR "/src/dotwire"
RUN dotnet publish "./dotwire.csproj" -c $BUILD_CONFIGURATION -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.14 AS final
LABEL org.opencontainers.image.source="https://github.com/pasztor-mark/dotwire" \
      org.opencontainers.image.licenses="Apache-2.0"
USER $APP_UID
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish/dotwire ./dotwire
HEALTHCHECK --interval=10s --timeout=3s --retries=5 CMD ["/app/dotwire", "--healthcheck"]
ENTRYPOINT ["./dotwire"]
