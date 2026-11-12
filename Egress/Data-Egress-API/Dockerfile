FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Egress/Data-Egress-API/Data-Egress-API.csproj", "Egress/Data-Egress-API/"]
COPY ["Shared/FiveSafesTes.Core/FiveSafesTes.Core.csproj", "Shared/FiveSafesTes.Core/"]
RUN dotnet restore "Egress/Data-Egress-API/Data-Egress-API.csproj"
COPY Shared/FiveSafesTes.Core/ Shared/FiveSafesTes.Core/
COPY Egress/Data-Egress-API/ Egress/Data-Egress-API/
RUN dotnet publish "Egress/Data-Egress-API/Data-Egress-API.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    --self-contained=false \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER root
COPY rootca.pem /usr/local/share/ca-certificates/chi-ansible.crt
RUN chmod 644 /usr/local/share/ca-certificates/chi-ansible.crt && \
    update-ca-certificates
USER app
EXPOSE 8080
EXPOSE 443
WORKDIR /app
COPY --chown=app:app --from=build /app/publish .
ENTRYPOINT ["dotnet", "Data-Egress-API.dll"]
