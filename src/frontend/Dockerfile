# syntax=docker/dockerfile:1
# Frontend image for the compose vertical slice / B6. Builds both Bun workspaces and serves
# them from nginx: the public stage at '/', the admin cabinet at '/admin/'. Debian-based
# images only (SPEC §9 forbids Alpine/musl).

FROM oven/bun:1-debian AS build
WORKDIR /app

# Install with only manifests first for layer caching.
COPY package.json bun.lock ./
COPY shared/package.json ./shared/package.json
COPY web-stage/package.json ./web-stage/package.json
COPY admin/package.json ./admin/package.json
RUN bun install --frozen-lockfile

COPY . .
# web-stage is served from '/'; admin is served under '/admin/' so both live behind one origin.
# admin's tsc gate already runs in CI/local; here we only need the Vite bundle with the right base.
RUN bun run --filter @web10/web-stage build \
    && (cd admin && bunx vite build --base=/admin/)

FROM nginx:stable-bookworm AS runtime
RUN groupadd --gid 1000 web10 \
    && useradd --uid 1000 --gid 1000 --no-create-home --shell /usr/sbin/nologin web10 \
    && mkdir -p /var/cache/nginx /var/log/nginx /tmp/nginx \
    && chown -R 1000:1000 /var/cache/nginx /var/log/nginx /tmp/nginx \
    && sed -i \
      -e 's/^user  nginx;/user  web10;/' \
      -e 's@pid        /run/nginx.pid;@pid        /tmp/nginx.pid;@' \
      -e 's@error_log  /var/log/nginx/error.log notice;@error_log  /dev/stderr notice;@' \
      -e 's@access_log  /var/log/nginx/access.log  main;@access_log  /dev/stdout main;@' \
      /etc/nginx/nginx.conf
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
USER 1000:1000
COPY --from=build /app/web-stage/dist /usr/share/nginx/html
COPY --from=build /app/admin/dist /usr/share/nginx/html/admin
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
