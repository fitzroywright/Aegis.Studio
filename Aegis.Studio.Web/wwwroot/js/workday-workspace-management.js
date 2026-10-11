(() => {
  const byId = id => document.getElementById(id);
  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, ch => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;"
  })[ch]);

  const stateClass = state =>
    state === "Available" || state === "Online"
      ? "good"
      : state === "OverCapacity" || state === "Offline"
        ? "bad"
        : "warn";

  function workspaceIdFromPath() {
    const match = window.location.pathname.match(/^\/workspaces\/([0-9a-f-]+)\/workday-management$/i);
    if (!match) throw new Error("Workspace ID is missing from the page URL.");
    return match[1];
  }

  async function load() {
    const workspaceId = workspaceIdFromPath();

    const [activityResponse, performanceResponse] = await Promise.all([
      fetch("/api/workday/workspaces/" + workspaceId + "/activity", { credentials: "same-origin" }),
      fetch("/api/workspaces/" + workspaceId + "/performance", { credentials: "same-origin" })
    ]);

    if (!activityResponse.ok) {
      byId("summary").textContent = "Activity unavailable: HTTP " + activityResponse.status;
      return;
    }

    const data = await activityResponse.json();
    byId("summary").innerHTML =
      "Photographers <b>" + data.photographers.length + "</b> · Updated " +
      new Date(data.generatedAtUtc).toLocaleString();

    byId("people").innerHTML = data.photographers.map(person => {
      const client = person.client;
      const capacity = person.capacity;
      const assignments = person.assignments || [];
      const time = person.timeSummary;
      const utilization = capacity
        ? Math.min(100, Math.max(0, capacity.utilizationPercent))
        : 0;

      return '<article class="card"><h3>' + escapeHtml(person.displayName) + "</h3>" +
        '<div class="' + stateClass(client?.connectivity || "Unknown") + '">' +
        escapeHtml(client?.connectivity || "No recent client telemetry") + "</div>" +
        "<p>Sync: " + escapeHtml(client?.synchronizationState || "Unknown") +
        " · Pending " + (client?.pendingOperations ?? 0) +
        " · Uploads " + (client?.pendingUploads ?? 0) + "</p>" +
        "<p>Work session: " +
        (person.activeWorkSession
          ? "ACTIVE since " + new Date(person.activeWorkSession.startedAtUtc).toLocaleTimeString()
          : "Signed out") +
        "</p>" +
        (capacity
          ? '<p>Capacity: <b class="' + stateClass(capacity.state) + '">' +
            escapeHtml(capacity.state) + "</b> · " + capacity.utilizationPercent +
            '%</p><div class="bar"><span style="width:' + utilization +
            '%"></span></div><p class="muted">' +
            (Math.round(capacity.scheduledMinutes / 60 * 10) / 10) +
            "h scheduled · " + (Math.round(capacity.remainingMinutes / 60 * 10) / 10) +
            "h remaining</p>"
          : "") +
        (time
          ? "<p><b>Time (14d)</b> · Regular " + (time.regularMinutes / 60).toFixed(1) +
            "h · Overtime " + (time.overtimeMinutes / 60).toFixed(1) +
            "h · Weekend " + (time.weekendMinutes / 60).toFixed(1) + "h</p>"
          : "") +
        "<p><b>" + assignments.length + "</b> assigned job(s) in this workspace window</p>" +
        assignments.slice(0, 6).map(item =>
          '<div class="muted">' + new Date(item.startsAtUtc).toLocaleString() +
          " · " + escapeHtml(item.title) + " · " + escapeHtml(item.status) + "</div>"
        ).join("") +
        "</article>";
    }).join("");

    if (!performanceResponse.ok) {
      byId("performance").innerHTML =
        '<p class="muted">Performance metrics unavailable: HTTP ' +
        performanceResponse.status + "</p>";
      return;
    }

    const performance = await performanceResponse.json();

    const photographerCards = (performance.photographers || []).map(item =>
      '<article class="card"><h3>' + escapeHtml(item.displayName) + "</h3>" +
      "<p><b>" + item.completedShoots + "/" + item.assignedShoots +
      "</b> shoots completed · " + item.completionPercent + "%</p>" +
      "<p>On-time arrival " + item.onTimePercent + "% · Backup confirmed " +
      item.backupConfirmedPercent + "%</p>" +
      "<p>Reshoots " + item.reshootsRequired + " · " + item.reshootPercent +
      "% · Verified images " + item.imageCount + "</p>" +
      '<p class="muted">Avg field session ' +
      (item.averageSessionMinutes == null ? "—" : Math.round(item.averageSessionMinutes) + " min") +
      "</p></article>"
    ).join("");

    const editorCards = (performance.editors || []).map(item =>
      '<article class="card"><h3>' + escapeHtml(item.displayName) + " · Editor</h3>" +
      "<p><b>" + item.completedActivities + "/" + item.assignedActivities +
      "</b> completed · " + item.completionPercent + "%</p>" +
      "<p>On time " + item.onTimePercent + "% · Open " + item.openActivities +
      " · Overdue " + item.overdueActivities + "</p>" +
      "<p>Blocked " + item.blockedActivities + " · Avg cycle " +
      (item.averageCycleMinutes == null ? "—" : Math.round(item.averageCycleMinutes) + " min") +
      "</p>" +
      '<p class="muted">Avg start delay ' +
      (item.averageStartDelayMinutes == null ? "—" : Math.round(item.averageStartDelayMinutes) + " min") +
      "</p></article>"
    ).join("");

    const productionCards = (performance.production || []).map(item =>
      '<article class="card"><h3>' + escapeHtml(item.displayName) + " · Production</h3>" +
      "<p><b>" + item.completedActivities + "/" + item.assignedActivities +
      "</b> completed · " + item.completionPercent + "%</p>" +
      "<p>On time " + item.onTimePercent + "% · Open " + item.openActivities +
      " · Overdue " + item.overdueActivities + "</p>" +
      "<p>Blocked " + item.blockedActivities + " · Avg cycle " +
      (item.averageCycleMinutes == null ? "—" : Math.round(item.averageCycleMinutes) + " min") +
      "</p></article>"
    ).join("");

    byId("performance").innerHTML =
      '<h3>Photographers</h3><div class="grid">' +
      (photographerCards || '<p class="muted">No photographer metrics in this window.</p>') +
      '</div><h3>Editors</h3><div class="grid">' +
      (editorCards || '<p class="muted">No editor metrics in this window.</p>') +
      '</div><h3>Other Production</h3><div class="grid">' +
      (productionCards || '<p class="muted">No production metrics in this window.</p>') +
      "</div>";
  }

  load().catch(error => {
    byId("summary").textContent = "Workspace WorkDay unavailable: " + error.message;
  });

  setInterval(() => {
    load().catch(() => {});
  }, 30000);
})();

