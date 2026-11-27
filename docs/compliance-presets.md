# Compliance presets (docs only)

Suggested settings per regime for a dotwire deployment. These are a starting point for a host's
own counsel and auditor, not a configuration dotwire applies.

**No preset exists in code.** dotwire has no regime switch and no preset file. The settings
below are plain configuration keys that the host sets in its own deployment configuration:

- `Dotwire:Compliance:AllowChanges` (default `true`)
- `Dotwire:Compliance:MinimumKeepDays` (default `0`)
- `Dotwire:Compliance:Erasure:Mode` (default `anonymize`, or `delete`)
- `Dotwire:AccessLog:Enabled` (default `false`)
- `Dotwire:AccessLog:RetentionDays` (default `0`, which keeps access-log rows indefinitely)

Retention is set with `Dotwire:Retention:MessagesDays` at startup, or with `PUT /admin/retention`.
Use one of the two, not both (see COMPLIANCE.md, "Technical controls inventory").

Every figure and legal reading below is our reading. Confirm each one with counsel before you rely on
it. Nothing here makes dotwire compliant or certified for any regime; certification and attestation
attach to a deployment and its operator (see COMPLIANCE.md, "Ready, not certified").

## Overlap rule: longest keep period wins, erasure after it ends

Settings are per deployment, not per room or per user. When more than one regime applies to the same
data, or when one deployment serves rooms under different regimes:

1. Use the **longest** keep period of any applicable regime as `MinimumKeepDays`, and the longest
   retention as `Retention:MessagesDays`. The startup check refuses a retention value that is above
   `0` and below `MinimumKeepDays`.
2. If any applicable regime requires write-once records, set `AllowChanges` to `false` for the
   whole deployment. That is the strictest setting, and it applies to every room.
3. An erasure request is not refused by a keep period. Messages inside the keep period, or under a
   legal hold, stay in place. The erasure stays open, reports `waiting` with the blocking reason, and
   finishes by itself once the period ends and the hold is released. The host does not need to send
   a second request.
4. With `AllowChanges` set to `false`, an erasure cannot re-attribute or delete messages. It stays
   `waiting` (`changes_not_allowed`) until retention removes the rows at end of life, or until the
   emergency path is used with a reason code.

When the strictest settings would be wrong for most of the data, for example write-once trader chat
next to employee chat under GDPR, run a separate dotwire deployment per regime instead of applying
the strictest settings to both. Each deployment has its own database, NATS stream and settings.

## GDPR (EU General Data Protection Regulation)

| Setting | Suggested value (our reading; confirm with counsel) |
|---|---|
| `AllowChanges` | `true`. Erasure has to be able to act on messages (Art. 17). |
| `MinimumKeepDays` | `0`, unless a separate legal record-keeping duty covers a category of data. Then use the number that duty requires. |
| `Erasure:Mode` | `anonymize` for general rooms. Use `delete` for DMs and small rooms, where anonymized messages can still identify a person (GDPR Recital 26). A request can override the mode per call. |
| `Retention:MessagesDays` | The host's own storage-limitation period (Art. 5(1)(e)). dotwire's default is unlimited, which is not a retention decision by itself. |

**What dotwire can't cover:** the lawful basis and any consent records; the Art. 30 record of
processing; the DPIA; transfers to third countries; backups the host stores outside dotwire's
encryption; copies held by the host's own webhook receivers beyond the erasure notices; deleting the
identity in the host's user table; and the response deadline for data subject requests.

## CCPA (California Consumer Privacy Act, as amended)

| Setting | Suggested value (our reading; confirm with counsel) |
|---|---|
| `AllowChanges` | `true`. |
| `MinimumKeepDays` | `0`, unless a specific exception in §1798.105(d) requires keeping data, such as a legal obligation. Keep only what that exception needs. |
| `Erasure:Mode` | `anonymize` for general rooms, `delete` for DMs and small rooms. |
| `Retention:MessagesDays` | The retention period the host discloses in its privacy notice (§1798.100(a)(3)). |

