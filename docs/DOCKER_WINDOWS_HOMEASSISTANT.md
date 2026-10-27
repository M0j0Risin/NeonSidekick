# Home Assistant and MQTT with Docker

Home Assistant, an MQTT broker (Mosquitto) and, if you have Ring cameras, ring-mqtt, all on Windows, for the Home Assistant tools. Back to the [README](../README.md).

## Prerequisites

- Docker Desktop
- For Ring cameras: your Ring account's email, password and two-factor codes

## Prepare directory

Use a WSL (Ubuntu) shell, starting in your home directory (`~`).

```bash
mkdir -p ~/home-assistant/config ~/home-assistant/mosquitto/config ~/home-assistant/mosquitto/data ~/home-assistant/ring-mqtt
cd ~/home-assistant/
```

## Create docker-compose.yml

Set `TZ` to your time zone. Leave out the `ring-mqtt` service if you have no Ring devices.

```yaml
services:
  homeassistant:
    container_name: homeassistant
    image: "ghcr.io/home-assistant/home-assistant:stable"
    volumes:
      - ./config:/config
      - /etc/localtime:/etc/localtime:ro
    restart: always
    privileged: true
    ports:
      - "8123:8123"
    environment:
      TZ: America/Chicago

  mosquitto:
    container_name: mosquitto
    image: eclipse-mosquitto:2
    restart: unless-stopped
    volumes:
      - ./mosquitto/config:/mosquitto/config
      - ./mosquitto/data:/mosquitto/data
    # no ports: only Home Assistant and ring-mqtt reach it, on this compose network

  ring-mqtt:
    container_name: ring-mqtt
    image: tsightler/ring-mqtt
    restart: unless-stopped
    depends_on: [mosquitto]
    ports:
      - "127.0.0.1:55123:55123"   # Ring sign-in page, first run only
    volumes:
      - ./ring-mqtt:/data
    logging:
      options: { max-size: 10m, max-file: "3" }
```

The three containers share the compose network, so they reach each other by name (`mosquitto`, `ring-mqtt`). The broker isn't published, so nothing else on your network can reach it.

## Create mosquitto.conf

```bash
cat > ~/home-assistant/mosquitto/config/mosquitto.conf <<'EOF'
persistence true
persistence_location /mosquitto/data/
log_dest stdout
listener 1883
allow_anonymous false
password_file /mosquitto/config/passwd
EOF
```

## Create the MQTT account

The first command asks for a password; Home Assistant and ring-mqtt both sign in with it.

```bash
docker compose run --rm mosquitto mosquitto_passwd -c /mosquitto/config/passwd mqtt
docker compose run --rm mosquitto sh -c 'chown mosquitto:mosquitto /mosquitto/config/passwd && chmod 0700 /mosquitto/config/passwd'
```

## Start Home Assistant and Mosquitto

```bash
docker compose up -d homeassistant mosquitto
```

Open `http://localhost:8123` in Windows and create your Home Assistant account.

## Connect Home Assistant to the broker

In Home Assistant, **Settings › Devices & services › Add integration › MQTT**: broker `mosquitto`, port `1883`, user `mqtt` and the password above.

## Ring cameras with ring-mqtt (optional)

ring-mqtt signs in to your Ring account and publishes your devices to the broker, where Home Assistant finds them by itself. Its snapshots are taken every 30 seconds by default, with or without a Ring Protect plan.

Create `~/home-assistant/ring-mqtt/config.json`. The login goes inside the URL: ring-mqtt doesn't read separate user and password keys.

```json
{
  "mqtt_url": "mqtt://mqtt:<password>@mosquitto:1883"
}
```

Start it and watch its log:

```bash
docker compose up -d ring-mqtt
docker compose logs -f ring-mqtt
```

Open `http://localhost:55123` in Windows, sign in with your Ring account and enter the two-factor code. The log then shows "MQTT connection established" and a "New device" line for each Ring device.

The token is saved in `ring-mqtt/ring-state.json` and gives full access to your Ring account: keep that file out of backups and shares. Once it's saved, you can delete the `55123` port line.

Each camera arrives in Home Assistant with a snapshot camera, `camera.<name>_snapshot`, beside its motion, ding and light entities. For live video, see ring-mqtt's [Video Streaming](https://github.com/tsightler/ring-mqtt/wiki/Video-Streaming) guide.

## Using it in NeonSidekick

1. In Home Assistant, open your profile › **Security** and create a long-lived access token.
2. On `/tools` › Home › HomeAssistant, set *Home Assistant URL* to `http://localhost:8123`, paste the token into *Home Assistant API key*, and turn on *Home Assistant tools*.

For a Ring camera, use its `_snapshot` camera with `ha_camera` or the home bar's 📹: it gives a recent still, where Ring's own integration gives the latest recording's. See [Home Assistant](TOOLS.md#home-assistant) for the tools and [the home bar](INTERFACE.md#the-home-bar) for 📹.

## Check the snapshots

Each line names a camera and the time its last snapshot was taken (Unix seconds). Two readings 30 seconds apart should differ.

```bash
docker exec mosquitto mosquitto_sub -u mqtt -P '<password>' -t 'ring/+/camera/+/snapshot/attributes' -v -W 40
```

## Troubleshooting

* **`denied` pulling `ghcr.io/tsightler/ring-mqtt`:** that copy refuses anonymous pulls; use `tsightler/ring-mqtt` from Docker Hub, as above.
* **ring-mqtt logs "Not authorized":** the password in `mqtt_url` doesn't match the MQTT account, or the login isn't inside the URL.
* **A camera's snapshots time out in ring-mqtt's log:** check that the camera is online in the Ring app.
* **No Ring devices in Home Assistant:** check that the MQTT integration is added; ring-mqtt sends its devices when Home Assistant connects.
