FROM debian:trixie-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends nginx libnginx-mod-rtmp curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 1000 web10 \
    && useradd --uid 1000 --gid 1000 --no-create-home --shell /usr/sbin/nologin web10 \
    && mkdir -p /var/cache/nginx /var/log/nginx /var/lib/nginx \
    && chown -R 1000:1000 /var/cache/nginx /var/log/nginx /var/lib/nginx /tmp
USER 1000:1000

COPY nginx.conf /etc/nginx/nginx.conf

EXPOSE 1935 8080

CMD ["nginx", "-g", "daemon off;"]
