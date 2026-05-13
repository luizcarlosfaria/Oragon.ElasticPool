FROM mcr.microsoft.com/dotnet/sdk:10.0

USER root

ENV DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true \
    LANG=C.UTF-8 \
    PATH="${PATH}:/root/.dotnet/tools"

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
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
RUN curl -fsSL https://get.docker.com -o get-docker.sh \
    && sh get-docker.sh \
    && rm get-docker.sh
