# Crypto / FinTech Playbook (for "how would you" answers — never presented as Shervin's past work)

## Ledgers
- Ledger = source of truth; every balance change is a business event with a unique transaction ID.
- Double-entry: every debit has an equal credit; balances derived from entries; entries immutable, corrections via reversing entries.
- States: pending → settled → reversed/failed. Define allowed transitions.
- **Idempotency**: client/request key; duplicates return the original result. Prevents double postings on retries/timeouts.
- Reconciliation: ledger vs on-chain/custody vs reward engine vs finance; automated daily (or continuous); breaks become exceptions with owners and SLAs.
- Auditability: who/what/when/why, append-only, permissions, maker-checker for manual adjustments.
- Supply compliance: total issued ≤ cap; sum of balances + reserves = supply; alerts on drift.
- Monitoring: failed transactions, reconciliation break rate, latency, manual adjustments.

## Closed-loop token economy
Tokens circulate inside the platform (earn → hold → use → redeem/burn), off-chain on an internal ledger; benefits: speed, low fees, control; needs: clear rules, supply integrity, regulatory clarity, user transparency.

## On-chain ↔ internal ledger migration
Map balances/states/events → define mapping & cut-over → dual-run and reconcile → staged rollout → rollback plan → retire old path only after proof.

## Rewards / VIP / tokenomics
- Rewards must change behaviour, not just cost money: measure incremental impact and ROI.
- VIP tiers: clear eligibility (balance, volume, tenure), anti-gaming rules, grace periods, downgrade rules, transparency.
- Boosters: time-limited multipliers tied to target behaviour; caps and budget.
- Fee discounts paid in/with XAB: utility that creates demand.
- Sustainability: emission vs utility sinks; avoid inflation-driven sell pressure.

## Compliance
MiCA/MiCAR: CASP authorisation (Teroxx: CySEC CASP004/25), white-paper rules for crypto-assets other than ART/EMT, marketing communications must be fair, clear, not misleading and consistent with the white paper, conflicts of interest, complaints handling, custody/segregation. KYC/AML, Travel Rule (TFR) for transfers. Compliance is part of discovery: requirement → business rule → allowed/prohibited flows → data/audit needs → acceptance criteria → validate → monitor.

## API security concepts
Auth (OAuth2/keys), least privilege, rate limiting, signing/HMAC, idempotency keys, input validation, audit logs, secrets management, monitoring.
