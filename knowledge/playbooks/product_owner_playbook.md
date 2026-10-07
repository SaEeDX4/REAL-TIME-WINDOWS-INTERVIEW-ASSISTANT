# Product Owner Playbook (compact, for retrieval)

## Case framework (don't recite)
Goal → user/problem → constraints (legal, tech, cost) → priority → solution/product logic → Engineering + Compliance → metric.

## Strategy → delivery chain
Objective → outcome → epic → feature → story → business rules → acceptance criteria → dependencies → refinement → sprint → validation/UAT → release → measurement → iteration.
"Jira executes the strategy; Jira is not the strategy."

## Backlog & stories
- Story: who / what / why + business rules + acceptance criteria (Given/When/Then), edge cases, non-functional needs (audit, latency, security).
- Definition of Ready: value clear, AC testable, dependencies known, designs/legal input attached, sized.
- Definition of Done: AC met, tested (incl. edge/negative), documented, monitored, released, metric tracked.

## Prioritization
Value (revenue, retention), customer impact, risk & regulatory urgency, cost of delay, effort, dependencies, tech debt. Tools: RICE, impact/effort, MoSCoW, cost of delay — use only when helpful. Regulatory and integrity issues are non-negotiable "must".

## Discovery
Problem interviews, funnel + cohort data, support tickets, CRM feedback, experiments (A/B, staged rollout), smallest test of the riskiest assumption.

## Metrics
North Star (e.g. monthly active XAB utility users), input metrics (activation, frequency, tier progression), guardrails (reward cost, reconciliation breaks, complaints). OKRs: outcome-based. LTV, churn, conversion, retention by cohort.

## Stakeholder playbook
- Engineering disagrees → understand the constraint, return to the user outcome and data, explore options/trade-offs, decide transparently, document.
- Compliance blocks → treat as a requirement, ask *what* risk, redesign the flow to reach the goal compliantly; involve them early next time.
- C-level urgent ask → clarify the outcome behind it, show impact on current commitments, offer options (scope cut, swap), make the trade-off explicit.
- Tech debt vs features → quantify debt as risk/cost (incidents, velocity, integrity); reserve capacity; prioritize debt that blocks the roadmap or threatens ledger integrity.
- Marketing wants early launch → align on readiness criteria; staged rollout / beta; never compromise integrity or compliance.
- Mid-sprint change → protect the sprint goal; urgent only if critical; otherwise next refinement.
- Opposing stakeholders → common goal, data, explicit trade-off, escalate with a recommendation if needed.
- Unrealistic deadline → scope to MVP, phase, show risk; agree what "done" means.
- Feature underperforms → check data/instrumentation, talk to users, iterate or kill, share learning.
- Data contradicts exec → present data neutrally, propose a test.

## First 90 days
30: learn XAB lifecycle, ledger/reward logic, architecture, customers, metrics, backlog; meet Engineering, Compliance, CRM, leadership.
60: map friction and opportunities, improve backlog quality, define KPIs, validate hypotheses, align roadmap.
90: ship measurable improvements, set operating cadence (refinement, reviews, metric reviews), refine roadmap.
