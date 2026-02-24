# magent - Agent Instructions

## Product goal
Build an EVE Online "Amarr Station Trading Radar" app that:
- Polls on a schedule (default 15 minutes) to find new station-trading opportunities in Amarr hub only.
- Produces actionable recommendations (no in-client automation).
- Alerts the user when high-confidence opportunities appear.

## Hard constraints (must follow)
- DO NOT automate EVE client actions (no botting). This app is advisory only: data + recommendations + alerts.
- Focus strictly on Amarr station trading (no hauling, no inter-hub arbitrage).
- Use ESI for data; cache responsibly; respect rate limits; use ETags/If-None-Match when possible.
- Persist state locally using SQLite (file-based DB).
- Must be Docker-friendly and runnable locally without cloud dependencies.

## Scope for MVP (first PR)
1) .NET 8 solution + projects:
   - src/Magent.Cli (command line)
   - src/Magent.Core (domain logic)
   - src/Magent.Esi (ESI clients + auth)
   - src/Magent.Data (SQLite persistence)
2) Config file (json): interval, thresholds, fees, Amarr hub IDs, watchlist rules.
3) Core loop:
   - Sync user’s character orders
   - Sync watchlist market orders (public) for Amarr region
   - Calculate opportunities (SEED/UPDATE/FLIP) using net margin after fees
   - Dedupe alerts and write reports (out/today.md + out/today.html)
4) README with setup/run steps.

## Market logic requirements
- Fee model must be configurable: broker fee %, sales tax %.
- Opportunity scoring must include:
  - net margin %
  - expected velocity heuristic (use market history or a placeholder if not available yet)
  - confidence level
- Alert only when net margin >= threshold and passes velocity/risk rules.

## CLI commands required
- magent init (creates folders, config template, db)
- magent auth (ESI OAuth flow; store refresh tokens securely in local storage)
- magent sync (one-shot sync)
- magent run (poll loop; default 15 min)
- magent report (generate report from last cached snapshot)

## Quality gates
- dotnet build must pass
- include at least minimal tests for opportunity calculation
- keep logs clean; never print tokens/secrets
