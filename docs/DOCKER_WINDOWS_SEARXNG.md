# SearXNG with Docker

A local SearXNG search engine for the web tools, on Windows or a Mac. Back to the [README](../README.md).

## Prerequisite

- Docker Desktop

## Prepare directory

On Windows, use a WSL (Ubuntu) shell; on a Mac, use Terminal. Start in your home directory (`~`).

```bash
mkdir -p ~/searxng/core-config/
cd ~/searxng/
```

## Download config files

```bash
curl -fsSL \
  -O https://raw.githubusercontent.com/searxng/searxng/master/container/docker-compose.yml \
  -O https://raw.githubusercontent.com/searxng/searxng/master/container/.env.example
```

## Copy env example

```bash
cp -i .env.example .env
nano .env
```

### .ENV content

Uncomment `SEARXNG_PORT` and set your port (the tests below use `8888`).

## Generate secret key

This key goes in `secret_key` in your `settings.yml` file.

```bash
python3 -c "import secrets; print(secrets.token_bytes(32).hex())"
```

## Create settings.yml

```bash
cd ~/searxng/core-config

curl -fsSL -O https://raw.githubusercontent.com/searxng/searxng/master/container/settings.template.yml
cp -i settings.template.yml settings.yml
nano settings.yml
```

### settings.yml content

Read [the documentation](https://docs.searxng.org/admin/settings/) before changing the defaults.

```yaml
use_default_settings: true

search:
  safe_search: 0
  formats:
    - html
    - csv
    - json
    - rss

server:
  limiter: false
  image_proxy: true
  secret_key: "<your_secret_key>"
  public_instance: false

botdetection:
  limiter: false
  ip_limit:
    enabled: false
```

## Start the server

```bash
docker compose up
```

## Using it in NeonSidekick

On `/tools` › Core › Web, set *Web search method* to `searxng` and *Web SearXNG URL* to `http://localhost:8888`. The URL can come from `NEONSIDEKICK_SEARXNG_URL` instead, but the method must still be `searxng`. The app reads the `json` format, so keep it enabled as above.

## Test output types

```bash
curl -s "http://localhost:8888/search?q=cheese&format=json" | head -c 300
curl -s "http://localhost:8888/search?q=cheese&format=html" | head -c 300
curl -s "http://localhost:8888/search?q=cheese&format=csv" | head -c 300
curl -s "http://localhost:8888/search?q=cheese&format=rss" | head -c 300
```

## (optional) Boot guard `docker-compose.yml`

On Windows, without this, SearXNG can fall back to its default settings after a boot, until you restart the container by hand.

```yaml
# Read the documentation before using the `docker-compose.yml` file:
# https://docs.searxng.org/admin/installation-docker.html
#
# Boot-time guard for Docker Desktop + WSL:
# core-config/ lives on the Ubuntu WSL distro. At a Windows boot, this
# container (restart: always) can start before that filesystem is served,
# and the image's entrypoint then silently regenerates a stock settings.yml
# from its template, so SearXNG runs with default settings until you
# restart the container.
#
# The entrypoint below refuses to start unless the real settings.yml is
# mounted (it checks for the "formats:" line, which is not in the image's
# template), so Docker's backoff simply retries until the source is there.
# Keep a "formats:" line in settings.yml, or adjust the marker below.

name: searxng

services:
  core:
    container_name: searxng-core
    image: docker.io/searxng/searxng:${SEARXNG_VERSION:-latest}
    restart: "always"
    depends_on:
      valkey:
        condition: service_healthy
    ports:
      - ${SEARXNG_HOST:+${SEARXNG_HOST}:}${SEARXNG_PORT:-8080}:${SEARXNG_PORT:-8080}
    env_file: ./.env
    volumes:
      - ./core-config/:/etc/searxng/:Z
      - core-data:/var/cache/searxng/
    entrypoint:
      - /bin/sh
      - -c
      - |
        if [ ! -f /etc/searxng/settings.yml ] || ! grep -qs 'formats:' /etc/searxng/settings.yml; then
          echo "searxng: real settings.yml not mounted yet (host source unavailable) - exiting, Docker will retry"
          exit 1
        fi
        exec /usr/local/searxng/entrypoint.sh

  valkey:
    container_name: searxng-valkey
    image: docker.io/valkey/valkey:9-alpine
    command: valkey-server --save 30 1 --loglevel warning
    restart: "always"
    healthcheck:
      test: ["CMD", "valkey-cli", "ping"]
      interval: 5s
      timeout: 3s
      retries: 5
      start_period: 10s
    volumes:
      - valkey-data:/data/

volumes:
  core-data:
  valkey-data:

```
