"""Authoring source for knowledge/question_bank.json and knowledge/test_questions.json.

Run: python3 scripts/question_bank_source.py
Each entry: (category, mode, canonical, [variants], [bullets], full, [evidence], gap_warning, [follow_ups], tech_note)
Modes: VERIFIED | HYPOTHETICAL | BRIDGE. Evidence ids refer to candidate_stories.json.
"""
import json, os, re

Q = []
def q(cat, mode, canon, variants, bullets, full="", evidence=(), gap="", follow=(), tech=""):
    Q.append(dict(cat=cat, mode=mode, canon=canon, variants=variants, bullets=bullets, full=full,
                  evidence=list(evidence), gap=gap, follow=list(follow), tech=tech))

# ---------------- INTRODUCTION / MOTIVATION ----------------
q("INTRODUCTION", "VERIFIED", "Tell me about yourself.",
  ["Walk me through your background", "Introduce yourself", "Can you give us a quick introduction",
   "Tell us a bit about your career so far", "Walk me through your CV", "Who is Shervin"],
  ["I'm a business and operations leader with over fifteen years across consumer electronics, IT and crypto.",
   "At Arzif Crypto Exchange I led operations and strategy, growing the platform to sixty thousand active traders and revenue by sixty percent.",
   "Before that, at LG I led go-to-market for product launches, and my software engineering degree helps me work closely with tech teams.",
   "Product Ownership of XAB is the natural next step, because it combines users, crypto, growth and technology in one role."],
  "I'm a business and operations leader with more than fifteen years across consumer electronics, IT and crypto. Most relevant here, at Arzif Crypto Exchange I led blockchain operations and strategic planning, growing the platform to around sixty thousand active traders in two years and revenue by sixty percent, while working on UX, infrastructure, partnerships with Binance, CoinEx and KuCoin, and regulatory compliance. Before that I spent seven years at LG leading go-to-market for new products, and I hold a degree in software engineering, which helps me translate between business and engineering. Today I run operations as Executive Director at Gabrielyte. Product Ownership of XAB brings all of this together: users, crypto, growth and technology.",
  ["arzif_growth", "arzif_revenue_retention", "lg_gtm"], "", ["Why Product Owner?", "Why Teroxx?"])

q("MOTIVATION", "VERIFIED", "Why do you want to become a Product Owner?",
  ["Why product ownership", "Why move into a product role", "Why the switch to product", "What attracts you to product ownership"],
  ["Throughout my career I've worked where customer needs, business value and technology meet.",
   "At Arzif that meant growth, UX, infrastructure and compliance decisions together, which is essentially product work.",
   "I want to own outcomes formally, with clear prioritization, well-defined stories and measurable results."],
  "", ["arzif_ux", "arzif_infra", "lg_gtm"])

q("MOTIVATION", "HYPOTHETICAL", "Why do you want to work at Teroxx?",
  ["Why Teroxx", "Why this company", "What do you know about Teroxx", "Why are you interested in us", "Why do you want this job"],
  ["Teroxx combines a MiCA-regulated platform with premium client service, which is exactly where crypto needs to go.",
   "XAB sits at the centre of that experience, connecting rewards, status levels and utility for clients.",
   "With my exchange background, I can help turn that token ecosystem into measurable engagement and retention."],
  "", ["arzif_growth"], "Company facts from company_brief.md (CySEC CASP004/25).", ["What would you change in XAB first?"])

q("MOTIVATION", "VERIFIED", "Why should we hire you?",
  ["What makes you the right candidate", "Why you", "What would you bring to this role", "What's your unique value"],
  ["I bring real crypto-exchange experience, growing Arzif to sixty thousand active traders and revenue by sixty percent.",
   "I combine growth, operations and customer experience with a software engineering education, so I bridge business and engineering.",
   "And I've worked with regulators' requirements and partners like Binance, so I understand trust and compliance in this market."],
  "", ["arzif_growth", "arzif_revenue_retention", "arzif_partnerships", "arzif_compliance"])

q("GAP_EXPERIENCE", "BRIDGE", "You haven't formally been a Product Owner. Why should we trust you with this role?",
  ["You have no product owner experience", "You've never had a product title", "Your background is sales and marketing, not product",
   "Have you worked as a product owner before", "This would be your first product owner role"],
  ["That's correct, Product Owner wasn't my formal title, although much of my work was product-oriented.",
   "At Arzif I worked across users, growth, UX, infrastructure, blockchain operations and regulatory requirements.",
   "What I'm adding now is formal product discipline: clear prioritization, strong stories and acceptance criteria, and measurable outcomes."],
  "", ["arzif_ux", "arzif_infra", "arzif_compliance"], "No formal PO title on résumé.")

q("GAP_EXPERIENCE", "BRIDGE", "Have you personally built or owned a financial ledger?",
  ["Have you built a ledger before", "Do you have ledger experience", "Have you owned a financial ledger system",
   "Tell me about your experience with ledgers"],
  ["I haven't owned a ledger implementation directly, but I understand the Product Owner responsibilities around it.",
   "I'd define every balance-changing event, transaction states, reversals, idempotency, reconciliation and audit requirements.",
   "Then I'd work closely with Engineering and Compliance to turn those rules into testable acceptance criteria."],
  "", ["arzif_infra"], "Job requires proven ledger/wallet/closed-loop ownership; résumé does not show it.")

q("GAP_EXPERIENCE", "BRIDGE", "Have you worked with closed-loop reward systems or wallet architectures?",
  ["Have you built a reward system", "Experience with wallet architecture", "Have you designed a loyalty program",
   "Tell me about your experience with rewards systems"],
  ["I haven't owned a closed-loop reward system build directly, but retention mechanics were central to my work at Arzif.",
   "We grew revenue by sixty percent over three years partly through user-retention strategies.",
   "For XAB, I'd define earn, hold and use rules clearly, and measure incremental behaviour against reward cost."],
  "", ["arzif_revenue_retention"], "No résumé evidence of owning a rewards/wallet build.")

q("GAP_EXPERIENCE", "BRIDGE", "Have you worked directly with MiCA?",
  ["Do you have MiCA experience", "Have you worked under MiCAR", "What is your experience with MiCA regulation"],
  ["I haven't been the owner of a MiCA implementation, but at Arzif I was responsible for compliance with international crypto regulations.",
   "Under MiCA, I'd treat requirements as product rules from the start, with Legal and Compliance in discovery.",
   "That means clear allowed flows, audit data, fair marketing consistent with the white paper, and testable acceptance criteria."],
  "", ["arzif_compliance"], "Résumé: 'international cryptocurrency regulations', not MiCA specifically.")

