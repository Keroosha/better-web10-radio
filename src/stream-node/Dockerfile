FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Web10.Radio.StreamNode/Web10.Radio.StreamNode.fsproj Web10.Radio.StreamNode/
COPY Web10.Radio.StreamNode.Tools/Web10.Radio.StreamNode.Tools.fsproj Web10.Radio.StreamNode.Tools/
RUN dotnet restore Web10.Radio.StreamNode/Web10.Radio.StreamNode.fsproj
COPY . .
RUN dotnet publish Web10.Radio.StreamNode/Web10.Radio.StreamNode.fsproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:PublishTrimmed=false \
    -p:PublishSingleFile=false \
    --output /app/publish

FROM debian:trixie-slim

ARG LIQUIDSOAP_DEB_URL="https://github.com/savonet/liquidsoap/releases/download/v2.4.0/liquidsoap_2.4.0-debian-trixie-ocaml4.14.2-3_amd64.deb"
ARG LIQUIDSOAP_DEB_SHA256="dd437f1a6af842aa4ebc61090fc3270890a592a0a531026e16b49e7b78c858fe"
ARG DMO_KEYRING_DEB_URL="https://www.deb-multimedia.org/pool/main/d/deb-multimedia-keyring/deb-multimedia-keyring_2024.9.1_all.deb"
ARG DMO_KEYRING_DEB_SHA256="8dc6cbb266c701cfe58bd1d2eb9fe2245a1d6341c7110cfbfe3a5a975dcf97ca"

RUN set -eu; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl gpgv; \
    keyring_deb=/tmp/deb-multimedia-keyring.deb; \
    curl --fail --location --proto '=https' --tlsv1.2 "${DMO_KEYRING_DEB_URL}" --output "${keyring_deb}"; \
    printf '%s  %s\n' "${DMO_KEYRING_DEB_SHA256}" "${keyring_deb}" | sha256sum --check --status -; \
    dpkg -i "${keyring_deb}"; \
    rm -f "${keyring_deb}"; \
    printf '%s\n' \
        'Types: deb' \
        'URIs: https://www.deb-multimedia.org' \
        'Suites: trixie' \
        'Components: main non-free' \
        'Signed-By: /usr/share/keyrings/deb-multimedia-keyring.pgp' \
        'Enabled: yes' > /etc/apt/sources.list.d/dmo.sources; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
        chromium \
        ffmpeg \
        xvfb \
        dumb-init \
        socat \
        unclutter-xfixes; \
    liquidsoap_deb=/tmp/liquidsoap.deb; \
    curl --fail --location --proto '=https' --tlsv1.2 "${LIQUIDSOAP_DEB_URL}" --output "${liquidsoap_deb}"; \
    printf '%s  %s\n' "${LIQUIDSOAP_DEB_SHA256}" "${liquidsoap_deb}" | sha256sum --check --status -; \
    apt-get install -y --no-install-recommends "${liquidsoap_deb}"; \
    rm -f "${liquidsoap_deb}"; \
    groupadd --gid 1654 web10; \
    useradd --uid 1654 --gid 1654 --create-home --home-dir /home/web10 --shell /usr/sbin/nologin web10; \
    apt-get purge -y curl gpgv deb-multimedia-keyring; \
    rm -f /etc/apt/sources.list.d/dmo.sources; \
    rm -rf /var/lib/apt/lists/*

WORKDIR /opt/web10/stream-node
COPY --from=build /app/publish/ ./
COPY scripts/check-runtime.sh ./scripts/check-runtime.sh
COPY liquidsoap/web10.liq ./liquidsoap/web10.liq
RUN chmod 0755 ./scripts/check-runtime.sh \
    && install -d -o 1654 -g 1654 -m 0775 /run/web10 /var/lib/web10/storage /var/lib/web10/cache

HEALTHCHECK --interval=2s --timeout=5s --retries=30 --start-period=5s CMD ["./Web10.Radio.StreamNode", "--health-check", "http://127.0.0.1:18080/healthz"]
ENTRYPOINT ["/usr/bin/dumb-init", "--"]
CMD ["./Web10.Radio.StreamNode", "run"]
