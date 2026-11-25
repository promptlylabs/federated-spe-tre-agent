FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src
COPY ["Agent/Agent.Api/Agent.Api.csproj", "Agent/Agent.Api/"]
COPY ["Credentials/Credentials.Models/Credentials.Models.csproj", "Credentials/Credentials.Models/"]
COPY ["Shared/FiveSafesTes.Core/FiveSafesTes.Core.csproj", "Shared/FiveSafesTes.Core/"]
RUN dotnet restore "Agent/Agent.Api/Agent.Api.csproj"

COPY ["Agent/Agent.Api/", "Agent/Agent.Api/"]
COPY ["Credentials/Credentials.Models/", "Credentials/Credentials.Models/"]
COPY ["Shared/FiveSafesTes.Core/", "Shared/FiveSafesTes.Core/"]
WORKDIR "/src/Agent/Agent.Api"
RUN dotnet publish "Agent.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    --self-contained=false \
    /p:UseAppHost=false

FROM --platform=${TARGETPLATFORM} mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER root
COPY rootca.pem /usr/local/share/ca-certificates/chi-ansible.crt
RUN chmod 644 /usr/local/share/ca-certificates/*.crt && \
	update-ca-certificates

# Semgrep scans researcher code before it runs (SemgrepCodeScanner). Installed here, pinned with hashes; it runs offline.
COPY Agent/Agent.Api/ScanRules/requirements.txt /tmp/semgrep-requirements.txt
RUN apt-get update && \
	apt-get install -y --no-install-recommends python3 python3-venv && \
	rm -rf /var/lib/apt/lists/* && \
	python3 -m venv /opt/semgrep && \
	/opt/semgrep/bin/pip install --no-cache-dir --require-hashes -r /tmp/semgrep-requirements.txt && \
	rm -rf /tmp/semgrep-requirements.txt /root/.cache/pip

USER app
EXPOSE 8080
EXPOSE 443
WORKDIR /app
COPY --chown=app:app --from=build /app/publish .
ENTRYPOINT ["dotnet", "Agent.Api.dll"]
