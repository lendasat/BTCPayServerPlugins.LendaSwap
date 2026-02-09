# WSL Development Setup Guide

## Prerequisites

- WSL2 (Ubuntu) on Windows
- Docker Desktop with WSL2 integration enabled

## 1. Install .NET 8 SDK on WSL

The default `apt` .NET package on WSL is often broken (`/usr/lib/dotnet/host/fxr does not exist`).
Use the official install script instead:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel 8.0
```

Add to your shell profile (`~/.bashrc` or `~/.zshrc`):

```bash
export PATH="$HOME/.dotnet:$PATH"
```

Verify:

```bash
dotnet --version
# Should output something like 8.0.417
```

## 2. Initialize Git Submodules

The repo depends on `btcpayserver` and `strike-client` submodules:

```bash
git submodule update --init --recursive
```

## 3. Build the Solution

Build the entire solution (use `-maxcpucount:1` to avoid WSL file-locking issues):

```bash
dotnet build BTCPayServerPlugins.RockstarDev.sln --configuration Debug -maxcpucount:1
```

> **Note:** Parallel builds on WSL can fail with `System.IO.IOException: The process cannot access the file ... because it is being used by another process`. The `-maxcpucount:1` flag fixes this by serializing the build.

Expect ~93 warnings (mostly `CS8632` nullable annotations) and **0 errors**.

## 4. Generate Plugin Config

The `ConfigBuilder` project scans the `Plugins/` folder for built DLLs and writes an `appsettings.dev.json`:

```bash
dotnet run --project ConfigBuilder
```

This creates `submodules/btcpayserver/BTCPayServer/appsettings.dev.json` with all plugin DLL paths in the `DEBUG_PLUGINS` field.

## 5. Start Docker Services

BTCPayServer needs PostgreSQL, Bitcoin Core (regtest), and NBXplorer. These are defined in the test docker-compose file:

```bash
cd submodules/btcpayserver/BTCPayServer.Tests
docker compose up -d postgres bitcoind nbxplorer
```

> **Note:** `docker compose up dev` tries to build an `sshd` container which requires the `docker-buildx` plugin. Starting only the three core services avoids this.

Verify they're running:

```bash
docker ps
# Should show postgres (port 39372), bitcoind (port 43782), nbxplorer (port 32838)
```

## 6. Start BTCPayServer

From the repo root:

```bash
dotnet run --no-build \
  --project submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj \
  -- \
  --network regtest \
  --postgres "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=btcpayserver" \
  --btcexplorerurl "http://127.0.0.1:32838/" \
  --port 14142
```

BTCPayServer will be available at **http://localhost:14142**.

On first launch it will run all database migrations and then prompt you to create an admin account.

## 7. Loading Plugins (TODO)

The `appsettings.dev.json` with `DEBUG_PLUGINS` is only loaded when `ASPNETCORE_ENVIRONMENT=Development`. To load the plugins, add the environment variable:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build \
  --project submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj \
  -- \
  --network regtest \
  --postgres "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=btcpayserver" \
  --btcexplorerurl "http://127.0.0.1:32838/" \
  --port 14142
```

## Quick Reference

| Step | Command |
|------|---------|
| Install .NET | `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash /dev/stdin --channel 8.0` |
| Init submodules | `git submodule update --init --recursive` |
| Build | `dotnet build BTCPayServerPlugins.RockstarDev.sln -c Debug -maxcpucount:1` |
| Generate config | `dotnet run --project ConfigBuilder` |
| Start Docker | `cd submodules/btcpayserver/BTCPayServer.Tests && docker compose up -d postgres bitcoind nbxplorer` |
| Start BTCPayServer | See command in Step 6 |
| Stop Docker | `cd submodules/btcpayserver/BTCPayServer.Tests && docker compose down` |
