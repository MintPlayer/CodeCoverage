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
