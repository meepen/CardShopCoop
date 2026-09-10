FROM myoung34/github-runner:debian-trixie

ARG DOTNET_INSTALL_VERSION=9.0.100

USER root
RUN curl --fail --silent --show-error --location https://dot.net/v1/dotnet-install.sh \
      --output /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --version "${DOTNET_INSTALL_VERSION}" --install-dir /usr/share/dotnet \
 && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
 && rm /tmp/dotnet-install.sh

ENV DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

RUN mkdir -p /home/runner/.nuget \
 && chown -R runner:runner /home/runner/.nuget

USER runner
