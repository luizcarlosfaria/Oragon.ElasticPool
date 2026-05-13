FROM mcr.microsoft.com/dotnet/sdk:10.0

USER root

ENV DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true \
    LANG=C.UTF-8 \
    PATH="${PATH}:/root/.dotnet/tools"

RUN apt update && \
    apt install -y \
    software-properties-common && \
    add-apt-repository ppa:dotnet/backports \
    && rm -rf /var/lib/apt/lists/*


RUN add-apt-repository ppa:dotnet/backports \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        gnupg \
        git \
        openjdk-21-jdk \
        dotnet-sdk-8.0 \
        dotnet-sdk-9.0 \
        dotnet-sdk-10.0 \
    && rm -rf /var/lib/apt/lists/*

RUN dotnet tool install --global dotnet-sonarscanner \
    && dotnet tool install --global dotnet-coverage

# Jenkins runs RabbitMQ integration tests through Testcontainers, so the build
# container needs Docker CLI access to the host Docker socket.
RUN apt-get update && \
    apt-get install -y ca-certificates curl gnupg && \
    install -m 0755 -d /etc/apt/keyrings && \
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg && \
    chmod a+r /etc/apt/keyrings/docker.gpg && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
    tee /etc/apt/sources.list.d/docker.list > /dev/null && \
    apt-get update && \
    apt-get install -y docker-ce-cli