# ---------------- RÉSUMÉ / BEHAVIOURAL ----------------
q("RESUME_EXPERIENCE", "VERIFIED", "Tell me about your crypto experience.",
  ["What's your background in crypto", "Tell me about Arzif", "What did you do at Arzif Crypto Exchange",
   "Tell me about your blockchain experience", "What was your role at the exchange"],
  ["At Arzif Crypto Exchange I was Business Management Lead, responsible for blockchain operations and strategic planning.",
   "We grew to sixty thousand active traders in two years and increased revenue by sixty percent over three years.",
   "I also built partnerships with Binance, CoinEx and KuCoin, improved satisfaction by twenty-five percent and kept us compliant."],
  "", ["arzif_growth", "arzif_revenue_retention", "arzif_partnerships", "arzif_ux", "arzif_compliance"],
  "", ["How did you grow to 60,000 traders?"])

q("BEHAVIOURAL", "VERIFIED", "How did you grow Arzif to 60,000 active traders?",
  ["How did you grow the user base", "How did you get 60,000 traders", "Tell me about the growth at Arzif"],
  ["The growth came from combining strategy, digital marketing optimization and a focus on keeping traders active.",
   "We improved UX and customer support, which raised user satisfaction by twenty-five percent.",
   "And partnerships with Binance, CoinEx and KuCoin built credibility, which mattered a lot for a younger exchange."],
  "", ["arzif_growth", "arzif_revenue_retention", "arzif_ux", "arzif_partnerships"])

q("BEHAVIOURAL", "VERIFIED", "Tell me about a time you improved retention.",
  ["Give an example of improving retention", "How have you reduced churn", "Tell me about a retention success"],
  ["At Arzif, retention was a core lever, and user-retention strategies were part of how revenue grew sixty percent over three years.",
   "We paired that with UX and support improvements, raising user satisfaction by twenty-five percent.",
   "At Philips, analysing customer data to identify trends drove a ten percent increase in customer retention."],
  "", ["arzif_revenue_retention", "arzif_ux", "philips_pricing_crm"])

q("BEHAVIOURAL", "VERIFIED", "Tell me about a time you worked with engineering on a technical problem.",
  ["Example of working with developers", "How have you collaborated with technical teams", "Tell me about infrastructure improvements you drove"],
  ["At Arzif, platform reliability was critical, so we prioritised infrastructure upgrades and scalability improvements with the technical team.",
   "My software engineering background helped me understand the trade-offs and translate them for the business.",
   "The result was a twenty percent reduction in platform downtime."],
  "", ["arzif_infra"])

q("BEHAVIOURAL", "VERIFIED", "Tell me about a partnership you negotiated.",
  ["Tell me about the Binance partnership", "How did you work with Binance, CoinEx and KuCoin", "Example of a strategic partnership"],
  ["At Arzif I established co-branding partnerships with Binance, CoinEx and KuCoin.",
   "For a younger exchange, that association mattered because traders choose platforms they can trust.",
   "It enhanced the platform's credibility and supported our growth to sixty thousand active traders."],
  "", ["arzif_partnerships", "arzif_growth"])

q("BEHAVIOURAL", "VERIFIED", "Tell me about a product launch you led.",
  ["Tell me about a go-to-market", "Give me a launch example", "How have you launched products"],
  ["At LG Electronics I led go-to-market strategies for new product launches across multiple markets.",
   "I used market analysis in SPSS and Excel to understand customers and position products.",
   "That work contributed to a ten percent market-share increase."],
  "", ["lg_gtm"])

q("BEHAVIOURAL", "VERIFIED", "Tell me about a time you used data to make a decision.",
  ["Example of data-driven decision", "How do you use analytics", "Tell me about your data analysis experience"],
  ["At LG I ran market analysis with SPSS and advanced Excel to guide where and how we competed.",
   "That analysis contributed to a ten percent increase in market share.",
   "At Philips, analysing customer data to spot trends drove a ten percent increase in retention."],
  "", ["lg_gtm", "philips_pricing_crm"])

q("LEADERSHIP", "VERIFIED", "Tell me about your leadership style.",
  ["How do you lead teams", "Describe how you manage people", "What kind of leader are you"],
  ["I lead by making goals clear, giving people ownership, and staying close enough to remove blockers.",
   "At Gabrielyte I built the initial team, cutting onboarding time by thirty percent and turnover by twenty-five percent.",
   "At LG I mentored ten sales representatives, which improved team performance by fifteen percent."],
  "", ["gabrielyte_ops", "lg_mentoring"])

q("LEADERSHIP", "VERIFIED", "What do you do in your current role?",
  ["Tell me about Gabrielyte", "What is your current position", "What are you doing now"],
  ["I'm Executive Director at Gabrielyte in Vilnius, running operations end to end: purchasing, sales, marketing and customer service.",
   "I grew revenue by twenty percent through targeted market expansion and raised client retention by fifteen percent.",
   "Service-delivery scores improved eighteen percent, and I'm now looking to apply this in a crypto product role."],
  "", ["gabrielyte_ops", "gabrielyte_growth"])

q("BEHAVIOURAL", "VERIFIED", "Tell me about a time you reduced costs.",
  ["Example of cost optimization", "How have you managed budgets", "Tell me about a negotiation that saved money"],
  ["At Gabrielyte I negotiated contracts that reduced costs by ten percent and improved operational efficiency.",
   "At Philips I managed sales and marketing budgets and cut costs by eight percent through vendor negotiations.",
   "My approach is always to protect what customers value and remove spend that doesn't change outcomes."],
  "", ["gabrielyte_growth", "philips_pricing_crm"])

q("BEHAVIOURAL", "VERIFIED", "Tell me about your experience with CRM systems.",
  ["Have you used Salesforce or HubSpot", "How have you used CRM", "Tell me about CRM implementation"],
  ["I've used Salesforce and HubSpot, and implemented CRM at both Philips and Epson.",
   "At Philips it improved lead conversion by eighteen percent; at Epson it improved pipeline efficiency by fifteen percent.",
   "For XAB, I'd work closely with CRM to target rewards, tier communication and re-engagement by segment."],
  "", ["philips_pricing_crm", "epson_channels"])

q("BEHAVIOURAL", "VERIFIED", "Tell me about a time you failed or something didn't go as planned.",
  ["Tell me about a failure", "Describe a mistake you made", "What's a setback you learned from"],
  ["Early in fast growth, it's easy to focus on acquisition and underinvest in reliability and support.",
   "At Arzif we corrected that by prioritising infrastructure upgrades and support processes alongside growth.",
   "Downtime dropped twenty percent and satisfaction rose twenty-five percent, and I learned to treat reliability as a product feature."],
  "", ["arzif_infra", "arzif_ux"], "Framed as a lesson; résumé doesn't describe a specific failure — Shervin should adapt with his own real memory.")

