# Klaxon

**Self-hosted on-call scheduling and escalation. Rotations, ack, correct timezones. No SaaS bill.**

[![MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com)
[![CI](https://github.com/LindqvistMartin/klaxon/actions/workflows/ci.yml/badge.svg)](https://github.com/LindqvistMartin/klaxon/actions)

> **Status: early.** The domain model and persistence land first; the escalation
> engine, HTTP API, and web UI follow. Watch the commits.

## Why

PagerDuty starts at $21/user/month. In incident.io and Rootly, on-call is a paid
add-on on top of incident management. Opsgenie — the historically affordable
option — shuts down in April 2027, and its core is on-call. Meanwhile the main
open-source option, Grafana OnCall, was archived in March 2026.

Klaxon is a self-hosted on-call engine: who to wake and when to escalate, with one
`docker compose up`. State lives in Postgres and an escalation resumes after a
restart — the on-call engineer still gets paged after a mid-incident deploy.

## Planned features

- Rotations (daily / weekly) with schedule timezones and handoff times
- One-off overrides that take precedence over the rotation
- Escalation policies: ordered levels, each with a wait-for-ack timeout
- Durable, resumable escalations: state in Postgres, resumed on restart
- Ack without login via a signed link
- Alert ingestion: Prometheus and generic webhook to start
- Notification channels: email, Slack, webhook (log channel for demos)
- Timezone-correct on-call resolution (NodaTime), DST-safe
- Outbox-backed delivery: a page is never lost between "decided" and "sent"
- Real-time updates over SignalR; REST API + OpenAPI

## Architecture

Single deployable monolith. ASP.NET Core 10 on the front edge, EF Core with Postgres
for storage, `BackgroundService` for the escalation engine and outbox dispatch,
NodaTime for timezone-correct scheduling.

- `Klaxon.Api` — HTTP surface
- `Klaxon.Core` — domain: entities, escalation state machine, on-call resolver
- `Klaxon.Infrastructure` — EF Core, Postgres, channels, notification adapters

Design decisions are recorded in [`docs/adr`](docs/adr).

## Build

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK. The unit tests need no services. A Postgres connection string
(`ConnectionStrings:Postgres`) is needed to run the API; integration tests, as they land,
spin up their own Postgres via Testcontainers.

## Roadmap

- SMS / voice paging (Twilio)
- Follow-the-sun multi-timezone rotations
- On-call analytics (MTTA, ack rate, load balance)
- OIDC authentication

MIT.
