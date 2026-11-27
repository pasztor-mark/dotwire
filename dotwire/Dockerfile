FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
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

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0.0@sha256:b392cb6e185b0c02cd8066b8b3b2e78d58f7cc15ba5d52ea3e2bfe8f3c0060f9 AS final
LABEL org.opencontainers.image.source="https://github.com/pasztor-mark/dotwire" \
      org.opencontainers.image.licenses="Apache-2.0"
USER $APP_UID
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish/dotwire ./dotwire
HEALTHCHECK --interval=10s --timeout=3s --retries=5 CMD ["/app/dotwire", "--healthcheck"]
ENTRYPOINT ["./dotwire"]