**What dotwire can't cover:** the response deadlines in §1798.130 and the request intake process;
verifying the identity of the requester; opt-out handling for any sale or sharing of personal
information (dotwire does not sell or share data, but the host's own integrations might); service
provider contracts with the host's webhook receivers; and the host's privacy notice itself (see
`docs/privacy-notice-template.md`).

## PIPL (Personal Information Protection Law of the People's Republic of China)

| Setting | Suggested value (our reading; confirm with counsel) |
|---|---|
| `AllowChanges` | `true`. |
| `MinimumKeepDays` | `0`, unless another law requires a longer period. |
| `Erasure:Mode` | `delete`. Anonymized messages may still count as personal information when they stay identifiable, so `delete` is the conservative choice. |
| `Retention:MessagesDays` | The shortest period that serves the stated purpose (our reading of Art. 19). Disclose the period to individuals. |

**What dotwire can't cover:** consent and notice to individuals (Art. 13 and 14); cross-border
transfer rules and any security assessment or standard contract they require (Art. 38 to 40); the
personal information protection impact assessment (Art. 55 and 56); data localization where a
regulator requires it; and the plaintext metadata columns (who messaged whom and when), which the
host must cover in its own assessment.

## FADP (Swiss Federal Act on Data Protection)

| Setting | Suggested value (our reading; confirm with counsel) |
|---|---|
| `AllowChanges` | `true`. |
| `MinimumKeepDays` | `0`, unless a Swiss legal record-keeping duty covers a category of data. Then use the number that duty requires. |
| `Erasure:Mode` | `anonymize` for general rooms, `delete` for DMs and small rooms, where anonymized messages can still point to a person. A request can override the mode per call. |
| `Retention:MessagesDays` | The shortest period that serves the purpose (Art. 6(4)). dotwire's unlimited default is not a by-default choice under Art. 7, so set a period and record why. |
| `AccessLog:Enabled` | `true` if the deployment processes sensitive data on a large scale or at high risk, because the Ordinance then asks for a log of reads and changes (our reading). Otherwise optional. |
| `AccessLog:RetentionDays` | At least `365`, the one-year minimum for that log (our reading). |

**What dotwire can't cover:** answering access requests within 30 days and adding the purpose,
retention period and recipients to the export (Art. 25); correction and objection (Art. 32); reporting
a breach to the FDPIC (Art. 24); the check on every destination abroad (Art. 16 and 17); the records
of processing, the impact assessment and a Swiss representative for a host based abroad (Art. 12, 22
and 14); a log kept apart from the database if the Ordinance applies; and backups outside dotwire's
encryption.

## LGPD (Brazil, Lei Geral de Proteção de Dados)

| Setting | Suggested value (our reading; confirm with counsel) |
|---|---|
| `AllowChanges` | `true`. |
| `MinimumKeepDays` | `0`, unless a legal or regulatory obligation requires keeping data (Art. 16 I). Then use the number that obligation requires. |
| `Erasure:Mode` | `delete`. Anonymized data is outside the law only if it cannot be traced back (Art. 12), and free text in chat often can be. |
| `Retention:MessagesDays` | The period after which the purpose has been met (Art. 15 and 16). Disclose it to people. |
| `AccessLog:Enabled` | Optional. The law has no access-log duty of its own; enable it if the host's security or incident policy needs it. |

**What dotwire can't cover:** the legal basis for processing (Art. 7) and any consent record; the
15-day answer to access requests (Art. 19); blocking and correction (Art. 18); notices to recipients
about blocks and corrections (Art. 18 §6); the report to the ANPD and the people affected after an
incident (Art. 48); international transfer grounds (Art. 33); the records of processing, the impact
report and the data protection officer (Art. 37, 38 and 41); children's data (Art. 14); and
backups outside dotwire's encryption.

## HIPAA (US Health Insurance Portability and Accountability Act)

| Setting | Suggested value (our reading; confirm with counsel) |
|---|---|
| `AllowChanges` | `true`. Individuals can ask for amendments to their records (45 CFR 164.526). |
| `MinimumKeepDays` | At least the documentation period in 45 CFR 164.316(b)(2), six years. State medical-record rules are often longer, so use the longest one that applies. |
| `Erasure:Mode` | `delete`. Health information in free text is unlikely to be de-identified by anonymization. |
| `Retention:MessagesDays` | At least `MinimumKeepDays`. The startup check enforces this. |
| `AccessLog:Enabled` | `true`. The access log is the record that §164.312(b) asks for (our reading). |
| `AccessLog:RetentionDays` | At least `2190`, the same six-year documentation period as `MinimumKeepDays`. The startup check enforces the floor. |

**What dotwire can't cover:** the Business Associate Agreement (dotwire is software, not a covered
entity or business associate, see COMPLIANCE.md); the administrative and physical safeguards; the
minimum-necessary standard; plaintext metadata (who messaged whom and when can itself be protected
health information); accounting of disclosures; breach notification; and state retention rules.