q("BEHAVIOURAL", "VERIFIED", "Tell me about a time you handled a difficult stakeholder.",
  ["Example of stakeholder conflict", "Tell me about a disagreement with a partner", "How did you deal with a difficult stakeholder"],
  ["In partnerships like those with Binance, CoinEx and KuCoin, each side had its own priorities and brand concerns.",
   "I focus on the shared goal, understand their constraints, and make trade-offs explicit and in writing.",
   "That approach helped us build partnerships that enhanced our credibility and supported growth."],
  "", ["arzif_partnerships"], "Specific conflict details not in résumé — speak at principle level.")

q("BEHAVIOURAL", "VERIFIED", "What is your biggest achievement?",
  ["What are you most proud of", "Your greatest accomplishment"],
  ["I'm most proud of the growth at Arzif, reaching sixty thousand active traders in two years.",
   "It wasn't one lever; it combined retention strategy, UX, infrastructure reliability and strong partnerships.",
   "Revenue grew sixty percent over three years while we stayed compliant in a demanding regulatory environment."],
  "", ["arzif_growth", "arzif_revenue_retention", "arzif_compliance"])

q("RESUME_EXPERIENCE", "VERIFIED", "How does your software engineering background help you?",
  ["Are you technical", "How technical are you", "Tell me about your technical background", "Can you work with engineers"],
  ["My bachelor's is in Computer Software Engineering, so I understand architecture, data and how systems fail.",
   "That lets me discuss trade-offs credibly with engineers and write precise, testable requirements.",
   "At Arzif it helped when we prioritised infrastructure upgrades that cut downtime by twenty percent."],
  "", ["arzif_infra"], "Degree only — never claim professional developer work.")

q("RESUME_EXPERIENCE", "VERIFIED", "What languages do you speak?",
  ["Do you speak Lithuanian", "What is your English level"],
  ["Persian is my native language and I work professionally in English.",
   "I'm also learning Lithuanian, currently at a basic A1 level.",
   "I've worked across Iran, Dubai and Lithuania, so international collaboration is natural for me."])

q("RESUME_EXPERIENCE", "VERIFIED", "Do you have experience working remotely and in international teams?",
  ["Remote work experience", "How do you work with distributed teams", "Have you worked in international environments"],
  ["Yes, I've worked across Tehran, Dubai and Vilnius, and Arzif operated across Tehran and Vilnius.",
   "Remote collaboration works best with clear written goals, a steady cadence and transparent priorities.",
   "As a Product Owner, I'd keep the backlog, decisions and metrics visible so everyone stays aligned."])

q("AGILE_JIRA", "VERIFIED", "What is your experience with Jira and Agile?",
  ["Have you used Jira", "How do you use Jira", "Are you familiar with Scrum", "Tell me about your agile experience"],
  ["I've used Jira and MS Project for project management, and I see Jira as the tool that executes strategy, not the strategy itself.",
   "I'd structure work from objective and outcome down to epics, stories, business rules and acceptance criteria.",
   "Then refinement, sprint delivery, validation, release and measurement, so every ticket links back to a goal."],
  "", [], "Résumé lists JIRA and MS Project under competencies.")

# ---------------- PRODUCT STRATEGY / XAB ----------------
q("XAB", "HYPOTHETICAL", "How would you increase XAB adoption?",
  ["How would you drive XAB adoption", "How do we get more users to use XAB", "How would you grow the XAB user base",
   "How would you increase usage of the token"],
  ["I'd start with utility, understanding exactly why users should earn, hold and use XAB.",
   "Then I'd connect meaningful benefits like fee advantages, rewards and status progression to real user behaviour.",
   "Finally, I'd measure activation, XAB usage, retention, reward cost and incremental business value."],
  "", ["arzif_revenue_retention"], "", ["Which metric would you look at first?"])

q("XAB", "HYPOTHETICAL", "How would you improve XAB utility?",
  ["How would you make XAB more useful", "What utility would you add to XAB", "How do you strengthen token utility"],
  ["I'd first map where XAB appears in the client journey today and where users actually feel the benefit.",
   "Then I'd prioritise utility that changes behaviour, such as fee discounts, reward boosters and exclusive experiences.",
   "I'd measure usage frequency, time to first utility event and retention to see which utilities truly matter."])

q("XAB", "HYPOTHETICAL", "What would be your first priorities in this role?",
  ["What would you do first", "What's the first thing you'd tackle", "Where would you start with XAB"],
  ["First I'd understand the XAB lifecycle end to end, including ledger, rewards logic, tiers and current metrics.",
   "Then I'd meet Engineering, Compliance, CRM and leadership to align on the biggest problems and constraints.",
   "From that, I'd build a prioritised backlog focused on integrity first, then utility and retention."])

q("XAB", "HYPOTHETICAL", "What would you do in your first 90 days?",
  ["What's your 30 60 90 day plan", "First three months plan", "How would you onboard into the role"],
  ["In the first thirty days I'd learn the XAB lifecycle, ledger and reward logic, customers, metrics and the backlog.",
   "By sixty days I'd have mapped friction points, clarified KPIs and prioritised the strongest opportunities with stakeholders.",
   "By ninety days I'd ship measurable improvements and set a steady cadence of refinement, reviews and metric tracking."])

q("REWARDS", "HYPOTHETICAL", "How would you prioritize improvements to the XAB rewards ecosystem?",
  ["How would you prioritise reward improvements", "How do you decide which rewards features to build",
   "Prioritizing the rewards roadmap"],
  ["I'd start with the customer and business outcome, whether retention, utility, revenue or a specific friction point.",
   "I'd rank opportunities by user impact, business value, regulatory risk, dependencies and engineering effort.",
   "Then I'd ship the highest-value hypothesis, measure its effect, and use the data to adjust the roadmap."])

q("REWARDS", "HYPOTHETICAL", "How would you design a reward booster?",
  ["How would reward boosters work", "Design a booster mechanic", "How would you use boosters to drive engagement"],
  ["I'd tie the booster to one specific behaviour we want, like locking XAB longer or trying a new product.",
   "I'd set clear rules, time limits, caps and budget, and check them with Compliance for fair communication.",
   "Then I'd measure incremental behaviour versus a control group and the reward cost per retained user."])

q("REWARDS", "HYPOTHETICAL", "How do you make sure rewards are sustainable and not just a cost?",
  ["How do you control reward costs", "How do you measure reward ROI", "Rewards are expensive, how would you manage that"],
  ["I'd treat every reward as an investment that must change behaviour, not just a giveaway.",
   "So I'd measure incremental retention, trading and usage against a control group, not raw participation.",
   "Rewards with weak ROI get redesigned or retired, and budget moves to mechanics that create real value."])

