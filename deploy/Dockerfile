FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /source
ARG TARGETARCH
COPY global.json Directory.Build.props Directory.Packages.props LICENSE THIRD-PARTY-NOTICES.md ./
COPY src/VarPlats.Core/ src/VarPlats.Core/
COPY src/VarPlats.ServiceDefaults/ src/VarPlats.ServiceDefaults/
COPY src/VarPlats.Web/ src/VarPlats.Web/
COPY tools/ThirdPartyNoticeGenerator/ tools/ThirdPartyNoticeGenerator/
RUN case "${TARGETARCH:-$(uname -m)}" in \
      amd64|x86_64) arch=x64 ;; \
      arm64|aarch64) arch=arm64 ;; \
      *) echo "Unsupported image architecture" >&2; exit 1 ;; \
    esac \
    && dotnet restore src/VarPlats.Web/VarPlats.Web.csproj --locked-mode \
    && dotnet publish src/VarPlats.Web/VarPlats.Web.csproj -c Release --no-restore -o /publish /p:UseAppHost=false \
    && dotnet restore tools/ThirdPartyNoticeGenerator/ThirdPartyNoticeGenerator.csproj --locked-mode \
    && dotnet run --project tools/ThirdPartyNoticeGenerator/ThirdPartyNoticeGenerator.csproj -c Release --no-restore -- /publish "${NUGET_PACKAGES:-/root/.nuget/packages}" "linux-$arch" \
    && mkdir -p /app/runtimes \
    && find /publish -mindepth 1 -maxdepth 1 ! -name runtimes -exec cp -a {} /app/ \; \
    && for rid in "linux-$arch" linux "unix-$arch" unix any; do \
      if [ -d "/publish/runtimes/$rid" ]; then cp -a "/publish/runtimes/$rid" /app/runtimes/; fi; \
    done \
    && test -f "/app/runtimes/linux-$arch/native/libSkiaSharp.so" \
    && test -f "/app/runtimes/linux-$arch/native/Magick.Native-Q8-$arch.dll.so"

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0@sha256:ca396ece3f2a2e62c7a03f1d7d710b8bc7e8c9dea5597d091178d9f5bfca13f0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "VarPlats.Web.dll"]
