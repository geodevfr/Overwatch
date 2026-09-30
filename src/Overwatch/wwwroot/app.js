const state = { logSince: 0, running: false, armed: false };

const $ = (id) => document.getElementById(id);

function bytes(value) {
  const n = Number(value) || 0;
  if (n < 1024) return `${n} o`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} Ko`;
  return `${(n / 1024 / 1024).toFixed(1)} Mo`;
}

async function api(path, options) {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...options
  });
  const text = await response.text();
  const body = text ? JSON.parse(text) : {};
  if (!response.ok) {
    const message = body.error || (body.errors || []).join("\n") || response.statusText;
    throw new Error(message);
  }
  return body;
}

function showError(message) {
  const node = $("form-error");
  node.hidden = !message;
  node.textContent = message || "";
}

function listenerRow(listener, index) {
  const row = document.createElement("div");
  row.className = "listener";
  row.innerHTML = `
    <label class="field"><span>Nom</span><input data-k="name" type="text" value="" /></label>
    <label class="field narrow"><span>Port local</span><input data-k="listenPort" type="number" min="1" max="65535" /></label>
    <label class="field"><span>IP du serveur</span><input data-k="upstreamHost" type="text" spellcheck="false" /></label>
    <label class="field narrow"><span>Port distant</span><input data-k="upstreamPort" type="number" min="1" max="65535" /></label>`;
  row.dataset.index = String(index);
  for (const input of row.querySelectorAll("input"))
    input.value = listener[input.dataset.k] ?? "";
  return row;
}

function hostRow(host) {
  const row = document.createElement("div");
  row.className = "host-row";
  row.innerHTML = `
    <label class="field"><span>Nom demandé par le jeu</span><input data-k="hostname" type="text" spellcheck="false" /></label>
    <label class="field narrow"><span>Adresse</span><input data-k="address" type="text" /></label>
    <button type="button" class="ghost remove-host">Retirer</button>`;
  for (const input of row.querySelectorAll("input"))
    input.value = host[input.dataset.k] ?? "";
  row.querySelector(".remove-host").addEventListener("click", () => row.remove());
  return row;
}

function readForm() {
  const listeners = [...$("listeners").querySelectorAll(".listener")].map((row) => {
    const value = (key) => row.querySelector(`[data-k="${key}"]`).value.trim();
    return {
      name: value("name"),
      listenPort: Number(value("listenPort")),
      upstreamHost: value("upstreamHost"),
      upstreamPort: Number(value("upstreamPort"))
    };
  });
  const hosts = [...$("hosts").querySelectorAll(".host-row")].map((row) => ({
    hostname: row.querySelector('[data-k="hostname"]').value.trim(),
    address: row.querySelector('[data-k="address"]').value.trim() || "127.0.0.1"
  }));
  return {
    acceptTerms: $("accept-terms").checked,
    listeners,
    hostsEnabled: $("hosts-enabled").checked,
    hosts,
    windowsEnabled: $("windows-enabled").checked
  };
}

function fillForm(form) {
  $("accept-terms").checked = !!form.acceptTerms;
  $("hosts-enabled").checked = !!form.hostsEnabled;
  $("windows-enabled").checked = !!form.windowsEnabled;
  const listeners = $("listeners");
  listeners.replaceChildren();
  (form.listeners || []).forEach((listener, index) => listeners.append(listenerRow(listener, index)));
  const hosts = $("hosts");
  hosts.replaceChildren();
  (form.hosts || []).forEach((host) => hosts.append(hostRow(host)));
}

function useRemote(address, port) {
  const rows = [...$("listeners").querySelectorAll(".listener")];
  let matched = false;
  for (const row of rows) {
    const local = Number(row.querySelector('[data-k="listenPort"]').value);
    if (local === port) {
      row.querySelector('[data-k="upstreamHost"]').value = address;
      row.querySelector('[data-k="upstreamPort"]').value = String(port);
      matched = true;
    }
  }
  if (!matched) {
    for (const row of rows)
      row.querySelector('[data-k="upstreamHost"]').value = address;
    showError(`Aucune ligne n'écoute sur le port ${port}. L'adresse a été recopiée, le port distant est inchangé.`);
    return;
  }
  showError("");
}

async function loadConfig() {
  const body = await api("/api/config");
  const form = body.form || {};
  if (!(form.listeners || []).length) {
    form.listeners = [
      { name: "jeu-principal", listenPort: 5555, upstreamHost: "", upstreamPort: 5555 },
      { name: "jeu-tls", listenPort: 443, upstreamHost: "", upstreamPort: 443 }
    ];
  }
  fillForm(form);
  if (body.errors?.length)
    showError(body.errors.join("\n"));
}

async function refreshStatus() {
  const status = await api("/api/status");
  state.running = !!status.running;
  state.armed = !!status.captureArmed;
  const pill = $("status-pill");
  pill.textContent = status.running ? "En observation" : "Arrêté";
  pill.className = "status" + (status.running ? " on" : status.error ? " bad" : "");
  $("m-c2s").textContent = bytes(status.bytesClientToServer);
  $("m-s2c").textContent = bytes(status.bytesServerToClient);
  $("m-sessions").textContent = String(status.sessions ?? 0);
  $("m-messages").textContent = String(status.messages ?? 0);
  const listeners = status.listeners || [];
  $("listen-line").textContent = listeners.length
    ? "Écoute sur " + listeners.map((item) => `${item.name} (${item.port})`).join(", ") + "."
    : "Rien n'écoute encore.";
  $("start").disabled = status.running;
  $("stop").disabled = !status.running;
  $("arm").textContent = status.captureArmed ? "Arrêter la capture" : "Armer la capture";
  $("arm").className = status.captureArmed ? "ghost" : "primary";
  const banner = $("hosts-banner");
  banner.hidden = !status.hostsWarning;
  banner.textContent = status.hostsWarning || "";
}

async function refreshLog() {
  const lines = await api(`/api/log?since=${state.logSince}`);
  const list = $("log");
  for (const line of lines) {
    state.logSince = line.id;
    const item = document.createElement("li");
    item.className = line.level;
    item.textContent = `${line.at}  ${line.message}`;
    list.append(item);
  }
  while (list.children.length > 80)
    list.removeChild(list.firstChild);
  if (lines.length)
    list.scrollTop = list.scrollHeight;
}

async function refreshConnections() {
  const rows = await api("/api/connections");
  const list = $("connections");
  list.replaceChildren();
  if (!rows.length) {
    const item = document.createElement("li");
    item.textContent = "Aucune connexion distante établie pour le moment. Lancez le jeu, puis actualisez.";
    list.append(item);
    return;
  }
  for (const row of rows) {
    const item = document.createElement("li");
    const label = document.createElement("span");
    label.textContent = `${row.address} : ${row.port}`;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "ghost";
    button.textContent = "Utiliser cette adresse";
    button.addEventListener("click", () => useRemote(row.address, row.port));
    item.append(label, button);
    list.append(item);
  }
}

function renderCapture(direction) {
  const bits = [`${direction.direction}`, bytes(direction.bytes), `${direction.segments} lectures`];
  if (direction.largestSegment)
    bits.push(`plus grande lecture ${bytes(direction.largestSegment)}`);
  if (direction.prefixHex)
    bits.push(direction.prefixHex);
  return bits.join(" · ");
}

async function refreshCaptures() {
  const sessions = await api("/api/captures");
  const root = $("captures");
  root.replaceChildren();
  if (!sessions.length) {
    const empty = document.createElement("p");
    empty.className = "hint";
    empty.textContent = "Aucune capture. Démarrez l'observation, armez la capture, puis jouez.";
    root.append(empty);
    return;
  }
  for (const session of sessions) {
    const block = document.createElement("article");
    block.className = "capture";
    const text = document.createElement("div");
    const title = document.createElement("strong");
    title.textContent = session.connectionId + (session.listener ? ` · ${session.listener}` : "");
    text.append(title);
    for (const direction of session.directions) {
      const line = document.createElement("div");
      const small = document.createElement("small");
      small.textContent = renderCapture(direction);
      line.append(small);
      if (direction.looksLikeTls) {
        const tag = document.createElement("span");
        tag.className = "tag tls";
        tag.textContent = "ressemble à TLS, illisible";
        line.append(" ", tag);
      }
      text.append(line);
    }
    const links = document.createElement("div");
    for (const direction of session.directions) {
      if (!direction.bytes) continue;
      const link = document.createElement("a");
      link.href = `/api/captures/${session.connectionId}/${direction.direction}`;
      link.textContent = `Télécharger ${direction.direction}`;
      link.style.marginLeft = "8px";
      links.append(link);
    }
    block.append(text, links);
    root.append(block);
  }
}

async function refreshRules() {
  const body = await api("/api/rules");
  const root = $("versions");
  root.replaceChildren();
  if (body.error) {
    const p = document.createElement("p");
    p.className = "error";
    p.textContent = body.error;
    root.append(p);
    return;
  }
  for (const version of body.versions || []) {
    const row = document.createElement("div");
    row.className = "version";
    const label = document.createElement("div");
    const title = document.createElement("strong");
    title.textContent = version.version || "";
    const small = document.createElement("small");
    small.textContent = (version.pending || []).join(", ") || "rien en attente";
    label.append(title, document.createElement("br"), small);
    const tag = document.createElement("span");
    tag.className = "tag " + (version.status === "captured" ? "captured" : "unknown");
    tag.textContent = version.status === "captured" ? "capturée" : "inconnue";
    row.append(label, tag);
    root.append(row);
  }
  if (!(body.versions || []).length) {
    const p = document.createElement("p");
    p.className = "hint";
    p.textContent = "La table de détection ne liste aucune version.";
    root.append(p);
  }
}

function setTag(id, ready) {
  const tag = $(id);
  tag.className = "tag " + (ready ? "captured" : "unknown");
  tag.textContent = ready ? "signature active" : "signature inconnue";
}

function emptyNote(parent, text) {
  const note = document.createElement("p");
  note.className = "hint";
  note.textContent = text;
  parent.append(note);
}

function fillTable(parent, headers, rows) {
  const table = document.createElement("table");
  const head = document.createElement("thead");
  const headRow = document.createElement("tr");
  for (const header of headers) {
    const cell = document.createElement("th");
    cell.textContent = header;
    headRow.append(cell);
  }
  head.append(headRow);
  const body = document.createElement("tbody");
  for (const row of rows) {
    const line = document.createElement("tr");
    for (const value of row) {
      const cell = document.createElement("td");
      cell.textContent = value ?? "";
      line.append(cell);
    }
    body.append(line);
  }
  table.append(head, body);
  parent.append(table);
}

async function refreshBoard() {
  const body = await api("/api/board");
  const detection = body.detection || {};
  setTag("tag-hotel", detection.hotel === "ready");
  setTag("tag-map", detection.map === "ready");
  setTag("tag-monsters", detection.monsters === "ready");

  const hotel = $("board-hotel");
  hotel.replaceChildren();
  const prices = body.prices || [];
  if (!prices.length) {
    emptyNote(hotel, detection.hotel === "ready"
      ? "Signature active. Aucun prix vu pour l'instant."
      : "Aucun prix. Les moyennes et les lots ne sont pas encore signés.");
  } else {
    fillTable(hotel, ["Serveur", "Objet", "Source", "Lot", "Total", "Unitaire"], prices.map((price) => [
      price.server,
      String(price.itemId ?? ""),
      price.source,
      String(price.quantity ?? ""),
      price.total ?? "",
      price.unit ?? ""
    ]));
  }

  const map = $("board-map");
  map.replaceChildren();
  const places = body.places || [];
  if (!places.length) {
    emptyNote(map, detection.map === "ready"
      ? "Signature active. Aucune carte lue pour l'instant."
      : "Aucune carte. La position n'est pas encore signée.");
  } else {
    fillTable(map, ["Personnage", "Serveur", "Position"], places.map((place) => [
      place.character || place.connectionId,
      place.server || "",
      place.position || "inconnue"
    ]));
  }

  const monsters = $("board-monsters");
  monsters.replaceChildren();
  const encounters = body.encounters || [];
  if (!encounters.length) {
    emptyNote(monsters, detection.monsters === "ready"
      ? "Signature active. Aucun combat lu pour l'instant."
      : "Aucun monstre. Le combat n'est pas encore signé.");
  } else {
    fillTable(monsters, ["Personnage", "Combat"], encounters.map((encounter) => [
      encounter.character || encounter.connectionId,
      encounter.combat
    ]));
  }
}

async function refreshHosts() {
  const body = await api("/api/hosts");
  const node = $("hosts-state");
  if (body.error)
    node.textContent = body.error;
  else if (body.managedBlockPresent)
    node.textContent = `Un bloc Overwatch est présent dans ${body.path}.`;
  else
    node.textContent = `Fichier visé : ${body.path}. Aucun bloc Overwatch.`;
  if (body.journalPath)
    node.textContent += ` Journal : ${body.journalPath}.`;
}

$("config-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  showError("");
  try {
    await api("/api/config", { method: "PUT", body: JSON.stringify(readForm()) });
    await refreshLog();
  } catch (error) {
    showError(error.message);
  }
});

$("add-host").addEventListener("click", () => {
  $("hosts").append(hostRow({ hostname: "", address: "127.0.0.1" }));
});

$("start").addEventListener("click", async () => {
  showError("");
  try {
    await api("/api/config", { method: "PUT", body: JSON.stringify(readForm()) });
    await api("/api/relay/start", { method: "POST", body: "{}" });
    await refreshStatus();
    await refreshLog();
    await refreshHosts();
  } catch (error) {
    showError(error.message);
  }
});

$("stop").addEventListener("click", async () => {
  await api("/api/relay/stop", { method: "POST", body: "{}" });
  await refreshStatus();
  await refreshLog();
  await refreshHosts();
  await refreshCaptures();
});

$("arm").addEventListener("click", async () => {
  await api("/api/capture", { method: "POST", body: JSON.stringify({ armed: !state.armed }) });
  await refreshStatus();
  await refreshLog();
});

$("remove-hosts").addEventListener("click", async () => {
  showError("");
  try {
    await api("/api/hosts/remove", { method: "POST", body: "{}" });
    await refreshHosts();
    await refreshLog();
  } catch (error) {
    showError(error.message);
  }
});

$("cleanup-task").addEventListener("click", async () => {
  showError("");
  try {
    const body = await api("/api/hosts/cleanup-task", { method: "POST", body: "{}" });
    $("hosts-state").textContent = body.detail || "Tâche enregistrée.";
    await refreshLog();
  } catch (error) {
    showError(error.message);
  }
});

$("refresh-connections").addEventListener("click", () => refreshConnections().catch((error) => showError(error.message)));

async function tick() {
  try {
    await refreshStatus();
    await refreshLog();
    if (state.armed || state.running)
      await refreshCaptures();
    await refreshBoard();
  } catch (error) {
    $("status-pill").textContent = "Page déconnectée";
    $("status-pill").className = "status bad";
    showError(error.message);
  }
}

loadConfig()
  .then(() => Promise.all([refreshStatus(), refreshConnections(), refreshCaptures(), refreshRules(), refreshBoard(), refreshHosts(), refreshLog()]))
  .catch((error) => showError(error.message));

setInterval(tick, 1500);
