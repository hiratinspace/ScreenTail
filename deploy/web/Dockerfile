# syntax=docker/dockerfile:1
# The proxy (ST-007): Caddy serving the built web admin and forwarding /v1 and /health to the API.
# Build from the repo root, as deploy/compose.yaml does:
#   docker build -f deploy/web/Dockerfile --build-arg VITE_GOOGLE_CLIENT_ID=... .
# Dockerfile.dockerignore beside this file decides what of the repository it sees.
#
# Both images are pinned by version and digest; change the tag and the digest together. The Node major
# version is the one in .nvmrc (deployHeaders.test.ts checks).

FROM node:24.22.2-alpine3.24@sha256:8a32c8cf649a49915a66e81e7a609223c708d0adabf288ee3f4077d765542f85 AS build
WORKDIR /repo/web
COPY web/package.json web/package-lock.json ./
# Exactly the lockfile, and no package's install scripts.
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY web/ ./
COPY deploy/web/page-headers.ts /repo/deploy/web/
# Not a secret: the browser is given it. Empty builds a page that says sign-in is not configured.
ARG VITE_GOOGLE_CLIENT_ID=""
RUN npm run build \
 && node /repo/deploy/web/page-headers.ts > /repo/page-headers.caddy

FROM caddy:2.11.7-alpine@sha256:e70b164da81ef698b3ad19af799ea6086ce6d9e48ed4079eb9fe26f6fbf14ee5
# The image gives the binary a file capability to bind low ports. compose.yaml drops every capability,
# and a binary that asks for one it cannot have does not start, so it is removed: Docker lets an
# unprivileged process bind 80 and 443 inside its own network namespace (the sysctl in compose.yaml).
RUN apk add --no-cache libcap-getcap libcap-setcap \
 && if [ -n "$(getcap /usr/bin/caddy)" ]; then setcap -r /usr/bin/caddy; fi \
 && test -z "$(getcap /usr/bin/caddy)" \
 && apk del libcap-getcap libcap-setcap \
 && mkdir -p /data /config \
 && chown -R 10001:10001 /data /config
COPY deploy/web/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /repo/page-headers.caddy /etc/caddy/page-headers.caddy
COPY --from=build /repo/web/dist /srv
# Checked as it will run, with a stand-in name, so a broken Caddyfile fails the build and not the start.
RUN SCREENTAIL_DOMAIN=screentail.invalid caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile \
 && rm -rf /data/caddy /config/caddy \
 && chown -R 10001:10001 /data /config
USER 10001:10001
EXPOSE 80 443 443/udp
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
