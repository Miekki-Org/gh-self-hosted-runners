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
          </main>
          <script>
            const message = document.getElementById("message");
            const form = document.getElementById("allowlist-form");
            const repositories = document.getElementById("repositories");

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
              repositories.value = configuration.repositories.join("\n");
              message.textContent = "";
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

            load().catch(() => { message.textContent = "Runner access could not be loaded."; });
          </script>
        </body>
        </html>
        """;
}
