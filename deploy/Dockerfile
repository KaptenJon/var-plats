FROM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/VarPlats.Core/ src/VarPlats.Core/
COPY src/VarPlats.ServiceDefaults/ src/VarPlats.ServiceDefaults/
COPY src/VarPlats.Web/ src/VarPlats.Web/
RUN dotnet restore src/VarPlats.Web/VarPlats.Web.csproj --locked-mode \
    && dotnet publish src/VarPlats.Web/VarPlats.Web.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3@sha256:bae0269049d5061ab7f49995cc1acebf54b75471c096c4a1f874e27661d44045 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "VarPlats.Web.dll"]
