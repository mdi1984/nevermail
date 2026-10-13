# NativeAOT can't cross-compile between CPU architectures here, so each platform builds natively
# (CI uses an arm64 runner for linux/arm64). TARGETARCH is set by BuildKit: amd64 or arm64.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.22-aot AS publish
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src

RUN apk add clang binutils musl-dev build-base zlib-static

RUN case "${TARGETARCH:-amd64}" in \
        amd64) echo linux-musl-x64 ;; \
        arm64) echo linux-musl-arm64 ;; \
        *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac > /rid

COPY ["Nevermail/Nevermail.csproj", "Nevermail/"]
RUN dotnet restore --runtime "$(cat /rid)" "./Nevermail/Nevermail.csproj"
COPY . .
WORKDIR "/src/Nevermail"
RUN dotnet publish -r "$(cat /rid)" -c $BUILD_CONFIGURATION -o /app/publish "./Nevermail.csproj"

FROM scratch AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
# 8080: HTTP APIs and web UI, 1025: SMTP
EXPOSE 8080 1025
ENV DOTNET_SYSTEM_NET_SECURITY_OPENSSL_VERSION=""
COPY --from=publish /app/publish .
ENTRYPOINT ["/app/Nevermail"]
