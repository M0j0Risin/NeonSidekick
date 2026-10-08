# SearXNG with Docker (Windows)

A local SearXNG for the web tools. Back to the [README](../README.md).

## Prerequisite

- Docker Desktop (Windows, or a Mac)

## Prepare directory

Use WSL (Ubuntu) on Windows. Start in your home directory (~). On a Mac, run the same commands in Terminal with Docker Desktop; skip WSL.

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

Read the documentation before extending the defaults: https://docs.searxng.org/admin/settings/

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

On `/tools` › Web, set *Web search method* to `searxng` and *Web SearXNG URL* to `http://localhost:8888` (or set `NEONSIDEKICK_SEARXNG_URL`). The `json` format above is required.

## Test output types

```bash
curl -s "http://localhost:8888/search?q=cheese&format=json" | head -c 300
curl -s "http://localhost:8888/search?q=cheese&format=html" | head -c 300
curl -s "http://localhost:8888/search?q=cheese&format=csv" | head -c 300
curl -s "http://localhost:8888/search?q=cheese&format=rss" | head -c 300
```
