# MQTT hardware control on .NET

- 20 simulated factory devices publish their live readings over **real MQTT**.
- When you click a tile, a command goes through MQTT and the machine stops.

<img src="images/for-github.png" alt="Test screenshot" width="838" />

## MQTT in 60 seconds (if you've never touched it)

- **MQTT is not a queue — it's a broker.** Nobody talks to anybody directly, and messages are not
  lined up for a single consumer. Instead there is a middleman (the broker): everyone connects to
  it, some **publish** messages, others **subscribe** — and the broker instantly hands each
  message to whoever is subscribed.
- **Topics & subscriptions in one sentence:** a *topic* is just a named channel like
  `health/press-01`, and a *subscription* is saying "give me everything published to this topic"
  — with wildcards like `health/+` meaning "health of **every** device with a single subscription".
- **Mosquitto** is the broker we use — a tiny, battle-tested open-source MQTT server. In this
  project it runs as a stock Docker image (`eclipse-mosquitto:2`) and is configured by one small
  file, [`mosquitto/mosquitto.conf`](mosquitto/mosquitto.conf): listen on TCP `1883`, allow
  anonymous clients (fine for a local demo), persist retained messages, log to stdout. 

## Project tour

```
Shared/   the "wire language" both sides speak
Hw/       the simulated hardware (20 MQTT clients)
Ui/       the control panel (ASP.NET Core + SignalR web page)
```

### `Shared/Mqtt.cs` — the shared data contracts

One file describes the entire wire protocol, used by both `Hw` and `Ui`:

- **`MqttTopic`** — topic names: `command/{id}` (panel → device), `health/{id}` (device → panel,
  ~1 per second), `isonline/{id}` (device → everyone).
- **`MqttPayload_Command` / `MqttPayload_Health` / `MqttPayload_IsOnline`** — the three message
  shapes that travel through those topics, serialized to JSON.
- **`HwType` / `HwState`** — plain string constants (`"press"`, `"overheat"`, …) instead of enums,
  so the JSON on the wire stays human-readable in `mosquitto_sub`.

### `Hw/HwItem.cs` — the "physics" of one device

A pure C# class with **no networking at all** — just behavior:

- **`Work(random)` — the tick.** Called once per second. Each tick the device: advances its
  internal clock, maybe develops (or recovers from) a random fault, computes its current power
  draw (nominal power × duty cycle × speed factor × noise) and body temperature (a Celsius model
  with inertia — the body heats up and cools down gradually, like real iron), and returns a fresh
  `MqttPayload_Health` snapshot.
- **`Obey(command)` — accepting a command.** A plain `switch`: `STOP` puts the machine into
  standby, `START` resumes full speed, `SET_SPEED` applies a speed factor. The command simply
  mutates internal state — the *next tick* naturally reflects it in the readings.
- **Faults** are part of the fun: overheat (protection lowers the speed, temperature → ~98 °C),
  jam (the motor draws *more*), and short circuit — which "trips the breaker" and takes the
  device offline for a while.

### `Hw/HwMqttChip.cs` — the simulated "MQTT chip"

If `HwItem` is the motor, `HwMqttChip` is the little network board bolted onto it. One chip = one
device = **one real TCP connection** to the broker. It:

1. connects with retry (the broker container may start later — the chip just keeps trying),
2. subscribes to **its own** `command/{id}` topic on every (re)connect,
3. once per second calls `Work()` and publishes the result to `health/{id}`,
4. hands every incoming command to `Obey()` (guarded by a `lock` — ticks and commands arrive on
   different threads),
5. announces `isonline/{id}` — but **only when the state changes**, not every second.

Two MQTT features worth knowing here:

- **Retained messages** (`retain` flag): the broker keeps the *last* message of a topic and gives
  it to any new subscriber immediately. Our `isonline` statuses are retained — so a freshly
  started panel instantly knows who is alive without waiting for anyone to speak.
