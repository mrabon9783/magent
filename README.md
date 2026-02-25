# magent

MVP .NET 8 advisory app for **Amarr station trading radar** in EVE Online.

## Scope and constraints
- Advisory only: data, recommendations, and alerts. No in-client automation.
- Amarr station trading only (hub-location filtering, no hauling/inter-hub).
- Uses ESI endpoints for character orders, market orders, market history, and wallet balance.
- Uses local SQLite file at `./data/magent.db`.
- Polls on a configurable interval (default 15 minutes).

## Solution layout
- `src/Magent.Cli` - command entrypoint and polling workflow
- `src/Magent.Core` - domain models, opportunity logic, report rendering
- `src/Magent.Esi` - ESI HTTP/OAuth client abstraction with retry/backoff and ETag support
- `src/Magent.Data` - SQLite schema + repository-style persistence
- `tests/Magent.Core.Tests` - unit tests for opportunities and intel parsing/scoring/cooldown

## Configuration
`config/config.json`:

```json
{
  "RegionId": 10000043,
  "HubLocationId": 60008494,
  "PollIntervalMinutes": 15,
  "BrokerFeePct": 3.0,
  "SalesTaxPct": 4.5,
  "MinNetMarginPct": 2.0,
  "MinDailyVolume": 50,
  "MaxWatchlistSize": 250,
  "MaxIskPerItem": 500000000.0,
  "MaxPortfolioExposurePct": 25.0,
  "MinOrderValue": 20000000.0,
  "MaxOrdersPerCycle": 10,
  "WebhookUrl": null,
  "Intel": {
    "ChatlogPath": null,
    "PilotScoreTtlMinutes": 10,
    "SystemScoreTtlSeconds": 120,
    "AlertCooldownSeconds": 300,
    "SystemAlertThresholdBand": "Med",
    "Denylist": [],
    "WebhookUrl": null
  }
}
```

## Commands
Run from repository root:

- `magent init`
  - Creates `data`, `out`, `config`
  - Writes config template if missing
  - Initializes SQLite schema tables:
    - `character_orders`
    - `orderbook_snapshots`
    - `opportunities`
    - `alerts_sent`
    - `watchlist`
    - `recommendation_history`
    - `recommendation_outcomes`

- `magent auth`
  - Prompts for an ESI refresh token, verifies it with ESI OAuth verify endpoint, then stores it in:
    - `~/.magent/refresh_token.txt`
  - On Linux/macOS, token file permissions are tightened to user read/write only.
  - Never logs token content.

### ESI OAuth requirement
Set `MAGENT_ESI_CLIENT_ID` before running commands that call authenticated ESI APIs (`auth`, `sync`, `run`, `report` wallet enrichment).
If your ESI app is configured as a confidential client, also set `MAGENT_ESI_CLIENT_SECRET`:

```bash
export MAGENT_ESI_CLIENT_ID=your_esi_application_client_id
export MAGENT_ESI_CLIENT_SECRET=your_esi_application_client_secret
```

- `magent sync`
  - Pulls character orders
  - Builds watchlist from character order type IDs
  - Pulls/stores Amarr-region market order snapshots, filtered to `hubLocationId`

- `magent run`
  - Loop: sync + opportunity calculation + report + deduped alerts + optional webhook POST

- `magent report`
  - Generates report from latest cached snapshot in DB without ESI re-sync

- `magent serve`
  - Starts a live dashboard server at `http://localhost:5000`
  - Lets you add/remove watchlist type IDs
  - Lets you select opportunities and copy buy/sell order lines to clipboard

- `magent intel watch [--chatlog-path <path>]`
  - Watches Windows EVE local chat logs (default `%USERPROFILE%\Documents\EVE\logs\Chatlogs`)
  - Prints immediate `NEW IN LOCAL` pending lines and resolved threat scores
  - Produces local summary + system risk updates
  - Writes `out/intel_latest.md` and `out/intel_latest.html`

- `magent intel paste [names...]`
  - Accepts names from args, comma-separated string, or stdin newline list
  - Starts a long-running clipboard monitor and resolves any newly seen pilot names

## Reports
Generated in `out/`:
- `today.md`
- `today.html`
- `intel_latest.md`
- `intel_latest.html`

Sections:
- Timestamp
- Wallet summary
- Orders needing update
- New seed opportunities
- Flip opportunities
- Historical performance
- Risk notes

## Build/test
```bash
dotnet build magent.sln
dotnet test tests/Magent.Core.Tests/Magent.Core.Tests.csproj
```

## Docker
```bash
docker build -t magent .
docker run --rm -it -v $(pwd)/data:/app/data -v $(pwd)/config:/app/config -v $(pwd)/out:/app/out magent init
```
