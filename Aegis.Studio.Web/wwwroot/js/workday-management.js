(() => {
  let locationTrackingEnabled = false;

  const byId = id => document.getElementById(id);
  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, ch => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;"
  })[ch]);

  async function getJson(url) {
    const response = await fetch(url, { credentials: "same-origin" });
    if (!response.ok) throw new Error("HTTP " + response.status);
    return response.json();
  }

  async function loadConfig() {
    const config = await getJson("/api/workday/management/config");
    locationTrackingEnabled = Boolean(config.locationTrackingEnabled);
    byId("map").textContent = locationTrackingEnabled
      ? "Select a work session to view location evidence."
      : "GPS tracking is disabled by Studio policy.";
  }

  async function loadFleet() {
    try {
      const fleet = await getJson("/api/workday/fleet");
      byId("fleet").innerHTML =
        "Clients <b>" + fleet.clients + "</b> · Recently online <b>" + fleet.recentlyOnline +
        "</b> · Offline/stale <b>" + fleet.offlineOrStale + "</b><br>" +
        "Pending sync " + fleet.pendingSync + " · Pending uploads " + fleet.pendingUploads +
        " · Failed uploads " + fleet.failedUploads;
    } catch (error) {
      byId("fleet").textContent = "Fleet view unavailable: " + error.message;
    }
  }

  async function loadSessions() {
    try {
      const [rows, people] = await Promise.all([getJson("/api/workday/management/sessions?count=100"),getJson("/api/studio-employees?assignable=true").catch(() => [])]);
      studioRememberPeople(people);
      let html = "<table><thead><tr><th>User</th><th>Started</th><th>Ended</th><th>Source</th><th></th></tr></thead><tbody>";

      for (const row of rows) {
        html += "<tr><td>" + escapeHtml(studioFirstName(row.userName)) + "</td><td>" +
          new Date(row.startedAtUtc).toLocaleString() + "</td><td>" +
          (row.endedAtUtc ? new Date(row.endedAtUtc).toLocaleString() : "ACTIVE") +
          "</td><td>" + escapeHtml(row.source) + "</td><td>" +
          '<button class="view" data-id="' + row.id + '" data-user="' +
          encodeURIComponent(row.userName) + '">View</button> ' +
          '<button class="correct" data-id="' + row.id + '">Correct</button></td></tr>';
      }

      html += "</tbody></table>";
      byId("sessions").innerHTML = html;

      document.querySelectorAll("#sessions button.view").forEach(button => {
        button.addEventListener("click", () => showTimeline(button.dataset.id, button.dataset.user));
      });

      document.querySelectorAll("#sessions button.correct").forEach(button => {
        button.addEventListener("click", () => correctSession(button.dataset.id));
      });
    } catch (error) {
      byId("sessions").textContent = "Sessions unavailable: " + error.message;
    }
  }

  async function correctSession(id) {
    const start = prompt("Corrected sign-in time (ISO 8601), or leave blank to keep current:", "");
    if (start === null) return;

    const end = prompt("Corrected sign-out time (ISO 8601), or leave blank to keep current:", "");
    if (end === null) return;

    const reason = prompt("Correction reason (required):", "");
    if (!reason?.trim()) return;

    const payload = {
      correctedStartedAtUtc: start.trim() ? new Date(start).toISOString() : null,
      correctedEndedAtUtc: end.trim() ? new Date(end).toISOString() : null,
      reason: reason.trim()
    };

    try {
      const response = await fetch("/api/workday/management/sessions/" + id + "/corrections", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        credentials: "same-origin",
        body: JSON.stringify(payload)
      });

      if (!response.ok) throw new Error("HTTP " + response.status);
      await loadSessions();
    } catch (error) {
      alert("Correction was not applied: " + error.message);
    }
  }

  function renderTrail(points) {
    if (!locationTrackingEnabled) {
      byId("map").textContent = "GPS tracking is disabled by Studio policy.";
      return;
    }

    if (!points.length) {
      byId("map").textContent = "No location evidence available.";
      return;
    }

    const minLat = Math.min(...points.map(x => x.latitude));
    const maxLat = Math.max(...points.map(x => x.latitude));
    const minLon = Math.min(...points.map(x => x.longitude));
    const maxLon = Math.max(...points.map(x => x.longitude));

    const width = 640;
    const height = 260;
    const padding = 24;
    const latSpan = Math.max(maxLat - minLat, 0.00001);
    const lonSpan = Math.max(maxLon - minLon, 0.00001);

    const plotted = points.map(point => ({
      x: padding + ((point.longitude - minLon) / lonSpan) * (width - padding * 2),
      y: height - padding - ((point.latitude - minLat) / latSpan) * (height - padding * 2),
      value: point
    }));

    const polyline = plotted.map(point => point.x.toFixed(1) + "," + point.y.toFixed(1)).join(" ");
    const circles = plotted.map(point =>
      '<circle cx="' + point.x.toFixed(1) + '" cy="' + point.y.toFixed(1) + '" r="5"><title>' +
      escapeHtml(new Date(point.value.observedAtUtc).toLocaleString()) + "</title></circle>"
    ).join("");

    const links = points.map((point, index) =>
      '<a target="_blank" rel="noopener" href="https://www.openstreetmap.org/?mlat=' +
      point.latitude + "&mlon=" + point.longitude + "#map=17/" + point.latitude + "/" +
      point.longitude + '">Observation ' + (index + 1) + "</a>"
    ).join(" · ");

    byId("map").innerHTML =
      '<div class="trail"><b>Field presence trail</b><br>' +
      '<span class="muted">' + points.length +
      " observation(s), chronological order. Device evidence only; not proof of employee conduct.</span>" +
      '<svg viewBox="0 0 ' + width + " " + height + '"><polyline points="' +
      polyline + '"></polyline>' + circles + "</svg>" +
      '<div class="trail-links">' + links + "</div></div>";
  }

  async function showTimeline(id, user) {
    try {
      const timeline = await getJson(
        "/api/workday/work-sessions/" + id + "/timeline?userName=" + user
      );

      let html = "<b>" + escapeHtml(studioFirstName(timeline.userName)) + "</b><br>";
      if (timeline.locationEvidence.length) {
        for (const item of timeline.locationEvidence) {
          html += new Date(item.observedAtUtc).toLocaleString() + " · accuracy " +
            (item.accuracyMetres == null ? "unknown" : item.accuracyMetres) + "m · " +
            escapeHtml(item.source || "Device") + "<br>";
        }
      } else {
        html += locationTrackingEnabled
          ? "No location evidence recorded."
          : "GPS tracking is disabled by Studio policy.";
      }

      byId("timeline").innerHTML = html;
      renderTrail(timeline.locationEvidence);
    } catch (error) {
      byId("timeline").textContent = "Timeline unavailable: " + error.message;
    }
  }

  async function load() {
    try {
      await loadConfig();
    } catch (error) {
      locationTrackingEnabled = false;
      byId("map").textContent = "Location configuration unavailable: " + error.message;
    }

    await Promise.all([loadFleet(), loadSessions()]);
  }

  load();
})();
