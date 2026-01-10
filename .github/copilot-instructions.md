# Grayjay.Desktop AI Coding Instructions

## Architecture Overview

**Grayjay** is a multi-platform media aggregator using a plugin-based architecture with JavaScript plugins that load video/media sources (YouTube, Reddit, etc.).

### Core Components

- **Grayjay.Desktop.CEF** - Desktop UI host (Windows, Linux, macOS). Uses Chromium Embedded Framework. Entry point in [Program.cs](Grayjay.Desktop.CEF/Program.cs).
- **Grayjay.ClientServer** - ASP.NET Core backend (HTTP + WebSocket API). Hosted in [GrayjayServer.cs](Grayjay.ClientServer/GrayjayServer.cs).
- **Grayjay.Engine** - Plugin runtime (separate repo). Executes JavaScript plugins via ClearScript V8 engine. Key class: [GrayjayPlugin.cs](Grayjay.Engine/Grayjay.Engine/GrayjayPlugin.cs).
- **Grayjay.Desktop.Web** - SolidJS frontend. Built to `Grayjay.Desktop.CEF/bin/Release/net8.0/{runtime}/publish/wwwroot/web/`.

### Plugin System

The plugin architecture is fundamental. Plugins are JavaScript files that implement media source integrations.

- **Plugin Execution**: [GrayjayPlugin](Grayjay.Engine/Grayjay.Engine/GrayjayPlugin.cs) wraps V8 runtime. Each plugin instance = one isolated V8 context.
- **Plugin Management**: [StatePlugins.cs](Grayjay.ClientServer/States/StatePlugins.cs) handles install/update/delete. Plugins are stored in `ManagedStore<PluginDescriptor>` with encryption.
- **Plugin Lifecycle**: [StatePlatform.cs](Grayjay.ClientServer/States/StatePlatform.cs) loads plugins into `_availableClients`/`_enabledClients` lists. Client pooling via [PlatformClientPool](Grayjay.ClientServer/Pooling/PlatformClientPool.cs) isolates plugin workloads (e.g., subscriptions don't block user interactions).
- **Developer Mode**: [DevGrayjayPlugin](Grayjay.ClientServer/Developer/DevGrayjayPlugin.cs) for testing. Uses [DeveloperController](Grayjay.ClientServer/Controllers/DeveloperController.cs) + embedded web UI.

## Build & Runtime

### Build Process
1. **Frontend**: `Grayjay.Desktop.Web` → `npm install && npm run build` (Vite → SolidJS)
2. **Backend**: `dotnet publish -r {win-x64|linux-x64|osx-arm64|osx-x64} -c Release`
3. **Copy Web**: Frontend `dist/` → CEF output `wwwroot/web/`

See [build.sh](build.sh) and [run.sh](run.sh) for exact commands. Build script requires `npm`, `dotnet 8.0`, and cross-platform runtimes.

### Server Architecture
- **HTTP**: ASP.NET Kestrel on `localhost:0` (random port) or `0.0.0.0:11338` (server mode)
- **Controllers**: Located in [Controllers/](Grayjay.ClientServer/Controllers) - route `/{controller}/{action}`. Examples: [DetailsController](Grayjay.ClientServer/Controllers/DetailsController.cs), [DownloadController](Grayjay.ClientServer/Controllers/DownloadController.cs).
- **WebSocket**: [WebSocketEndpoint](Grayjay.ClientServer/WebSockets) broadcasts plugin lifecycle events.
- **Port Discovery**: CEF process reads port from `{portfile}` after server starts.

## Key Patterns

### State Management
Central state classes in [States/](Grayjay.ClientServer/States):
- **StateApp** - Global app context (version, thread pools, main window, temp files)
- **StatePlugins** - Plugin registry + persistence
- **StatePlatform** - Active plugin clients + content routing
- **StateDeveloper** - Dev plugin testing
- **StateDownloads**, **StateHistory**, **StateSync**, etc. - Feature domains

State classes are typically static with private stores (`ManagedStore<T>`). Load on startup.

### Data Persistence
- **ManagedStore<T>** - Generic encrypted/backed-up JSON store. Example: `_plugins = new ManagedStore<PluginDescriptor>("plugins").WithEncryption().Load()`
- **StringStore** - Key-value strings
- **StringUniqueStore** - Deduplicated strings (e.g., plugin scripts)
- Located in [Store/](Grayjay.ClientServer/Store)

### HTTP/Networking
- **PluginHttpClient** - Custom HttpClient for plugins with rate limiting, proxy support
- **ManagedHttpClient** - Base client used in [Browser/](Grayjay.ClientServer/Browser) and media fetching
- **Proxy Support** - [ProxyController](Grayjay.ClientServer/Controllers/ProxyController.cs) + [ProxyProvider](Grayjay.ClientServer/Proxy) for plugin CORS bypass

### Async Patterns
- Thread pools: `StateApp.ThreadPool` (16 threads, general), `StateApp.ThreadPoolDownload` (4 threads, downloads)
- Long operations use `Task` + `await`. Plugin operations can be async (V8 handles promises).

## Testing

- **Unit Tests**: [Grayjay.Desktop.Tests/](Grayjay.Desktop.Tests) uses MSTest
- **Engine Tests**: [Grayjay.Engine.Tests/](Grayjay.Engine/Grayjay.Engine.Tests) in separate solution
- **Run Tests**: `dotnet test Grayjay.Desktop.sln`

Example test pattern in [DatabaseTests.cs](Grayjay.Desktop.Tests/DatabaseTests.cs) - setup store, assert state.

## Common Tasks

### Adding a Feature
1. **State Class**: Create `StateXxx.cs` in [States/](Grayjay.ClientServer/States) with static methods
2. **Controller**: Create `XxxController.cs` in [Controllers/](Grayjay.ClientServer/Controllers) with `[HttpGet/Post]` action methods
3. **Frontend**: Add route/component in `Grayjay.Desktop.Web/src/`
4. **WebSocket Broadcast**: Use `GrayjayServer.Instance.WebSocket.Broadcast(id, "EventType", data)` for live updates

### Modifying Plugin Runtime
- Changes to `GrayjayPlugin` require rebuilding `Grayjay.Engine` and publishing CEF
- Plugin API surface (methods plugins call) is exposed via reflection + `[JSDocs]` attributes
- Test via [DeveloperController](Grayjay.ClientServer/Controllers/DeveloperController.cs) - load test plugin, call methods

### Debugging
- **Logs**: `Logger.Info<ClassName>()` in [GrayjayLogger.cs](Grayjay.ClientServer/GrayjayLogger.cs)
- **CEF**: Opens browser dev tools (F12). WebSocket connection visible in Network tab.
- **Plugin Errors**: Appear in [StateDeveloper.Instance.LogDevInfo()](Grayjay.ClientServer/States/StateDeveloper.cs)

## Project Conventions

- **Namespaces**: Org.Project.Area (e.g., `Grayjay.ClientServer.States`, `Grayjay.Engine`)
- **Async**: Method naming doesn't use `Async` suffix consistently (e.g., `UpdateAvailableClients()` returns `Task`)
- **Null Handling**: Enabled (`<Nullable>enable</Nullable>` in `.csproj`). Use `?` and null-coalescing.
- **JSON**: Custom `GJsonSerializer` for plugin models. Serializer config in [Serializers/](Grayjay.ClientServer/Serializers)
