// ERS field companion (Phase 21): a crew member's phone speaks for their unit. Plain JavaScript, no build step.
// Reports made with no signal are kept on the phone and sent when it reconnects.
"use strict";

const API = "/api/v1/field";
const STORE = "ers-field";

const TEXT = {
  en: {
    skip: "Skip to the main content", title: "Field companion", language: "Language", night: "Night",
    joinHeading: "Join the exercise", code: "Session code", name: "Your name", callsign: "Your unit's callsign", joinButton: "Join",
    parHeading: "PAR: account for your crew", accounted: "Crew accounted for", missing: "Missing (names, comma between)", sendPar: "Send PAR",
    ordersHeading: "Orders", noOrders: "No orders waiting.", acknowledge: "Acknowledge", acknowledged: "Acknowledged",
    statusHeading: "Status", EnRoute: "En route", OnScene: "On scene", Operating: "Working", Transporting: "Transporting", Available: "Available",
    OutOfService: "Out of service", Dispatched: "Dispatched", Cancelled: "Stood down",
    checkinHeading: "Check in at the incident (ICS 211)", crew: "Crew with you", checkIn: "Check in",
    reportHeading: "Field report", reportLabel: "What you see", withLocation: "Include my location", sendReport: "Send report",
    sharePosition: "Share my position every 30 seconds", privacy: "Your location is sent only when you choose, and only to this exercise.",
    leave: "Leave", notAssigned: "Not assigned to an incident.", queued: "{0} report(s) waiting for a signal.", offline: "No signal: kept on the phone, will send when back.",
    paused: "The exercise is paused or hasn't started.", noLocation: "Location unavailable: sent without it.",
  },
  ga: {
    skip: "Léim go dtí an príomhábhar", title: "Compánach páirce", language: "Teanga", night: "Oíche",
    joinHeading: "Glac páirt sa chleachtadh", code: "Cód an tseisiúin", name: "D'ainm", callsign: "Glaoshíneadh d'aonaid", joinButton: "Glac páirt",
    parHeading: "PAR: cuntas ar do chriú", accounted: "Criú a bhfuil cuntas orthu", missing: "Ar iarraidh (ainmneacha, camóg eatarthu)", sendPar: "Seol PAR",
    ordersHeading: "Orduithe", noOrders: "Níl aon ordú ag fanacht.", acknowledge: "Admhaigh", acknowledged: "Admhaithe",
    statusHeading: "Stádas", EnRoute: "Ar an mbealach", OnScene: "Ar an láthair", Operating: "Ag obair", Transporting: "Ag iompar", Available: "Ar fáil",
    OutOfService: "As seirbhís", Dispatched: "Seolta", Cancelled: "Scaoilte",
    checkinHeading: "Cláraigh ag an teagmhas (ICS 211)", crew: "Criú leat", checkIn: "Cláraigh",
    reportHeading: "Tuairisc pháirce", reportLabel: "An méid a fheiceann tú", withLocation: "Cuir mo shuíomh leis", sendReport: "Seol an tuairisc",
    sharePosition: "Roinn mo shuíomh gach 30 soicind", privacy: "Ní sheoltar do shuíomh ach nuair a roghnaíonn tú, agus chuig an gcleachtadh seo amháin.",
    leave: "Fág", notAssigned: "Gan sannadh do theagmhas.", queued: "{0} tuairisc ag fanacht le comhartha.", offline: "Gan chomhartha: coinnithe ar an bhfón, seolfar ar ball.",
    paused: "Tá an cleachtadh ar sos nó níor thosaigh sé fós.", noLocation: "Níl suíomh ar fáil: seolta gan é.",
  },
  pl: {
    skip: "Przejdź do treści", title: "Aplikacja terenowa", language: "Język", night: "Noc",
    joinHeading: "Dołącz do ćwiczenia", code: "Kod sesji", name: "Imię i nazwisko", callsign: "Kryptonim jednostki", joinButton: "Dołącz",
    parHeading: "PAR: policz swoją załogę", accounted: "Obecni członkowie załogi", missing: "Brakujący (nazwiska po przecinku)", sendPar: "Wyślij PAR",
    ordersHeading: "Polecenia", noOrders: "Brak oczekujących poleceń.", acknowledge: "Potwierdź", acknowledged: "Potwierdzone",
    statusHeading: "Status", EnRoute: "W drodze", OnScene: "Na miejscu", Operating: "W działaniu", Transporting: "Transport", Available: "Dostępny",
    OutOfService: "Poza służbą", Dispatched: "Zadysponowany", Cancelled: "Odwołany",
    checkinHeading: "Zgłoszenie na miejscu zdarzenia (ICS 211)", crew: "Liczba osób w załodze", checkIn: "Zgłoś przybycie",
    reportHeading: "Meldunek z terenu", reportLabel: "Co widzisz", withLocation: "Dołącz moją lokalizację", sendReport: "Wyślij meldunek",
    sharePosition: "Udostępniaj moją pozycję co 30 sekund", privacy: "Lokalizacja jest wysyłana tylko na Twoje życzenie i tylko do tego ćwiczenia.",
    leave: "Wyjdź", notAssigned: "Brak przydziału do zdarzenia.", queued: "Meldunki czekające na zasięg: {0}.", offline: "Brak zasięgu: zapisano w telefonie, zostanie wysłane później.",
    paused: "Ćwiczenie jest wstrzymane lub jeszcze się nie rozpoczęło.", noLocation: "Lokalizacja niedostępna: wysłano bez niej.",
  },
};