q("VIP", "HYPOTHETICAL", "How would you design VIP tier governance?",
  ["How should VIP tiers work", "How would you manage status levels", "How do you design VIP eligibility",
   "Lite Silver Gold Platinum how would you improve them"],
  ["I'd define clear, transparent eligibility rules, for example balance, activity and tenure, with explicit upgrade and downgrade logic.",
   "I'd add anti-gaming rules, grace periods and an audit trail so every tier change is explainable.",
   "Then I'd check that each tier's benefits actually drive progression, retention and value, not just cost."])

q("VIP", "HYPOTHETICAL", "How would you increase engagement of VIP clients?",
  ["How do you keep high value clients engaged", "Premium client engagement", "How to retain VIP users"],
  ["I'd start by understanding what high-value clients actually value, through data and direct conversations.",
   "Then I'd design exclusive benefits and experiences that feel personal, working closely with CRM and client support.",
   "I'd track VIP retention, activity, tier progression and satisfaction to see what really works."])

q("TOKENOMICS", "HYPOTHETICAL", "How do you think about tokenomics for a utility token like XAB?",
  ["Explain tokenomics", "What makes good tokenomics", "How would you think about XAB supply and demand"],
  ["Healthy tokenomics balance how tokens enter circulation with real reasons to hold and use them.",
   "For XAB, the supply is capped at two hundred fifty million, so utility and sinks matter more than distribution volume.",
   "I'd monitor emissions, usage, holding behaviour and reward cost so the ecosystem stays sustainable."],
  "", [], "Supply cap is a whitepaper fact.")

q("TOKENOMICS", "HYPOTHETICAL", "What risks do you see in a token rewards ecosystem?",
  ["What could go wrong with token rewards", "Risks of reward tokens", "What are the risks of XAB"],
  ["The biggest risk is rewards that create sell pressure without real utility, which erodes trust and value.",
   "There are also integrity risks like duplicate payouts or reconciliation breaks, and regulatory risk in how rewards are marketed.",
   "I'd manage these with clear rules, strong ledger controls, Compliance in discovery, and close monitoring."])

q("XAB", "HYPOTHETICAL", "What metrics would you use to measure success of XAB?",
  ["Which KPIs would you track", "How do you measure token success", "What would your north star metric be",
   "How would you know XAB is succeeding"],
  ["I'd use a North Star like monthly active XAB utility users, meaning clients who actually use a benefit.",
   "Supporting that I'd track activation, usage frequency, tier progression, retention by cohort and time to first utility event.",
   "And guardrails like reward cost, reward ROI, reconciliation breaks and complaints, so growth stays healthy."])

q("PRODUCT_STRATEGY", "HYPOTHETICAL", "What would your vision be for the XAB product?",
  ["What's your product vision", "Where should XAB be in two years", "Describe your vision for Abloxx"],
  ["I'd want XAB to be the simple key that makes every Teroxx client relationship more rewarding.",
   "Clients should clearly understand why to earn, hold and use it, with benefits that grow as they engage.",
   "Underneath, it needs a trustworthy ledger, full compliance and metrics proving it creates real value."])

q("PRODUCT_STRATEGY", "HYPOTHETICAL", "How would you build a product roadmap?",
  ["How do you create a roadmap", "Roadmap planning", "How do you plan a product roadmap"],
  ["I'd start from business objectives and user problems, and express the roadmap as outcomes rather than a feature list.",
   "Then I'd sequence work by value, risk, regulatory needs, dependencies and effort, with near-term detail and longer-term themes.",
   "I'd review it regularly with stakeholders and adjust based on results and learning."])

q("PRODUCT_STRATEGY", "HYPOTHETICAL", "What is the difference between a Product Owner and a Product Manager?",
  ["Product owner versus product manager", "How is a PO different from a PM"],
  ["A Product Manager often focuses more on market, strategy and discovery, while the Product Owner maximises value delivered by the team.",
   "The Product Owner owns the backlog, priorities, stories and acceptance criteria, and works closest with Engineering.",
   "In practice, for XAB I'd do both: connect strategy and tokenomics to precise, testable delivery."])

q("PRODUCT_STRATEGY", "HYPOTHETICAL", "How would you compete with other exchanges' token programs?",
  ["How does XAB compare to BNB or other exchange tokens", "Competitive analysis of exchange tokens", "Who are the competitors"],
  ["I wouldn't copy large exchange tokens; I'd build on Teroxx's strengths: regulation, premium service and simplicity.",
   "XAB should make the boutique experience more rewarding, with benefits clients clearly feel.",
   "I'd benchmark competitors' utilities, then focus on the few that matter most to our client segments."])

# ---------------- LEDGER / RECONCILIATION ----------------
q("LEDGER", "HYPOTHETICAL", "How would you approach an internal XAB ledger?",
  ["How would you design the internal ledger", "Tell me how you would own the ledger", "Internal ledger for XAB",
   "How would you manage the token ledger"],
  ["I'd first define every event that can change an XAB balance and make the ledger the clear source of truth.",
   "I'd specify transaction states, duplicate protection, reversals, reconciliation, auditability and exception handling with Engineering and Compliance.",
   "Then I'd turn those rules into acceptance criteria and monitor reconciliation accuracy and failed transactions after release."])

q("LEDGER", "HYPOTHETICAL", "How would you prevent duplicate financial transactions?",
  ["How do you avoid double spending", "How do you prevent duplicate payouts", "What is idempotency", "Explain idempotency"],
  ["I'd require every transaction to carry a unique idempotency key, so repeated requests can't create a second posting.",
   "The ledger should return the original result for duplicates and keep a complete audit trail.",
   "I'd test retries, timeouts and partial failures, and reconciliation, before treating the flow as production-ready."])

q("RECONCILIATION", "HYPOTHETICAL", "How would you design an automated reconciliation process?",
  ["How would you reconcile the ledger", "Explain reconciliation", "How do you ensure balances match",
   "Automated reconciliation rules"],
  ["I'd define which sources must agree, for example the ledger, the rewards engine, custody or on-chain balances, and finance.",
   "Then I'd set automated matching rules, tolerances and frequency, with every break becoming an exception with an owner and SLA.",
   "I'd track the break rate and resolution time as critical KPIs, and alert immediately on supply mismatches."])

q("LEDGER", "HYPOTHETICAL", "How would you ensure token supply compliance?",
  ["How do you make sure supply is correct", "Supply integrity", "How do you guarantee total supply never exceeds the cap"],
  ["I'd make it an invariant: total balances plus reserves must always equal the issued supply, and never exceed the cap.",
   "That check should run automatically, with immediate alerts and a frozen issuance path if it ever fails.",
   "Every mint, burn and transfer would be logged and auditable, so Compliance can verify it at any time."])

