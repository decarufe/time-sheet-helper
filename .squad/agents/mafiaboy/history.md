📌 Imported from squad-export on 2026-10-14T12:28:15.554Z. Portable knowledge carried over; project learnings from previous project preserved below.

# Project Context

- **Owner:** Eric De Carufel
- **Project:** time-sheet-helper — app Windows locale (.NET 10, tray + UI Blazor Server sur `localhost:5178`) qui collecte l'activité (Windows, VS Code, Git, Azure DevOps, GitHub, Microsoft 365) pour préparer les feuilles de temps Forecast.
- **Stack:** .NET 10, EF Core SQLite, plugins `ISourcePlugin` (AssemblyLoadContext), MSAL/Graph, MCP Forecast, Anthropic (opt-in), extension VS Code (pnpm), MSI WiX per-user non signé.
- **Created:** 2026-09-01 (réaligné sur time-sheet-helper le 2026-10-14)

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

- 2026-09-01 : Équipe montée. Je suis la sécurité. Attention notée dans les docs sur la sécurité multi-clients des synchros Quickbooks (voir `Documents/SLIM_SRE`).
- 2026-09-30: Politique modèle — toujours spawné avec `model` Anthropic explicite (config.json modelPolicy).
- 2026-10-14 : (obsolète) la ligne 2026-09-01 concernait SLIM / Quickbooks, un autre projet ; ignorer.
- 2026-10-14 : Audit initial (lecture seule), rapport complet dans `.squad/decisions/inbox/mafiaboy-audit-initial.md`.
  - OK : jeton d'ingestion solide (`IngestEndpoints.cs`: CSPRNG 32 o, `FixedTimeEquals`, contrôle avant lecture du corps, plafond 256 Ko) ; pas de CORS donc pas de CSRF sur `/ingest` ; `Urls=http://localhost:5178` (loopback par config seulement, surchargeable).
  - M-1 (haute, à confirmer par PoC) : UI Blazor sans auth ni filtrage Host/Origin (pas d'`AllowedHosts`) + installation de plugin depuis l'UI (`Sources.razor:592-611`) → DNS rebinding = RCE utilisateur. Correctif : `UseHostFiltering` + contrôle Origin + test.
  - M-2 : aucun sandbox ni signature des plugins ; ils reçoivent le `ISecretStore` complet (`PluginContext.cs:21`). Limiter la portée par plugin + signature.
  - M-3 : `.mcp.json` cherché dans le cwd et tous les parents, `http` accepté, hôte libre (`ForecastOptions.cs:62-96`). Restreindre à `https` + hôte `mcp.forecast.it`.
  - M-4 : extension VS Code : `endpoint`/`token` sans `scope: machine` (`package.json:28-37`) → exfiltration du jeton via `.vscode/settings.json`.
  - Bas : jeton en clair sur disque (accepté), corps d'erreurs OAuth journalisés (`ForecastMcpConnector.cs:171`), git sur dépôts non fiables, .zip sans plafond décompressé.
  - Anthropic : désactivé par défaut (`EngineOptions.cs:113`), 8 échantillons max par groupe ; à documenter côté vie privée.
  - Leçon : vérifier un fichier avant d'affirmer son contenu (j'avais écrit à tort que `.mcp.json` n'avait pas d'entrée Forecast MCP ; corrigé).
