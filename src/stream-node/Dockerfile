FROM debian:trixie-slim

ARG LIQUIDSOAP_DEB_URL="https://github.com/savonet/liquidsoap/releases/download/v2.4.0/liquidsoap_2.4.0-debian-trixie-ocaml4.14.2-3_amd64.deb"
ARG LIQUIDSOAP_DEB_SHA256="dd437f1a6af842aa4ebc61090fc3270890a592a0a531026e16b49e7b78c858fe"
ARG DMO_KEYRING_DEB_URL="https://www.deb-multimedia.org/pool/main/d/deb-multimedia-keyring/deb-multimedia-keyring_2024.9.1_all.deb"
ARG DMO_KEYRING_DEB_SHA256="8dc6cbb266c701cfe58bd1d2eb9fe2245a1d6341c7110cfbfe3a5a975dcf97ca"

RUN set -eu; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates python3 gpgv; \
    keyring_deb=/tmp/deb-multimedia-keyring.deb; \
    python3 -c "from urllib.request import urlretrieve; urlretrieve('${DMO_KEYRING_DEB_URL}', '${keyring_deb}')"; \
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
        socat; \
    liquidsoap_deb=/tmp/liquidsoap.deb; \
    python3 -c "from urllib.request import urlretrieve; urlretrieve('${LIQUIDSOAP_DEB_URL}', '${liquidsoap_deb}')"; \
    printf '%s  %s\n' "${LIQUIDSOAP_DEB_SHA256}" "${liquidsoap_deb}" | sha256sum --check --status -; \
    apt-get install -y --no-install-recommends "${liquidsoap_deb}"; \
    rm -f "${liquidsoap_deb}"; \
    apt-get purge -y deb-multimedia-keyring gpgv; \
    rm -f /etc/apt/sources.list.d/dmo.sources; \
    rm -rf /var/lib/apt/lists/*

WORKDIR /opt/web10/stream-node

COPY scripts/supervisor.py ./scripts/supervisor.py
COPY scripts/check-runtime.sh ./scripts/check-runtime.sh
COPY scripts/create-smoke-track.py ./scripts/create-smoke-track.py
COPY liquidsoap/web10.liq ./liquidsoap/web10.liq

RUN chmod 0755 ./scripts/check-runtime.sh ./scripts/create-smoke-track.py \
    && install -d -m 0775 /run/web10

ENTRYPOINT ["/usr/bin/dumb-init", "--"]
CMD ["python3", "./scripts/supervisor.py", "run"]