q("LEDGER", "HYPOTHETICAL", "How would you migrate XAB from an on-chain model toward an internal ledger?",
  ["On-chain to off-chain migration", "Migrating to an internal ledger", "Moving tokens to a closed-loop system"],
  ["I'd first map ownership, balances, token states and every business event that must stay consistent during migration.",
   "Then I'd define migration, reconciliation, rollback and audit rules with Engineering, Finance, Legal and Compliance.",
   "I'd launch in controlled stages, reconcile both systems rigorously, and retire the old path only after the data is proven."])

q("LEDGER", "HYPOTHETICAL", "What is double-entry accounting and why does it matter for a ledger?",
  ["Explain double entry", "Debits and credits in a ledger", "Why double-entry bookkeeping"],
  ["Double entry means every movement is recorded as an equal debit and credit, so the books always balance.",
   "For XAB, a reward payout debits a rewards pool and credits the user, so supply is never created by accident.",
   "It makes errors detectable and every balance fully explainable, which is essential for audit and trust."])

q("LEDGER", "HYPOTHETICAL", "How would you handle a reversal or correction in the ledger?",
  ["How do you handle refunds", "What happens when a transaction is wrong", "How do you correct ledger errors"],
  ["I'd never edit or delete entries; corrections should be new reversing entries linked to the original.",
   "Each reversal needs a reason, an authorised approver and a full audit trail, ideally with maker-checker for manual cases.",
   "Then reconciliation confirms the corrected balances, and we track root causes to prevent repeats."])

q("LEDGER", "HYPOTHETICAL", "What edge cases would you consider for ledger transactions?",
  ["Ledger edge cases", "What can go wrong in a transaction", "Failure scenarios for transactions"],
  ["I'd consider retries and timeouts, duplicate requests, partial failures and concurrent updates on the same balance.",
   "Also pending transactions that never settle, reversals after tier changes, and rounding on reward calculations.",
   "Each one becomes an explicit rule and acceptance criterion, tested before release and monitored afterward."])

q("LEDGER", "HYPOTHETICAL", "What are the transaction states you would define?",
  ["Transaction lifecycle", "Pending settled reversed", "Transaction state machine"],
  ["I'd keep it simple and explicit: initiated, pending, settled, failed and reversed.",
   "Each state needs allowed transitions, who or what can trigger them, and how balances are affected.",
   "That clarity makes reconciliation, customer support and audit much easier."])

q("LEDGER", "HYPOTHETICAL", "How would you write acceptance criteria for a reward payout feature?",
  ["Give an example of acceptance criteria", "Write a user story for rewards", "How do you write acceptance criteria"],
  ["Given an eligible locked position, when the daily payout runs, the user is credited the exact calculated XAB once.",
   "If the job reruns, no duplicate credit is created, and every payout has a ledger entry, reference and audit record.",
   "Reconciliation must match total payouts to the rewards pool debit, and failures must raise an alert."])

q("TECHNICAL_CONCEPT", "HYPOTHETICAL", "What is a closed-loop token economy?",
  ["Explain closed loop economy", "What does closed-loop mean for tokens"],
  ["A closed-loop economy means tokens circulate inside the platform: users earn, hold, use and redeem them within the ecosystem.",
   "It's often run on an internal ledger, which gives speed, low cost and control over rules.",
   "The trade-off is that the platform must guarantee integrity, transparency and compliance itself."])

q("TECHNICAL_CONCEPT", "HYPOTHETICAL", "What is the difference between a custodial and non-custodial wallet?",
  ["Custodial vs non custodial", "Explain wallet types", "What is a custodial wallet"],
  ["In a custodial wallet the platform holds the keys and manages assets on the client's behalf.",
   "In a non-custodial wallet the user controls their own private keys and full responsibility.",
   "For a regulated boutique like Teroxx, custody brings simplicity for clients but strict safeguarding and reconciliation duties."])

q("TECHNICAL_CONCEPT", "HYPOTHETICAL", "What is an ERC-20 token?",
  ["Explain ERC20", "What standard is XAB"],
  ["ERC-20 is the standard interface for fungible tokens on Ethereum, defining balances and transfers.",
   "It makes tokens like XAB compatible with wallets and exchanges across the ecosystem.",
   "From a product view, it matters for interoperability, fees and how on-chain and internal balances reconcile."])

q("TECHNICAL_CONCEPT", "HYPOTHETICAL", "How do you think about API security for a financial product?",
  ["API security", "How do you secure APIs", "Security requirements for the token platform"],
  ["I'd define security as product requirements: strong authentication, least-privilege access and signed requests.",
   "I'd add rate limiting, idempotency keys, input validation and full audit logs on every balance-changing call.",
   "And I'd make monitoring and alerting part of the definition of done, with Security reviewing before release."])

q("TECHNICAL_CONCEPT", "HYPOTHETICAL", "What is staking and how is it different from your rewards program?",
  ["Explain staking", "Is the rewards program staking", "Staking versus rewards", "What is yield in crypto"],
  ["Staking usually means locking tokens to help secure a blockchain network in return for protocol rewards.",
   "A platform rewards program pays rewards from the platform under its own rules, for example fixed daily XAB on locked assets.",
   "The difference matters for risk disclosure, compliance and how we communicate it to clients."])

q("TECHNICAL_CONCEPT", "HYPOTHETICAL", "How would you write a technical specification for engineering?",
  ["How do you write specs", "How do you translate business into technical requirements", "Requirements for developers"],
  ["I'd start with the goal and the user problem, so engineers understand why, not just what.",
   "Then business rules, data and state changes, edge cases, non-functional needs like audit and performance, and acceptance criteria.",
   "I'd review it with Engineering early, so they shape the solution and we agree what done means."])

# ---------------- COMPLIANCE ----------------
q("MICAR", "HYPOTHETICAL", "What do you know about MiCA and how does it affect this product?",
  ["What is MiCAR", "Explain MiCA", "How does MiCA regulation impact XAB", "Markets in crypto assets regulation"],
  ["MiCA is the EU framework for crypto-assets, covering service providers, white papers and marketing communications.",
   "Teroxx is authorised as a CASP by CySEC, so client protection, disclosures and conduct rules apply to everything we build.",
   "For XAB, marketing and product features must stay consistent with the white paper, fair and not misleading."],
  "", ["arzif_compliance"], "CySEC CASP004/25 is a public register fact.")

q("COMPLIANCE", "HYPOTHETICAL", "How do you work with Legal and Compliance as a Product Owner?",
  ["How do you collaborate with compliance", "Working with legal", "How do you include compliance in product development"],
  ["I treat compliance as part of product discovery, not a final approval step.",
   "I bring Legal and Compliance in early, translate requirements into business rules, allowed flows and audit needs.",
   "Then we turn them into testable acceptance criteria, validate before release and monitor after launch."],
  "", ["arzif_compliance"])

