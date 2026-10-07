# Billing (Paddle as Merchant of Record)

Status: **IMPLEMENTED + TESTED against stubbed Paddle API and locally signed webhooks. NOT YET VALIDATED against a real Paddle sandbox account** (external account boundary).

## Why Paddle
Paddle is the Merchant of Record: it collects payment, calculates and remits VAT/sales tax, issues invoices and handles refunds/chargebacks. **Card data never touches the desktop app or our backend** — checkout and the customer portal are Paddle-hosted pages opened in the system browser.

## Flow
1. Desktop calls `POST /api/v1/billing/checkout {planCode}` (authenticated).
2. Backend maps the plan to its Paddle price id (from configuration, never from the client), calls Paddle `POST /transactions` with `custom_data.user_id`, and returns the hosted checkout URL.
3. User pays in the browser. Paddle sends webhooks to `POST /api/v1/webhooks/paddle`.
4. Backend verifies, records and applies the event; entitlements change server-side only.
5. Desktop refreshes `GET /api/v1/me`. "Manage subscription" opens `POST /api/v1/billing/portal` → Paddle customer-portal session URL.

## Webhook security (`Services/Billing.cs`)
| Control | Implementation | Test |
|---|---|---|
| Signature | `Paddle-Signature: ts=…;h1=…`, HMAC-SHA256 over `ts:rawBody` with the notification secret, constant-time compare | `ForgedOrReplayedWebhooksAreRejected(bad-signature/missing)` |
| Replay window | timestamp must be within `Paddle:SignatureToleranceSeconds` (300 s) | `…(old-timestamp)` |
| Idempotency | `webhook_events.event_id` primary key; duplicates return `duplicate` without re-applying | `DuplicateEventsAreProcessedOnce` |
| Ordering | `subscriptions.last_event_occurred_utc`; older events are ignored | `OutOfOrderEventsDoNotRegressState` |
| Body logging | raw bodies are stored for audit in `webhook_events`; never logged | — |

## Subscription state handling
| Paddle status / event | Effect |
|---|---|
| `active`, `trialing` | Paid plan for the current billing period |
| `past_due` | Paid plan kept for `PastDueGraceDays` (7) → then default plan (`PastDueKeepsAccessDuringGraceThenFallsBack`) |
| `scheduled_change.action = cancel` | `CancelAtPeriodEnd = true`; access until period end (`CancellationAtPeriodEndKeepsAccessUntilPeriodEnd`) |
| `canceled`, `paused` | Default plan (after period end when cancellation was scheduled) |
| `transaction.completed` | Links Paddle customer id to user (for the portal) |

Refunds/chargebacks: Paddle cancels or pauses the subscription and sends the corresponding `subscription.*` event, which is applied as above.

## Configuration (environment variables / secret store only)
`Paddle__ApiBaseUrl` (sandbox `https://sandbox-api.paddle.com`, live `https://api.paddle.com`), `Paddle__ApiKey`, `Paddle__WebhookSecret`, `PlanCatalog__Plans__N__PaddlePriceIds__0`. Never commit these.

## Sandbox validation checklist (requires Paddle account — NOT YET DONE)
- [ ] Create sandbox products/prices, set price ids in config
- [ ] Create notification destination → `https://<staging>/api/v1/webhooks/paddle`, events `subscription.*`, `transaction.completed`
- [ ] Test-card checkout → `subscription.created` → `/me` shows paid plan
- [ ] Portal: cancel → `scheduled_change` → access until period end
- [ ] Simulate past_due via Paddle's webhook simulator
