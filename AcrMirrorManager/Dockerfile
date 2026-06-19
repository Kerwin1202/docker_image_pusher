ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:9.0
ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:9.0

FROM ${SDK_IMAGE} AS build
WORKDIR /src

COPY AcrMirrorManager.csproj ./
RUN dotnet restore ./AcrMirrorManager.csproj

COPY . ./
RUN dotnet publish ./AcrMirrorManager.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM ${ASPNET_IMAGE} AS final
WORKDIR /app
ARG APP_UID=1654

ENV ASPNETCORE_URLS=http://+:15187 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TZ=Asia/Shanghai

RUN mkdir -p /app/App_Data && chown -R ${APP_UID}:0 /app/App_Data

COPY --from=build /app/publish ./

USER ${APP_UID}
EXPOSE 15187

ENTRYPOINT ["dotnet", "AcrMirrorManager.dll"]
