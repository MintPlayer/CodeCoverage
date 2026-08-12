# Coverage

A self-hosted code-coverage analyzer for GitHub — upload coverage reports from your
workflows, browse coverage per organization → repository → commit → file, and embed
badges in your READMEs.

Built on [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark)
(ASP.NET Core + RavenDB + Angular) with
[mintplayer-ng-bootstrap](https://github.com/MintPlayer/mintplayer-ng-bootstrap).

- **Product & architecture**: [docs/PRD.md](docs/PRD.md)
- **Milestone plan**: [docs/PLAN.md](docs/PLAN.md)
- **Upstream (Spark) work items**: [docs/spark-handoff.md](docs/spark-handoff.md)

## Local development

Prerequisites:

- .NET 10 SDK, Node 22+
- RavenDB running unsecured on `http://localhost:8080` (the `Coverage` database is
  auto-created in Development)
- A GitHub App (for sign-in + webhooks). Follow the walkthrough in
  MintPlayer.Spark's `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/README.md`;
  for local webhook delivery use a [smee.io](https://smee.io) channel.

### GitHub App settings

Create one App per environment (dev + prod). What the app actually uses:

**Repository permissions**

| Permission | Level | Why |
|---|---|---|
| Contents | Read-only | File view fetches source at a commit through the installation token; also required to subscribe to `push` events |
| Metadata | Read-only | Mandatory on every App; covers repository listings |
| Pull requests | Read-only | Required to subscribe to `pull_request` events |

**Account permissions**

| Permission | Level | Why |
|---|---|---|
| Email addresses | Read-only | First-time sign-in only auto-provisions a local account when GitHub attests a **verified primary email** — Spark reads `GET /user/emails` with the user's token, and for a GitHub App that endpoint needs this permission. Without it the popup completes but sign-in fails with `email_not_verified`. |

No organization permissions are needed: the viewer's visibility is derived
from `GET /user/installations` with the **user's OAuth token**, which lists
whatever installations that user can access on their own authority.

Planned upgrades (PLAN M9.11 — PR comments and commit checks) will additionally
need **Checks: Read & write** and **Pull requests: Read & write**; don't grant
them until that ships.

**Webhook events to subscribe**: `Repository`, `Push`, `Pull request`
(`installation` / `installation_repositories` are always delivered to Apps, no
subscription needed). Webhook URL: your smee channel in dev, `https://<host>/spark/webhooks/github` in prod;
set a webhook secret and keep it in `GitHub:WebhookSecret`.

**Identity (sign-in)**: add a **Callback URL** per environment — GitHub requires
exact matches including the port. Spark pins the OAuth callback path to
`/signin-github`, so for local dev that's `https://localhost:5200/signin-github`.
Leave *Request user authorization (OAuth) during installation* **unchecked**: it
makes GitHub redirect installs to the callback URL with a `code` but no OAuth
`state` (our server never initiated that flow), which the handler rejects. The
sign-in button performs its own properly-stated OAuth challenge and doesn't need
it. Optionally set the **Setup URL** to the app's home page so installs land
back in the app.
The App's *Client ID* / a generated *client secret* go into
`GitHub:{Development|Production}:ClientId` / `:ClientSecret` below; sign-in is
disabled (button throws "No authentication handler is registered for the scheme
'GitHub'") until they're configured.

Configure secrets (never commit them):

```bash
cd Coverage
dotnet user-secrets set "GitHub:Development:ClientId" "Iv1.…"
dotnet user-secrets set "GitHub:Development:ClientSecret" "…"
dotnet user-secrets set "GitHub:Development:AppId" "123456"
dotnet user-secrets set "GitHub:Development:PrivateKeyPath" "C:/path/to/app.private-key.pem"
dotnet user-secrets set "GitHub:WebhookSecret" "…"
dotnet user-secrets set "GitHub:SmeeChannelUrl" "https://smee.io/your-channel"
```

Run:

```bash
dotnet run --project Coverage --launch-profile https
```

The host spawns the Angular dev server itself (SPA proxy middleware) — do **not** run
`ng serve` separately. App: https://localhost:5200.

After changing entities, regenerate the model metadata:

```bash
dotnet run --project Coverage --launch-profile Synchronize
```

## Deployment

`docker-compose.yml` runs the app plus a pinned RavenDB on an internal network behind
Traefik. Copy `.env.example` to `.env`, fill in the GitHub App credentials, place the
App's private key at `./github-app.pem`.
