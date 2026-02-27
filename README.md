# Chronicle.Plugin.MusicBrainz

MusicBrainz metadata provider plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches music metadata — albums, artists, genres, track listings, and cover art — from [MusicBrainz](https://musicbrainz.org/) and the [Cover Art Archive](https://coverartarchive.org/). **No API key required.**

## Supported Media Types

| Media Type | Fields |
|------------|--------|
| `album`    | title, year, poster_url (cover art), genres, cast (performers), runtime_minutes |
| `artist`   | title, year, genres, overview (type, country, active period) |

## Installation

1. Build the plugin in Release mode:
   ```powershell
   dotnet publish -c Release -o ./publish
   ```

2. In the Chronicle web UI → **Plugins** → **Install Plugin**, enter the path to `Chronicle.Plugin.MusicBrainz.dll` inside the `publish/` folder.

3. Optionally configure a custom User-Agent string in plugin settings.

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `user_agent` | | `Chronicle/1.0 (...)` | MusicBrainz requires a descriptive User-Agent. See [Rate Limiting](https://wiki.musicbrainz.org/MusicBrainz_API/Rate_Limiting). |
| `fetch_cover_art` | | `true` | Download album art from the Cover Art Archive. Disable for metadata-only mode. |

> **Rate limiting:** MusicBrainz allows ~1 request/second for anonymous clients. Chronicle's plugin
> system does not currently implement automatic throttling — avoid bulk metadata refreshes.

## External ID Format

This plugin uses the format `{type}:{mbid}` where MBID is the MusicBrainz UUID:

- `album:f4179994-7621-4a46-b272-c62d8b3b9b1b` → a specific release
- `artist:5b11f4ce-a62d-471e-81fc-a69a8278c7da` → Nirvana

## Development

This plugin references Chronicle.Plugins via a local path reference for development.

```xml
<!-- Development (local) -->
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />

<!-- Production (NuGet) -->
<PackageReference Include="Chronicle.Plugins" Version="x.y.z" />
```