q("COMPLIANCE", "HYPOTHETICAL", "What would you do if Compliance blocks a feature?",
  ["Compliance says no", "Legal rejected your feature", "Regulatory blocker on the roadmap"],
  ["I'd first understand exactly which risk or rule is the issue, rather than treating it as a no.",
   "Then I'd work with Compliance to redesign the flow so we reach the business goal in a compliant way.",
   "And I'd bring them in earlier next time, so constraints shape the design from the start."])

q("COMPLIANCE", "HYPOTHETICAL", "How do KYC and AML affect product design?",
  ["Explain KYC AML", "Know your customer requirements", "Anti money laundering in product"],
  ["KYC and AML define who can access which features and when, so they shape onboarding and eligibility flows.",
   "I'd design them to be as smooth as possible while meeting requirements, with clear states like pending or restricted.",
   "Rewards and tiers must respect those rules, and every decision should be auditable."])

q("COMPLIANCE", "HYPOTHETICAL", "How would you ensure marketing of XAB rewards is compliant?",
  ["Compliant marketing of tokens", "How do you communicate rewards", "Marketing communications under MiCA"],
  ["Under MiCA, communications must be fair, clear, not misleading and consistent with the white paper.",
   "I'd agree messaging rules and required risk wording with Compliance and Marketing before any campaign.",
   "And I'd make sure product behaviour exactly matches what we promise, which protects trust long-term."])

# ---------------- PRIORITIZATION / EXECUTION ----------------
q("PRIORITIZATION", "HYPOTHETICAL", "How do you prioritize the backlog?",
  ["How do you prioritise features", "Prioritization approach", "How do you decide what to build next", "What prioritization framework do you use"],
  ["I prioritise by outcome: customer impact, business value, risk and regulatory urgency, weighed against effort and dependencies.",
   "Integrity and compliance issues come first, because trust is the foundation of a financial product.",
   "I use frameworks like RICE or cost of delay when helpful, but I always make the trade-off transparent."])

q("PRIORITIZATION", "HYPOTHETICAL", "How do you balance technical debt against new features?",
  ["Tech debt versus features", "How do you prioritise technical debt", "When do you pay down tech debt"],
  ["I treat technical debt as business risk, so I quantify its impact on incidents, speed and integrity.",
   "Debt that threatens the ledger or blocks the roadmap goes first; I also reserve steady capacity for it.",
   "At Arzif, prioritising infrastructure upgrades cut downtime by twenty percent, so I've seen the payoff."],
  "", ["arzif_infra"])

q("PRODUCT_EXECUTION", "HYPOTHETICAL", "How do you write a good user story?",
  ["What makes a good user story", "User story format", "How do you write stories"],
  ["A good story states who the user is, what they need and why, in a slice small enough to deliver in a sprint.",
   "It includes business rules, edge cases and clear acceptance criteria, often written as given, when, then.",
   "And it's refined with Engineering and QA, so everyone agrees on what done means."])

q("AGILE_JIRA", "HYPOTHETICAL", "What is your Definition of Ready and Definition of Done?",
  ["Definition of ready", "Definition of done", "When is a story ready"],
  ["Ready means the value is clear, acceptance criteria are testable, dependencies and legal input are known, and it's sized.",
   "Done means acceptance criteria are met, edge cases tested, documentation and monitoring in place, and it's released.",
   "For financial features, I'd also include audit logging and reconciliation checks in done."])

q("AGILE_JIRA", "HYPOTHETICAL", "How do you run sprint planning and refinement?",
  ["Backlog refinement", "Sprint planning process", "Scrum ceremonies"],
  ["Before planning, I make sure top stories are refined, prioritised and ready with clear acceptance criteria.",
   "In planning, we agree a sprint goal tied to an outcome, and the team commits to what they can deliver.",
   "Then reviews and retros close the loop on results and how we work."])

q("PRODUCT_EXECUTION", "HYPOTHETICAL", "How do you run user acceptance testing and releases?",
  ["UAT process", "How do you validate before release", "Release planning"],
  ["I define UAT scenarios from the acceptance criteria, including negative and edge cases.",
   "For sensitive financial features, I prefer staged rollouts with feature flags and a clear rollback plan.",
   "After release, I monitor the key metrics and errors closely before scaling to everyone."])

q("PRODUCT_DISCOVERY", "HYPOTHETICAL", "How do you do product discovery?",
  ["How do you understand user needs", "Customer research approach", "How do you validate an idea"],
  ["I combine data, like funnels and cohorts, with direct conversations, support tickets and CRM feedback.",
   "Then I identify the riskiest assumption and test it as cheaply as possible, with a prototype, survey or small experiment.",
   "Only validated problems move into the delivery backlog."],
  "", ["lg_gtm", "arzif_ux"])

q("METRICS", "HYPOTHETICAL", "How would you run an experiment to test a new reward?",
  ["A/B testing", "How do you run experiments", "Test a hypothesis"],
  ["I'd write a clear hypothesis: this reward will increase a specific behaviour by a target amount.",
   "Then I'd run it on a randomised segment against a control group, with guardrails on cost and complaints.",
   "If it delivers incremental value, we scale it; if not, we learn and iterate."])

q("METRICS", "HYPOTHETICAL", "How do you set OKRs or a North Star metric?",
  ["What are OKRs", "Define a north star metric", "How do you set objectives"],
  ["A North Star should capture the core value users get, for XAB perhaps active users of token benefits.",
   "OKRs then define a clear objective with two or three measurable outcomes, not feature outputs.",
   "I'd connect every epic in the backlog to one of those outcomes."])

q("METRICS", "HYPOTHETICAL", "How would you reduce churn?",
  ["How do you improve retention", "Users are leaving, what do you do", "Churn reduction strategy"],
  ["I'd first find where and why users churn, using cohorts, behaviour data and direct feedback.",
   "Then I'd fix the biggest friction and strengthen reasons to return, like meaningful rewards and tier benefits.",
   "At Arzif, retention strategies were part of how we grew revenue sixty percent over three years."],
  "", ["arzif_revenue_retention"])

q("PRODUCT_EXECUTION", "HYPOTHETICAL", "A feature you launched is underperforming. What do you do?",
  ["Feature failed after launch", "Low adoption after release", "What if a release doesn't hit targets"],
  ["First I'd check the data and instrumentation to confirm it's really underperforming.",
   "Then I'd talk to users and look at the funnel to find whether it's awareness, usability or value.",
   "Based on that, I'd iterate quickly, or retire it, and share the learning openly."])

