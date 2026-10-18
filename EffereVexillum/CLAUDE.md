# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

EffereVexillum is a multiplayer flag-drawing/guessing game. Players in a lobby draw a flag from memory, then guess each other's drawings. Two .NET 10 projects in `EffereVexillum.sln`:

- `BackendServer/` — ASP.NET Core minimal-API app (EF Core + PostgreSQL, MinIO object storage, SignalR, ASP.NET Identity API endpoints).
- `Frontend/` — Blazor WebAssembly app using MudBlazor, served by nginx in production.

There is no test project and no linter configured.

## Commands

- Full stack (postgres, minio, backend on `:9002`, frontend on `:9003`): `docker compose up --build`
- Frontend hot reload: `docker compose -f compose.yaml -f compose.dev.yaml up --build` (app on `:9003`). `Frontend/Dockerfile.dev` runs `dotnet watch` on a bind mount; the `gateway` nginx (`Frontend/nginx.dev.conf`) keeps one origin by routing `/api` and the SignalR hub to the backend and everything else (incl. the reload websocket) to the dev server. Backend is still the production image.
- Backend alone: `dotnet run --project BackendServer` (http://localhost:5116; needs Postgres and MinIO reachable — defaults are `Host=postgres`, `localhost:9000`, override via `ConnectionStrings:DefaultConnection` and `Minio:*` config/env vars, e.g. `Minio__Endpoint`)
- Frontend alone: `dotnet run --project Frontend`
- Build: `dotnet build EffereVexillum.sln`
- `k8s/` holds manifests mirroring the compose services; `docs/k8s.md` (repo root) covers the registry pull secret.
- CI (`.github/workflows/main.yml`) only builds and pushes the frontend and backend images to a private registry on pushes to `master`; it runs no tests.
- `download_flags.py` scrapes flag images from the vexillology fandom wiki into a local `flags/` dir (the untracked `Flags/` dir holds such images); the backend seeds from the MinIO `pictures` bucket, so images must be uploaded there separately.

## Architecture

**Request routing.** In Docker, `Frontend/nginx.conf` serves the WASM bundle and proxies `/api/` and `/lobby` (SignalR, with websocket upgrade headers) to `backendserver:8080`. The frontend therefore talks to the backend via its own origin (`HttpClient.BaseAddress`), with `CookieHandler` in `Frontend/Program.cs` forcing credentialed requests. Auth is cookie-based Identity (`/api/auth/*` via `MapIdentityApi`, plus a custom `/api/auth/logout`); backend CORS is wide open (`AllowAll`).

**Backend layout.**
- `Program.cs` does all startup work: DB setup via `EnsureCreated()` (no EF migrations — schema changes need manual handling; an ad-hoc `ALTER TABLE` for `Flags.Aliases` is run and swallowed on failure), then seeds `Flags` by listing the MinIO `pictures` bucket (filename → flag name, `_` → space), enriching category/aliases from the restcountries.com API. Npgsql is configured with `EnableDynamicJson()` because entities store lists/dictionaries as jsonb.
- `Database/` — entities: `Flag`, `Lobby`, `Game`, `Round`, `Stats` plus `AppDbContext` (also the Identity store). `Round.PlayerToDrawingMapping` / `PlayerPoints` are JSON dictionaries; mutations to them aren't change-tracked, so code sets `db.Entry(x).Property(...).IsModified = true` before saving — keep doing this.
- `Api/Endpoints.cs` — flags list, `/health`, and `/api/pictures` (proxy to MinIO).
- `Api/LobbyEndpoints.cs` — nearly all game logic: lobby CRUD, join links, start, guess, drawing upload/submit, and the round state machine.
- `Api/GameHub.cs` — a thin SignalR hub; clients only join/leave a group named by lobby id. All server→client events are pushed from endpoints/timers via `IHubContext<GameHub>`.

**Game flow / round state machine** (in `LobbyEndpoints.cs`): round `State` goes drawing → guessing → finished. `GameTimers` starts a server-side drawing timer per round (`Lobby.TimerForDrawing`); `FinishDrawingPhase` randomly assigns each guesser another player's drawing, and the round-finish step scores guesses, then either starts the next round or marks the game finished. Events pushed to the lobby group: `PlayerJoined`, `GameStarted`, `RoundStarted`, `DrawingPhaseFinished`, `RoundFinished`, `GameFinished`. Timers/background steps create their own DI scope from `IServiceProvider`. Guess checking (`IsGuessAcceptable`) normalizes case/spaces/hyphens and allows a Levenshtein distance of `max(1, len/4)` against the flag name and its aliases.

**Frontend.** Razor pages in `Frontend/Pages/` (`Lobby`, `Play`, `GuessFlags`, `CreateLobby`, `Login`, `Register`, `Profile`, ...; `Counter`/`Weather` are template leftovers). Drawing happens in `Components/DrawingCanvas.razor` (MudBlazor toolbar) backed by `wwwroot/js/drawing.js`, which keeps a vector scene (strokes, shapes, flood-fill bitmaps) with undo history, selection, grid/snap, zoom and a custom flag ratio; `Play.razor` and `PlayChain.razor` embed it and call `GetPngBase64Async()` on submit (the PNG size follows the chosen ratio, so guess/result `<img>`s must not hard-code dimensions). Blazor reaches the editor only through the module's `call(id, method, ...args)`; the JS calls back `OnEditorState`/`OnColorPicked`/`OnHotkeyTool`. `Lobby.razor` and `Play.razor` build `HubConnection`s to `/lobby` and react to the events above by re-fetching state from the REST endpoints.