**Access logging is off by default.** dotwire's audit log records admin and compliance actions. With
`AccessLog:Enabled` set to `true`, the access log also records which member opened a room and when, and
which history pages each member was served. Our reading is that this is the record 45 CFR 164.312(b)
asks for, subject to the limitations in COMPLIANCE.md. With it off, which member opened a room or read
its messages is not recorded, and the audit-controls standard is only partly covered. Enable the access
log, or record access to chat by other means, or document chat access as unlogged in the risk analysis
(see COMPLIANCE.md, "Accepted limitations").

## SOC 2 (AICPA trust services criteria)

No setting is needed for SOC 2. CC7.2 asks for monitoring of system activity, and the access log is one
optional input to it (`AccessLog:Enabled`, with `AccessLog:RetentionDays` set by the host). See
COMPLIANCE.md, "Control mappings."

## MiFID II (EU Markets in Financial Instruments Directive II)

| Setting | Suggested value (our reading; confirm with counsel) |
|---|---|
| `AllowChanges` | `false`, so records cannot be altered during the keep period (Art. 16(7)). |
| `MinimumKeepDays` | `1826` (five years). Use `2556` (seven years) where the competent authority has asked for a longer period. |
| `Erasure:Mode` | `delete`. With `AllowChanges` set to `false`, the erasure waits until the rows reach end of life. |
| `Retention:MessagesDays` | At least `MinimumKeepDays`. |

**Optional:** `AccessLog:Enabled` records which member opened a room and read its history. The
record-keeping rules in this table do not require it (our reading); enable it if the host's own policy
needs it.

**What dotwire can't cover:** durable, non-rewritable storage and time-stamping of records; the
recording duties for telephone and electronic communications that may apply to chat used for trading
(confirm with counsel); retrieval within the regulator's deadlines; and the archive itself. The
`archiveAuditLog` SDK helper writes to a sink the host provides. It does not store records for the
host.

**The postsend webhook can drop messages.** It is best-effort: a full queue, repeated delivery
failures or a restart lose messages, and admin-injected messages skip it unless `IncludeAdminSends` is
set. An archive fed only by postsend will have holes. Page each room's history after the last archived
`seq` on a schedule, as a room member, and use postsend only as the fast path (see COMPLIANCE.md,
"Accepted limitations").

## SEC 17a-4 and FINRA 4511 (US broker-dealer record keeping)

| Setting | Suggested value (our reading; confirm with counsel) |
|---|---|
| `AllowChanges` | `false`. |
| `MinimumKeepDays` | `1095` (three years) for business communications under Rule 17a-4(b)(4). Use `2190` (six years) where FINRA Rule 4511(c) applies and no other period is specified. |
| `Erasure:Mode` | `delete`. The erasure waits until the rows reach end of life. |
| `Retention:MessagesDays` | At least `MinimumKeepDays`. |

**Optional:** `AccessLog:Enabled` records which member opened a room and read its history. It is not one
of the record-keeping items in this table (our reading); enable it if the host's own policy needs it.

**What dotwire can't cover:** the storage itself. Our reading is that Rule 17a-4(f) calls for
either WORM (write once, read many) media or an audit-trail alternative. A Postgres database is not
that storage. The host archives records to WORM or audit-trail storage, keeps the archive under its own
controls, and feeds it from scheduled history pages per room, with a postsend receiver as the fast path
and the `archiveAuditLog` SDK helper for the audit log. Postsend alone drops messages (see the MiFID II
section above). The undertaking and
designated-officer requirements, supervision and review duties, and any other FINRA obligations are
also the host's.

## Related

- [COMPLIANCE.md](../COMPLIANCE.md): control mappings, accepted limitations and host responsibilities.
- [privacy-notice-template.md](privacy-notice-template.md): a starting point for the privacy notice.
