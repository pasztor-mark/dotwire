# Privacy notice template

> **Starting point, not legal advice.** This template lists the sections a privacy notice for a
> dotwire-based chat service usually needs. Fill in every bracketed field, delete what does not apply,
> and have counsel review the result before you publish it. The legal readings in COMPLIANCE.md are
> our reading; confirm them with counsel too.

## 1. Who we are

[Organization name], [registered address]. Contact for privacy questions: [email or form].
Data protection contact, if one is appointed: [name, contact]. [Brazil: the data protection officer
(encarregado), whose identity and contact the LGPD requires you to publish. Switzerland: the Swiss
representative, if you are based abroad.]

## 2. What data we process

- **Chat messages.** The text, [attachments,] and other content you send in a room. dotwire encrypts
  message content (AES-256-GCM) before it is stored.
- **Message details.** Which room a message belongs to, who sent it, when, and its position in the
  room. These are stored in plain form so that the service can run. dotwire does not encrypt them.
- **Account details.** [Identifier from our identity provider, display name, role in the service].
  dotwire stores an opaque user identifier (`sub`) and the roles and memberships we grant. We keep
  the names and contact details in [our user system].
- **Access records** (only if we keep an access log, and delete this line otherwise). Which rooms you
  opened and when, and which message ranges were served to you, recorded by your user identifier. They
  hold no message content and are kept for [period].
- **Operational data.** Service metrics that carry no personal identifiers, and [any other logs we keep,
  with their content and period].

## 3. Why we process it and on what basis

[Purpose 1, for example: to deliver messages between members of a room.] Basis: [contract / legitimate
interests / consent / legal obligation, as applicable].

[Purpose 2, for example: to keep records we must keep by law.] Basis: [legal obligation].

## 4. How long we keep it

- Messages: [number] days, then they are deleted automatically. [Describe any longer period for
  records a law requires, and which records those are.]
- Account details: [period after the account ends].
- Audit records of administrative actions, including the opaque user identifier: [period]. These
  records do not contain message content.

## 5. Who receives it

- Our service providers that host or operate the service: [list, with their role and location].
- Systems that receive our webhooks, such as moderation, archive or search services: [list].
- Authorities, where the law requires it: [describe].

## 6. Your rights and how erasure works

You can ask us for:

- **Access and a copy.** We provide your messages, memberships, roles and, if section 2 lists access records, those records in a machine-readable form.
  [Describe how to request it and the response time.]
- **Erasure.** When we erase your account, we either:
  - **anonymize** your messages, so they are attributed to a placeholder sender instead of you, or
  - **delete** your messages.

  We delete your account identity in our user system. Some data cannot be erased straight away: a legal
  hold can block erasure of the data it covers until the hold ends, and a required keep period can do the
  same until it ends. We then erase that data automatically. Some audit records keep an opaque
  identifier after erasure, because we must keep a record of what was done. [Adjust to the mode you use,
  and state how you treat DMs and small rooms.]
- **Restriction, blocking and objection.** [Describe how to ask us to limit, block or stop a use of
  your data, and what we do: for example, removing you from rooms.]
- **Correction.** [Describe how to ask us to correct data about you. Chat messages cannot be edited, so
  say what you do instead, for example replacing the message.]
- **Portability.** [Describe the export format and how to get it.]
- **Complaints.** [Name of the supervisory authority and how to contact it: for example the data
  protection authority in your country, the Swiss FDPIC, or the Brazilian ANPD.]

We respond within [period]. [GDPR: one month. FADP: 30 days. LGPD: 15 days for the full answer. Use the
shortest period that applies.]

## 7. Automated decisions

[State whether any decision about you is made automatically, for example by moderation, and what
it does. Describe how you can ask for a human review, if the law requires it. If none, say so.]

## 8. Transfers outside [your country or region]

[Describe any transfer outside your region, the safeguard used, and where to read more. If none,
say so.]

## 9. Security

[Message content is encrypted at rest. Connections use TLS. confirm for your deployment] Access to administrative functions is
limited to named administrators, and their actions are logged. [Add any other measures you run.]

## 10. Changes to this notice

[How we tell people when this notice changes, and the date of the last update.]
