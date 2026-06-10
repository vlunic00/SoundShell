Diagnostics Guide for SoundShell PoC

Overview
- Logs are configured via `appsettings.json` (Serilog) and environment variables.
- Key correlation fields: `ServiceInstanceId` (generated per run) and `SessionId` (per-session).

Log locations
- Default: `src/SoundShell.PoC/logs/soundshell-*.log` (rolling daily).
- Override via `appsettings.json` Serilog `WriteTo` File path or environment variable `SOUNDLOG_PATH` (legacy).

Configuration
- `appsettings.json` controls logging and monitoring options:
  - `Serilog` section configures sinks and minimum level.
  - `Monitoring` section binds to `WindowsAudioSessionService.MonitoringOptions`:
    - `SessionRegistrationMaxAttempts` (int)
    - `SessionRegistrationBackoffMs` (int)
    - `PerSessionRegistrationMaxAttempts` (int)
    - `PerSessionRegistrationBackoffMs` (int)

Environment overrides
- `ASPNETCORE_ENVIRONMENT` selects `appsettings.{ENV}.json` (e.g., `Development`).
- Environment variables override config values; recommended for CI/production secrets.

Structured logging and correlation
- Session events are logged with a scope including `SessionId` and `ServiceInstanceId` to enable filtering.
- Example query (grep/PowerShell): filter logs for a sessionId or service instance id.

Common tasks
- Inspect latest logs:
  - PowerShell: `Get-ChildItem -Path src\SoundShell.PoC\bin\Debug\net10.0-windows\logs -Filter "soundshell-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content -Tail 200 -Wait`
- Increase verbosity: set `Serilog:MinimumLevel` to `Debug` in `appsettings.Development.json` or set `SOUNDLOG_LEVEL=Debug`.

Production recommendations
- Use a centralized sink (Seq, Elasticsearch, or Splunk) in production via Serilog sinks.
- Rotate and retain logs; configure file sink rolling and retention.
- Add health checks and surface registration success/failure metrics.
- Expose `MonitoringOptions` via environment/CI for safe tuning without code changes.

Troubleshooting
- If COM registration fails, check for permission or audio device conflicts; logs include retry attempts and final failure messages.
- If events are missing on some hosts, keep a short polling fallback temporarily and run the integration harness to validate event flow.

Contact
- For environment-specific issues, include `ServiceInstanceId` and a timestamp when asking for help.