const state = JSON.parse(localStorage.getItem(STORE) || "{}");
state.queue = state.queue || [];
state.language = state.language || (navigator.language || "en").slice(0, 2);
if (!TEXT[state.language]) state.language = "en";
let positionTimer = null;
let refreshTimer = null;

const $ = id => document.getElementById(id);
const t = key => (TEXT[state.language] || TEXT.en)[key] || TEXT.en[key] || key;
const save = () => localStorage.setItem(STORE, JSON.stringify(state));

function say(message, kind) {
  const status = $("status");
  status.textContent = message || "";
  status.className = kind || "";
}

function translate() {
  document.documentElement.lang = state.language;
  document.querySelectorAll("[data-t]").forEach(el => { el.textContent = t(el.dataset.t); });
  $("language").value = state.language;
  showQueue();
}

function applyTheme() {
  if (state.night) document.documentElement.dataset.theme = "night";
  else delete document.documentElement.dataset.theme;
  $("night").setAttribute("aria-pressed", state.night ? "true" : "false");
  document.querySelector('meta[name="theme-color"]').content = state.night ? "#000000" : "#0F1419";
}

async function call(path, body, method) {
  const response = await fetch(API + path, {
    method: method || (body === undefined ? "GET" : "POST"),
    headers: Object.assign({ "Content-Type": "application/json" }, state.token ? { "X-Field-Token": state.token } : {}),
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const data = await response.json().catch(() => ({}));
  if (response.status === 401 && state.token) { leave(); }
  if (!response.ok) throw Object.assign(new Error(data.message || data.error || response.statusText), { status: response.status });
  return data;
}

function where() {
  return new Promise(resolve => {
    if (!navigator.geolocation) return resolve(null);
    navigator.geolocation.getCurrentPosition(
      p => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude, accuracy: p.coords.accuracy, speed: p.coords.speed, heading: p.coords.heading }),
      () => resolve(null), { enableHighAccuracy: true, timeout: 8000, maximumAge: 15000 });
  });
}

// ---- Joining ----

$("join-form").addEventListener("submit", async e => {
  e.preventDefault();
  try {
    const joined = await call("/join", { code: $("code").value, name: $("name").value, callsign: $("callsign").value });
    Object.assign(state, { token: joined.token, callsign: joined.callsign, title: joined.title });
    save();
    say(joined.title, "ok");
    show();
  } catch (err) { say(err.message, "error"); }
});

function leave() {
  delete state.token;
  state.queue = [];
  save();
  stopSharing();
  show();
}
$("leave").addEventListener("click", leave);

// ---- The unit ----

