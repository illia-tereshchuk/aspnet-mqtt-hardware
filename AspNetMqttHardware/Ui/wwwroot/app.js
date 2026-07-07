// ============================================================
// Factory control panel: SignalR (live updates + commands) + Chart.js (chart)
// Data flow: devices → MQTT → MqttBridge → SignalR → this file,
// and back: click → SignalR SendCommand → MQTT → device.
// ============================================================

const MAX_POINTS = 180; // how many points we keep on the chart

let lastDevices = [];   // devices from the last snapshot (for the "all" buttons)
let overFlags = [];     // for each chart point: was the limit exceeded
let limitKw = 0;
let hoveredId = null;   // which device the cursor is over (for the info line)

// ---- Consumption chart ----
const chart = new Chart(document.getElementById("chart"), {
  type: "line",
  data: {
    labels: [],
    datasets: [
      {
        label: "Consumption, kW",
        data: [],
        borderWidth: 2,
        pointRadius: 0,
        tension: 0.3,
        fill: true,
        backgroundColor: "rgba(13, 110, 253, 0.10)",
        borderColor: "#0d6efd",
        // Above the limit the line turns red.
        segment: {
          borderColor: ctx => (overFlags[ctx.p1DataIndex] ? "#dc3545" : "#0d6efd"),
        },
      },
      {
        label: "Limit",
        data: [],
        borderWidth: 1.5,
        borderDash: [6, 4],
        pointRadius: 0,
        borderColor: "#e6a700",
      },
    ],
  },
  options: {
    animation: false,
    responsive: true,
    maintainAspectRatio: false,
    events: [],  // chart is view-only — no tooltips/hover
    plugins: { legend: { display: false }, tooltip: { enabled: false } },
    scales: {
      x: { ticks: { maxTicksLimit: 6, color: "#666" }, grid: { display: false } },
      y: { beginAtZero: true, ticks: { color: "#666" } },
    },
  },
});

function pushChartPoint(label, totalKw, overLimit) {
  chart.data.labels.push(label);
  chart.data.datasets[0].data.push(totalKw);
  chart.data.datasets[1].data.push(limitKw);
  overFlags.push(overLimit);

  if (chart.data.labels.length > MAX_POINTS) {
    chart.data.labels.shift();
    chart.data.datasets[0].data.shift();
    chart.data.datasets[1].data.shift();
    overFlags.shift();
  }
}

// ---- Equipment tiles ----
const typeIcons = { press: "🔨", conveyor: "📦", cnc: "🛠️", compressor: "💨", vent: "🌀" };

// A tile is created ONCE per device and only its properties are updated afterwards.
// (If we rebuild the grid via innerHTML every second, the element under the cursor
// is destroyed — and the info line loses its target. A stable element = stable info.)
const tiles = new Map(); // deviceId → tile element

function renderDevices(devices) {
  const grid = document.getElementById("devices");

  for (const d of devices) {
    let tile = tiles.get(d.id);

    if (!tile) {
      tile = document.createElement("div");
      // Handler attached once; the fresh state is read from data-attributes at click time.
      tile.onclick = () => {
        if (tile.dataset.online !== "true") return;
        connection.invoke("SendCommand", d.id, tile.dataset.state === "stopped" ? "START" : "STOP", 1.0); // here is the real shit
      };
      // Hover → show the device in the info line (instead of a tooltip).
      tile.onmouseenter = () => { hoveredId = d.id; updateInfo(); };
      tile.onmouseleave = () => { hoveredId = null; updateInfo(); };
      tiles.set(d.id, tile);
      grid.appendChild(tile);
    }

    tile.dataset.state = d.state;
    tile.dataset.online = d.online;
    tile.className = "device-tile st-" + d.state;
    tile.textContent = typeIcons[d.type] ?? "▫";
  }
}

// Info line above the tiles: shows the device under the cursor and updates every second.
function updateInfo() {
  const el = document.getElementById("device-info");
  const d = hoveredId ? lastDevices.find(x => x.id === hoveredId) : null;

  if (!d) {
    el.textContent = "hover over a device";
    el.classList.add("text-secondary");
    return;
  }
  el.classList.remove("text-secondary");
  const conn = d.online ? "" : " · OFFLINE";
  el.textContent = `${d.id} · ${d.powerKw.toFixed(1)} kW · ${d.temperatureC.toFixed(0)} °C · ${d.state}${conn}`;
}

// ---- Render a state snapshot ----
function render(s) {
  limitKw = s.limitKw;
  lastDevices = s.hwViews;

  // Banner: color and text depend on consumption relative to the limit.
  const banner = document.getElementById("banner");
  let css, text;
  if (s.totalKw > s.limitKw) { css = "alert-danger"; text = "🔴 LIMIT EXCEEDED"; }
  else if (s.totalKw > s.limitKw * 0.85) { css = "alert-warning"; text = "⚠ NEAR LIMIT"; }
  else { css = "alert-success"; text = "✅ FACTORY RUNNING"; }
  banner.className = `alert ${css} d-flex justify-content-between align-items-center py-2 px-3 mb-2`;
  document.getElementById("status-text").textContent = text;
  document.getElementById("clock").textContent = s.time;
  document.getElementById("total-kw").textContent = s.totalKw.toFixed(1);
  document.getElementById("limit-kw").textContent = s.limitKw;

  renderDevices(s.hwViews);
  updateInfo();   // if the cursor is on a tile, refresh its readings live

  const online = s.hwViews.filter(d => d.online).length;
  document.getElementById("device-summary").textContent =
    s.hwViews.length > 0 ? `${online}/${s.hwViews.length} connected` : "waiting for telemetry…";

  // Journal
  const log = document.getElementById("log");
  log.innerHTML = "";
  for (const entry of s.log) {
    const li = document.createElement("li");
    li.textContent = `${entry.time} ${entry.message}`;
    log.appendChild(li);
  }
}

// ---- Connect to SignalR ----
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hub/factory")
  .withAutomaticReconnect()
  .build();

// Startup package: chart history + last snapshot (so the screen comes alive instantly).
connection.on("init", p => {
  if (p.snapshot) limitKw = p.snapshot.limitKw;
  for (const h of p.history ?? []) pushChartPoint(h.t, h.totalKw, h.isOverLimit);
  chart.update();
  if (p.snapshot) render(p.snapshot);
});

// Then a fresh snapshot arrives every second.
connection.on("snapshot", s => {
  render(s);
  pushChartPoint(s.time, s.totalKw, s.totalKw > s.limitKw);
  chart.update();
});

// ---- "Whole factory" buttons (send a command to every device) ----
function sendToAll(command, factor = 1.0) {
  for (const d of lastDevices) {
    if (d.online) connection.invoke("SendCommand", d.id, command, factor);
  }
}

document.getElementById("btn-start-all").onclick = () => sendToAll("START");
document.getElementById("btn-stop-all").onclick = () => sendToAll("STOP");

let halfSpeed = false;
document.getElementById("btn-speed").onclick = () => {
  halfSpeed = !halfSpeed;
  sendToAll("SET_SPEED", halfSpeed ? 0.5 : 1.0);
  document.getElementById("btn-speed").textContent =
    halfSpeed ? "⚡ Apply 100% speed" : "🐢 Apply 50% speed";
};

connection.start();
