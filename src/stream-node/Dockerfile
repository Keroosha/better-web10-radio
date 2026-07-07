FROM debian:bookworm-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        chromium \
        ffmpeg \
        liquidsoap \
        xvfb \
        dumb-init \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/web10/stream-node
COPY scripts/check-runtime.sh ./scripts/check-runtime.sh

ENTRYPOINT ["/usr/bin/dumb-init", "--"]
CMD ["./scripts/check-runtime.sh"]