# ---------------- STAKEHOLDERS ----------------
q("STAKEHOLDER", "HYPOTHETICAL", "What do you do when Engineering disagrees with you?",
  ["Conflict with developers", "Engineers push back on your requirement", "Disagreement with the tech team"],
  ["I'd first understand their concern, because engineers often see risks I don't.",
   "Then we'd go back to the user outcome and data, and look at options with their trade-offs together.",
   "I make the decision transparently, document why, and stay open to revisiting it if new facts appear."])

q("STAKEHOLDER", "HYPOTHETICAL", "How do you handle an urgent request from the C-level?",
  ["CEO wants a feature now", "Executive asks for something urgently", "Leadership changes priorities"],
  ["I'd clarify the outcome behind the request, because the real need is often different from the first ask.",
   "Then I'd show the impact on current commitments and offer options, like reducing scope or swapping priorities.",
   "That keeps leadership in control of the trade-off while protecting the team's focus."])

q("STAKEHOLDER", "HYPOTHETICAL", "Two stakeholders want opposite priorities. How do you decide?",
  ["Conflicting stakeholder priorities", "Marketing and engineering want different things", "How do you resolve competing demands"],
  ["I'd bring them back to the shared goal and the data on customer and business impact.",
   "Then I'd make the trade-off explicit and propose a recommendation, sometimes with a phased approach.",
   "If we still can't agree, I'd escalate with that recommendation rather than leave it unresolved."])

q("STAKEHOLDER", "HYPOTHETICAL", "Marketing wants to launch before Engineering is ready. What do you do?",
  ["Pressure to launch early", "Launch date versus readiness", "Marketing pushes the release"],
  ["I'd align everyone on clear readiness criteria, especially integrity, compliance and support.",
   "Often a beta or staged rollout lets Marketing start while we limit risk.",
   "But I wouldn't compromise ledger integrity or compliance for a date."])

q("STAKEHOLDER", "HYPOTHETICAL", "Requirements change in the middle of a sprint. How do you handle it?",
  ["Mid sprint scope change", "New urgent requirement during the sprint"],
  ["I'd protect the sprint goal and assess whether the change is truly urgent.",
   "If it's critical, like a compliance or integrity issue, we swap scope transparently with the team.",
   "Otherwise it goes into refinement and the next sprint."])

q("STAKEHOLDER", "HYPOTHETICAL", "How do you handle an unrealistic deadline?",
  ["Deadline is too tight", "Impossible timeline", "What if the date can't be met"],
  ["I'd make the risk visible early with a clear view of scope, capacity and dependencies.",
   "Then I'd propose options: a smaller MVP, phased delivery, or moving the date.",
   "I'd agree what done means for that date, so no one is surprised."])

q("STAKEHOLDER", "HYPOTHETICAL", "What if data contradicts what leadership believes?",
  ["Data disagrees with the CEO", "Challenging executive assumptions"],
  ["I'd present the data neutrally and focus on the shared goal, not on being right.",
   "If there's still doubt, I'd propose a small experiment to test the assumption.",
   "Leadership then decides with evidence, and we move forward together."])

q("STAKEHOLDER", "HYPOTHETICAL", "How would you work with the CRM team on XAB?",
  ["Collaboration with CRM", "How do you use CRM for rewards", "Working with marketing and CRM"],
  ["CRM is key for turning XAB benefits into behaviour, through segmentation, communication and re-engagement.",
   "I'd align on target segments and triggers, like tier upgrades or unused benefits, and share clear eligibility data.",
   "Then we'd measure campaign impact on usage and retention, not just opens or clicks."],
  "", ["philips_pricing_crm"])

q("STAKEHOLDER", "HYPOTHETICAL", "How do you keep stakeholders aligned?",
  ["Stakeholder management", "How do you communicate with stakeholders", "Stakeholder alignment"],
  ["I keep a visible roadmap and backlog linked to outcomes, so everyone sees what we're doing and why.",
   "I run a steady cadence of reviews with IT, Compliance, CRM and leadership, and share metrics openly.",
   "And I make trade-offs explicit early, so decisions don't surprise anyone."])

# ---------------- CASE STUDIES ----------------
q("CASE_STUDY", "HYPOTHETICAL", "Design a trading fee discount feature using XAB.",
  ["Fee discount with tokens", "How would trading fee management work", "Use XAB to pay lower fees"],
  ["I'd define the goal first, for example more active trading and XAB usage, and the eligible clients and products.",
   "Then rules: discount levels by tier or holding, how fees are calculated and recorded in the ledger, and limits.",
   "I'd launch to a segment, measure trading activity, XAB demand and fee revenue impact, and adjust."])

q("CASE_STUDY", "HYPOTHETICAL", "How would you design an exclusive experience for top-tier clients?",
  ["Exclusive user experiences", "Premium benefits for Platinum", "VIP experiences"],
  ["I'd start by learning what top clients value most, such as access, insight or personal service.",
   "Then design a few high-quality experiences tied to tier status, delivered with CRM and client support.",
   "I'd measure retention, activity and satisfaction of that segment against the cost."])

q("CASE_STUDY", "HYPOTHETICAL", "A reconciliation shows a mismatch in XAB balances. What do you do?",
  ["Reconciliation break", "Balances don't match", "Ledger mismatch incident"],
  ["First I'd contain it, pausing affected flows if needed, and inform Engineering, Finance and Compliance.",
   "Then we trace the break to specific transactions, correct with reversing entries, and communicate with affected clients.",
   "Finally a root-cause review, with new rules or tests so it can't happen the same way again."])

q("CASE_STUDY", "HYPOTHETICAL", "How would you redesign the rewards program onboarding to improve activation?",
  ["Improve activation", "Get new users to their first reward", "Onboarding funnel"],
  ["I'd map the funnel from sign-up to first reward and find the biggest drop-off.",
   "Then simplify that step, explain the benefit clearly, and guide users to a first utility event quickly.",
   "I'd measure activation rate and time to first reward, and test changes against a control."])

q("CASE_STUDY", "HYPOTHETICAL", "If the XAB price drops sharply, how would that affect your product decisions?",
  ["Token price falls", "Market downturn impact", "Bear market strategy for XAB"],
  ["I'd focus even more on utility, so XAB's value to clients doesn't depend only on price.",
   "I'd review reward costs and communication with Compliance, keeping messaging fair and clear.",
   "And I'd watch retention and sell pressure closely to adjust mechanics if needed."])

q("CASE_STUDY", "HYPOTHETICAL", "How would you launch a new XAB utility feature end to end?",
  ["Walk me through launching a feature", "End to end feature delivery", "From idea to release"],
  ["I'd start with the problem and goal, validate it with data and users, and involve Compliance early.",
   "Then define stories, business rules and acceptance criteria with Engineering, and build in slices.",
   "We'd launch in stages with CRM support, measure adoption and impact, and iterate."],
  "", ["lg_gtm"])

