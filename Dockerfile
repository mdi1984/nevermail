# Use the SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.22-aot AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

RUN apk add clang binutils musl-dev build-base zlib-static

COPY ["Nevermail/Nevermail.csproj", "Nevermail/"]
RUN dotnet restore --runtime linux-musl-x64 "./Nevermail/Nevermail.csproj"
COPY . .
WORKDIR "/src/Nevermail"
RUN dotnet publish -r linux-musl-x64 -c $BUILD_CONFIGURATION -o /app/publish "./Nevermail.csproj"

FROM scratch AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
# 8080: HTTP APIs and web UI, 1025: SMTP
EXPOSE 8080 1025
ENV DOTNET_SYSTEM_NET_SECURITY_OPENSSL_VERSION=""
COPY --from=publish /app/publish .
ENTRYPOINT ["/app/Nevermail"]
