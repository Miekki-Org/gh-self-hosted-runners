namespace RunnerControlPlane.Access;

public static class RunnerAccessPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>GitHub Runner Access</title>
          <style>
            body { font: 16px system-ui, sans-serif; margin: 2rem auto; max-width: 48rem; padding: 0 1rem; }
            textarea { box-sizing: border-box; display: block; min-height: 9rem; width: 100%; }
            button { margin: 1rem 0; padding: .5rem 1rem; }
            #message { min-height: 1.5rem; }
            .warning { color: #9a3412; }
            .capacity-grid { display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; }
            .capacity-grid dt { font-weight: 600; }
            .capacity-grid dd { margin: 0; }
            .capacity-form { margin-top: 1rem; }
            .capacity-form label { margin-right: .5rem; }
          </style>
        </head>
        <body>
          <header>
            <h1>GitHub Runner Access</h1>
            <form action="/logout" method="post"><button type="submit">Sign out</button></form>
          </header>
          <main>
            <p id="message" role="status">Loading runner access…</p>
            <dl>
              <dt>Organization</dt><dd id="organization"></dd>
              <dt>Runner group</dt><dd id="group"></dd>
              <dt>Stable runner label</dt><dd id="label"></dd>
              <dt>Access mode</dt><dd id="access-mode"></dd>
            </dl>
            <h2>Allowed repositories</h2>
            <ul id="repository-list"></ul>
            <form id="allowlist-form" hidden>
              <label for="repositories">One organization/repository per line</label>
              <textarea id="repositories" spellcheck="false"></textarea>
              <button type="submit">Save allowlist</button>
            </form>
            <section aria-labelledby="capacity-heading">
              <h2 id="capacity-heading">Runner capacity</h2>
              <p id="capacity-message" role="status">Loading runner capacity…</p>
              <dl class="capacity-grid">
                <dt>Minimum</dt><dd id="capacity-minimum"></dd>
                <dt>Maximum</dt><dd id="capacity-maximum"></dd>
                <dt>Current VMs</dt><dd id="capacity-current"></dd>
                <dt>Busy runners</dt><dd id="capacity-busy"></dd>
                <dt>Idle capacity</dt><dd id="capacity-idle"></dd>
                <dt>Queued eligible jobs</dt><dd id="capacity-queued"></dd>
                <dt>Unserved queued jobs</dt><dd id="capacity-unserved"></dd>
              </dl>
              <form id="capacity-settings-form" class="capacity-form" hidden>
                <h3>Configure capacity</h3>
                <label for="capacity-minimum-input">Minimum</label>
                <input id="capacity-minimum-input" type="number" min="0" required>
                <label for="capacity-maximum-input">Maximum</label>
                <input id="capacity-maximum-input" type="number" min="1" required>
                <button type="submit">Save capacity</button>
              </form>
              <form id="capacity-scale-out-form" class="capacity-form" hidden>
                <h3>Request one-off scale-out</h3>
                <label for="capacity-scale-out-count">Additional VMs</label>
                <input id="capacity-scale-out-count" type="number" min="1" value="1" required>
                <button type="submit">Request scale-out</button>
              </form>
            </section>
            <section aria-labelledby="audit-heading">
              <h2 id="audit-heading">Operational audit trail</h2>
              <p id="audit-message" role="status">Loading audit history…</p>
              <div id="audit-trail"></div>
            </section>
          </main>
          <script>
            const message = document.getElementById("message");
            const form = document.getElementById("allowlist-form");
            const repositories = document.getElementById("repositories");
            const capacityMessage = document.getElementById("capacity-message");
            const capacitySettingsForm = document.getElementById("capacity-settings-form");
            const capacityScaleOutForm = document.getElementById("capacity-scale-out-form");
            const capacityMinimumInput = document.getElementById("capacity-minimum-input");
            const capacityMaximumInput = document.getElementById("capacity-maximum-input");
            const capacityScaleOutCount = document.getElementById("capacity-scale-out-count");
            const auditTrail = document.getElementById("audit-trail");
            const auditMessage = document.getElementById("audit-message");

            function render(configuration) {
              document.getElementById("organization").textContent = configuration.organization;
              document.getElementById("group").textContent = configuration.runnerGroup;
              document.getElementById("label").textContent = configuration.runnerLabel;
              document.getElementById("access-mode").textContent = configuration.isRestricted
                ? "Restricted to the selected repositories"
                : "Not restricted to the selected repositories";
              document.getElementById("access-mode").className = configuration.isRestricted ? "" : "warning";
              const list = document.getElementById("repository-list");
              list.replaceChildren(...configuration.repositories.map(name => {
                const item = document.createElement("li");
                item.textContent = name;
                return item;
              }));
              form.hidden = !configuration.canManage;
              capacitySettingsForm.hidden = !configuration.canManage;
              capacityScaleOutForm.hidden = !configuration.canManage;
              repositories.value = configuration.repositories.join("\n");
              message.textContent = "";
            }

            function renderCapacity(capacity) {
              document.getElementById("capacity-minimum").textContent = capacity.minimumCapacity;
              document.getElementById("capacity-maximum").textContent = capacity.maximumCapacity;
              document.getElementById("capacity-current").textContent = capacity.currentCapacity;
              document.getElementById("capacity-busy").textContent = capacity.busyRunners;
              document.getElementById("capacity-idle").textContent = capacity.idleCapacity;
              document.getElementById("capacity-queued").textContent = capacity.queuedJobs;
              document.getElementById("capacity-unserved").textContent = capacity.unservedJobs;
              capacityMessage.textContent = capacity.unservedJobs > 0
                ? `Capacity is capped at ${capacity.maximumCapacity}; ${capacity.unservedJobs} queued job(s) remain queued.`
                : capacity.currentCapacity > capacity.maximumCapacity
                  ? "Capacity is above the configured maximum while running jobs drain."
                  : capacity.isCapped
                    ? "The configured maximum is below the number of busy runners; no additional VMs will be added."
                    : "Capacity is within the configured limit.";
              capacityMessage.className = capacity.isCapped ? "warning" : "";
              capacityMinimumInput.value = capacity.minimumCapacity;
              capacityMaximumInput.value = capacity.maximumCapacity;
            }

            async function loadCapacity() {
              const response = await fetch("/api/runner-capacity");
              if (!response.ok) {
                capacityMessage.textContent = "Runner capacity could not be loaded.";
                return;
              }
              renderCapacity(await response.json());
            }

            function renderAudit(entries) {
              const rows = entries.map(entry => {
                const time = new Date(entry.timestamp).toLocaleString();
                return `
                  <tr>
                    <td>${entry.actor}</td>
                    <td>${time}</td>
                    <td>${entry.action}</td>
                    <td>${entry.outcome}</td>
                  </tr>
                `;
              }).join("");

              auditTrail.innerHTML = `
                <table>
                  <thead>
                    <tr><th>Actor</th><th>Timestamp</th><th>Action</th><th>Outcome</th></tr>
                  </thead>
                  <tbody>${rows || '<tr><td colspan="4">No audit entries.</td></tr>'}</tbody>
                </table>
              `;
            }

            async function loadAuditTrail() {
              const responses = await Promise.all([
                fetch("/api/runner-capacity/audit"),
                fetch("/api/runner-images/rollouts/audit"),
                fetch("/api/runner-vms/audit")
              ]);

              const entries = await Promise.all(responses.map(async response => {
                if (!response.ok) {
                  return [];
                }
                return await response.json();
              }));

              const merged = entries.flat().sort((left, right) => Date.parse(right.timestamp) - Date.parse(left.timestamp));
              renderAudit(merged);
              auditMessage.textContent = merged.length > 0
                ? `Showing ${merged.length} audited operation(s).`
                : "No audit entries have been recorded yet.";
            }

            async function load() {
              const response = await fetch("/api/runner-access");
              if (!response.ok) {
                message.textContent = response.status === 401 || response.status === 403
                  ? "You are not authorized to view this runner group."
                  : "Runner access could not be loaded.";
                return;
              }
              render(await response.json());
              await Promise.all([loadCapacity(), loadAuditTrail()]);
            }

            form.addEventListener("submit", async event => {
              event.preventDefault();
              message.textContent = "Saving allowlist…";
              const response = await fetch("/api/runner-access", {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                  repositories: repositories.value.split("\n").map(name => name.trim()).filter(Boolean)
                })
              });
              if (!response.ok) {
                const error = await response.json().catch(() => ({}));
                message.textContent = error.error || error.title || "The allowlist could not be saved.";
                return;
              }
              render(await response.json());
              message.textContent = "Allowlist saved.";
            });

            capacitySettingsForm.addEventListener("submit", async event => {
              event.preventDefault();
              capacityMessage.textContent = "Saving capacity settings…";
              const response = await fetch("/api/runner-capacity/settings", {
                method: "PUT",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                  minimumCapacity: Number(capacityMinimumInput.value),
                  maximumCapacity: Number(capacityMaximumInput.value)
                })
              });
              if (!response.ok) {
                const error = await response.json().catch(() => ({}));
                capacityMessage.textContent = error.error || error.title || "Capacity settings could not be saved.";
                return;
              }
              renderCapacity(await response.json());
            });

            capacityScaleOutForm.addEventListener("submit", async event => {
              event.preventDefault();
              capacityMessage.textContent = "Requesting scale-out…";
              const response = await fetch("/api/runner-capacity/scale-out", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ count: Number(capacityScaleOutCount.value) })
              });
              if (!response.ok) {
                const error = await response.json().catch(() => ({}));
                capacityMessage.textContent = error.error || error.title || "Scale-out could not be requested.";
                return;
              }
              const result = await response.json();
              renderCapacity(result.capacity);
              capacityMessage.textContent = result.isCapped
                ? `Added ${result.addedCount} VM(s); maximum capacity is reached.`
                : `Added ${result.addedCount} VM(s).`;
            });

            load().catch(() => {
              message.textContent = "Runner access could not be loaded.";
              capacityMessage.textContent = "Runner capacity could not be loaded.";
            });
            window.setInterval(() => Promise.all([
              loadCapacity().catch(() => {
                capacityMessage.textContent = "Runner capacity could not be refreshed.";
              }),
              loadAuditTrail().catch(() => {
                auditMessage.textContent = "Audit history could not be refreshed.";
              })
            ]), 30000);
          </script>
        </body>
        </html>
        """;
}