- **LWT (Last Will and Testament):** at connect time each chip leaves a "will" with the broker:
  *"if I ever vanish without saying goodbye — publish `isonline: false` on my behalf."* Kill the
  `hw` container and the broker itself reports every device offline. Nobody polls anything.

The chips also use two QoS (delivery guarantee) levels: health readings go as QoS 0
("fire and forget" — a lost reading doesn't matter, a new one comes in a second), while commands
and statuses go as QoS 1 ("at least once" — these must arrive).

### `Hw/HwFleetWorker.cs` — the background service that runs the fleet

A standard .NET `BackgroundService` hosted in a console app ([`Hw/Program.cs`](Hw/Program.cs)).
On startup it:

1. reads its settings from environment variables (`MQTT_HOST`, `HW_COUNT`, `FAULT_PROBABILITY`…),
2. asks `HwFleetFactory` to build N devices with realistic names (`press-01`, `conveyor-02`, …)
   and per-type nominal power,
3. wraps each device in its own `HwMqttChip` and starts all of their `RunAsync` loops
   **concurrently** (collecting the `Task`s without awaiting each one),
4. then `await Task.WhenAll(...)` — which keeps the service alive until shutdown.

Twenty devices, twenty TCP connections, a handful of threads — thanks to `async/await`.

### `Ui/` — the control panel (briefly)

- **`MqttBridge`** — the mirror image of a chip, but for the whole floor: subscribes to
  `health/+` and `isonline/+` (wildcards!), remembers the latest reading of every device, keeps a
  small journal of *state changes* (faults, recoveries, operator actions), and once per second
  pushes a `FactorySnapshot` to every browser via SignalR. It also publishes commands back.
- **`FactoryHub`** — the SignalR hub: sends a new browser its initial snapshot + chart history,
  and exposes one method, `SendCommand`, that the browser invokes when you click a tile.
- **`wwwroot/`** — a single static page (Bootstrap + Chart.js): live chart with a power-limit
  line, a colored tile per device, start/stop/speed buttons, and the journal.

---

## Running it

### Containers: what is configured where

Everything lives in one file — [`docker-compose.yml`](docker-compose.yml) — which defines three
services on a shared network (inside it, service names work as DNS hostnames — that's why the
apps connect to `MQTT_HOST: "broker"`, not an IP):

| Service | What it is | Configured by |
|---|---|---|
| `broker` | stock `eclipse-mosquitto:2` image, port `1883` (also exposed to your host) | [`mosquitto/mosquitto.conf`](mosquitto/mosquitto.conf), mounted read-only into the container |
| `ui` | the control panel, built from [`Ui/Dockerfile`](Ui/Dockerfile), port `8080` | env vars: `MQTT_HOST`, `MQTT_PORT`, `POWER_LIMIT_KW` (the chart's limit line) |
| `hw` | the device fleet, built from [`Hw/Dockerfile`](Hw/Dockerfile) | env vars: `MQTT_HOST`, `MQTT_PORT`, `HW_COUNT` (fleet size), `FAULT_PROBABILITY` (chaos level) |

Both Dockerfiles are classic two-stage .NET builds: compile with the `sdk` image, run on the
slim `aspnet`/`runtime` image. All app settings come **only** from environment variables with
sane code defaults — there are no appsettings files.

### Start everything

```bash
docker compose up --build
# Control panel:  http://localhost:8080
```

That's the whole deployment. Stop it with `docker compose down`.

### Peek at the raw MQTT traffic (recommended!)

The broker port is exposed, so you can watch the entire floor talk:

```bash
# everything flowing through the broker, live:
docker compose exec broker mosquitto_sub -t '#' -v

# stop a press by hand (exactly what a tile click does):
docker compose exec broker mosquitto_pub -t 'command/press-01' -m '{"command":"STOP"}'
```

### Local development (without Docker for the .NET apps)

```bash
docker compose up broker     # broker only
dotnet run --project Ui      # http://localhost:5080
dotnet run --project Hw
```

Both apps default to `localhost:1883`, so they find the dockerized broker automatically.