async function refresh() {
  if (!state.token) return;
  try {
    const me = await call("/me");
    $("callsign-label").textContent = me.callsign;
    $("unit-status").textContent = t(me.status);
    $("incident").textContent = me.incident ? `${me.incident}${me.address ? " · " + me.address : ""}` : t("notAssigned");
    $("tasking").textContent = me.tasking || "";
    document.querySelectorAll("#statuses button").forEach(b => {
      const current = b.dataset.status === me.status;
      b.classList.toggle("current", current);
      b.setAttribute("aria-pressed", current ? "true" : "false");
    });
    const orders = $("orders");
    orders.replaceChildren();
    if (!me.orders.length) { const li = document.createElement("li"); li.textContent = t("noOrders"); orders.append(li); }
    for (const order of me.orders) {
      const li = document.createElement("li");
      const text = document.createElement("p");
      text.textContent = order.text;
      li.append(text);
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = order.acknowledged ? t("acknowledged") : t("acknowledge");
      button.disabled = order.acknowledged;
      button.addEventListener("click", () => act(`/orders/${order.id}/acknowledge`, {}));
      li.append(button);
      orders.append(li);
    }
    $("par").hidden = !me.par;
    if (me.par) {
      $("par-reason").textContent = me.par.reason;
      if (!$("accounted").value) $("accounted").value = me.par.expected;
    }
    if (!me.running) say(t("paused"));
    await flush();
  } catch (err) {
    if (!navigator.onLine || err instanceof TypeError) say(t("offline"), "error");
  }
}

async function act(path, body) {
  try {
    const result = await call(path, body);
    say(result.message, result.ok ? "ok" : "error");
  } catch (err) {
    say(err.message, "error");
  }
  await refresh();
}

document.querySelectorAll("#statuses button").forEach(b => b.addEventListener("click", () => act("/status", { status: b.dataset.status })));

$("checkin-form").addEventListener("submit", async e => {
  e.preventDefault();
  const at = await where();
  act("/checkin", { crew: Number($("crew").value), latitude: at && at.latitude, longitude: at && at.longitude });
});

$("par-form").addEventListener("submit", e => {
  e.preventDefault();
  const missing = $("missing").value.split(",").map(s => s.trim()).filter(Boolean);
  act("/par", { accounted: Number($("accounted").value), missing });
});

// ---- Reports, kept on the phone when there's no signal ----

$("report-form").addEventListener("submit", async e => {
  e.preventDefault();
  const report = { text: $("report").value };
  if ($("with-location").checked) {
    const at = await where();
    if (at) Object.assign(report, { latitude: at.latitude, longitude: at.longitude, accuracy: at.accuracy });
    else say(t("noLocation"));
  }
  state.queue.push(report);
  save();
  $("report").value = "";
  await flush();
});

async function flush() {
  while (state.queue.length) {
    try {
      const result = await call("/report", state.queue[0]);
      state.queue.shift();
      save();
      say(result.message, "ok");
    } catch (err) {
      if (err.status && err.status < 500) { state.queue.shift(); save(); say(err.message, "error"); continue; } // refused: don't retry
      say(t("offline"), "error");
      break;
    }
  }
  showQueue();
}

function showQueue() {
  const queue = $("queue");
  queue.hidden = !state.queue.length;
  queue.textContent = t("queued").replace("{0}", state.queue.length);
}

// ---- Position sharing (opt-in) ----

function stopSharing() {
  clearInterval(positionTimer);
  positionTimer = null;
  $("share-position").checked = false;
}

$("share-position").addEventListener("change", e => {
  if (!e.target.checked) return stopSharing();
  const send = async () => {
    const at = await where();
    if (at) call("/position", { latitude: at.latitude, longitude: at.longitude, speedKph: (at.speed || 0) * 3.6, heading: at.heading || 0 }).catch(() => {});
  };
  send();
  positionTimer = setInterval(send, 30000);
});

// ---- Settings ----

$("language").addEventListener("change", e => { state.language = e.target.value; save(); translate(); refresh(); });
$("night").addEventListener("click", () => { state.night = !state.night; save(); applyTheme(); });
window.addEventListener("online", flush);

function show() {
  const joined = Boolean(state.token);
  $("join").hidden = joined;
  $("unit").hidden = !joined;
  clearInterval(refreshTimer);
  if (joined) { refresh(); refreshTimer = setInterval(refresh, 10000); }
}

if ("serviceWorker" in navigator) navigator.serviceWorker.register("sw.js").catch(() => {});
translate();
applyTheme();
show();