# ---------------- GENERAL / CLOSING ----------------
q("GENERAL_BUSINESS", "VERIFIED", "What are your strengths?",
  ["Your main strengths", "What are you good at"],
  ["I connect business, customers and technology, which is exactly where product decisions live.",
   "I'm strongly results-oriented, with growth at Arzif, LG, Philips and Epson to show for it.",
   "And I build trust across teams and partners, from engineers to partners like Binance."],
  "", ["arzif_growth", "lg_channel", "arzif_partnerships"])

q("GENERAL_BUSINESS", "VERIFIED", "What is your biggest weakness?",
  ["Your weaknesses", "What do you need to improve"],
  ["Product Owner hasn't been my formal title, so I'm deliberately strengthening formal backlog and story-writing discipline.",
   "I've been practising with structured acceptance criteria and outcome-based roadmaps.",
   "My strength in business and crypto operations means I can focus that learning where it matters most."])

q("GENERAL_BUSINESS", "HYPOTHETICAL", "Where do you see yourself in five years?",
  ["Five year plan", "Career goals"],
  ["I see myself as a strong product leader in digital assets, owning products that clients genuinely value.",
   "Ideally growing XAB into a central, trusted part of the Teroxx ecosystem.",
   "And helping build a product culture that combines growth, compliance and technical excellence."])

q("GENERAL_BUSINESS", "HYPOTHETICAL", "What are your salary expectations?",
  ["Salary expectation", "What compensation are you looking for"],
  ["I'm flexible and most interested in the role and the impact I can have.",
   "I'd expect a package in line with the market for a Product Owner in European FinTech.",
   "I'm happy to discuss the details once we see the fit is right."], "", [], "Shervin should replace with his own number.")

q("GENERAL_BUSINESS", "HYPOTHETICAL", "Do you have any questions for us?",
  ["Any questions for me", "What would you like to ask us", "Is there anything you want to know"],
  ["How do you currently define success for the XAB ecosystem?",
   "What's the biggest product problem you'd want this role to solve in the first six months?",
   "How do Product, Engineering and Compliance work together when a new reward or token mechanic is designed?"],
  "Other good questions: How is the internal ledger and reconciliation set up today, and where are the pain points? How do clients currently experience XAB and the status levels? Which metrics does leadership review for XAB? How is the roadmap decided between XAB and other Teroxx products? What does the engineering team look like? How do you see XAB evolving under MiCA? What would make someone exceptional in this role after a year?")

q("CRYPTO", "HYPOTHETICAL", "What trends in crypto do you think matter most right now?",
  ["Crypto market trends", "What's happening in the crypto industry", "Future of digital assets"],
  ["Regulation like MiCA is making trust and compliance a real competitive advantage in Europe.",
   "Users increasingly expect simple, premium experiences, not just access to trading.",
   "And token utility is shifting from speculation toward real benefits that drive loyalty."])

q("CRYPTO", "HYPOTHETICAL", "How do you build user trust in a crypto product?",
  ["Trust in crypto", "How do you make users trust the platform", "Security and trust"],
  ["Trust comes from reliability, transparency and regulation working together.",
   "That means accurate balances, clear rules for rewards and tiers, and fast, human support.",
   "At Arzif, reducing downtime twenty percent and improving satisfaction twenty-five percent were central to building trust."],
  "", ["arzif_infra", "arzif_ux"])

q("FOLLOW_UP", "HYPOTHETICAL", "Can you give a concrete example?",
  ["Give me an example", "Can you be more specific", "What would that look like in practice"],
  ["For example, at Arzif we combined UX and support improvements with retention strategies.",
   "That raised user satisfaction by twenty-five percent and supported sixty percent revenue growth over three years.",
   "For XAB, I'd apply the same approach: fix friction first, then reward the behaviour that matters."],
  "", ["arzif_ux", "arzif_revenue_retention"], "Generic fallback — the LLM should contextualise using the previous question.")


# ---------------- emit ----------------
STOP = set("a an the and or of to in on for with is are was were be been do does did you your we our i me my it this that what how why when which who would could should can will at by as from about tell us".split())
def keywords(text):
    words = re.findall(r"[a-zA-Z][a-zA-Z0-9\-]+", text.lower())
    out = []
    for w in words:
        if w not in STOP and len(w) > 2 and w not in out:
            out.append(w)
    return out[:14]

root = os.path.join(os.path.dirname(__file__), "..", "samples", "shervin-teroxx")
bank = []
for i, e in enumerate(Q, 1):
    full = e["full"] or " ".join(e["bullets"])
    bank.append({
        "question_id": f"Q{i:03d}",
        "canonical_question": e["canon"],
        "category": e["cat"],
        "intent": e["canon"].rstrip("?."),
        "keywords": keywords(e["canon"] + " " + " ".join(e["variants"])),
        "semantic_variants": e["variants"],
        "answer_mode": e["mode"],
        "short_bullets": e["bullets"],
        "optional_full_answer": full,
        "candidate_evidence": e["evidence"],
        "gap_warning": e["gap"],
        "technical_notes": e["tech"],
        "product_notes": "",
        "follow_up_questions": e["follow"],
    })
with open(os.path.join(root, "question_bank.json"), "w", encoding="utf-8") as f:
    json.dump({"version": 1, "questions": bank}, f, ensure_ascii=False, indent=1)

# Test questions: spoken-style paraphrases (expected match) + unexpected (expect LLM fallback)
tests = []
for e, b in zip(Q, bank):
    if e["variants"]:
        tests.append({"utterance": "So, um, " + e["variants"][0].lower() + "?", "expected_question_id": b["question_id"], "expected_category": e["cat"], "kind": "paraphrase"})
unexpected = [
 "How would you price a premium subscription bundled with XAB benefits?",
 "What do you think about stablecoins under MiCA?",
 "If you had to cut half the roadmap tomorrow, what would you keep?",
 "How would you explain the ledger to a non-technical CEO in one minute?",
 "What would you do if a whale tries to game the VIP tier rules?",
 "How would you handle a data breach affecting reward balances?",
 "What's your opinion on gamification in financial products?",
 "How would you integrate a new blockchain network for XAB?",
 "What makes a product team high-performing?",
 "How would you measure whether cashback in XAB is worth its cost?",
]
for u in unexpected:
    tests.append({"utterance": u, "expected_question_id": None, "expected_category": None, "kind": "unexpected"})
with open(os.path.join(root, "test_questions.json"), "w", encoding="utf-8") as f:
    json.dump({"tests": tests}, f, ensure_ascii=False, indent=1)
print(len(bank), "questions;", len(tests), "tests")
