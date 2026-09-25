# Network protocol development

The CardShopCoop protocol is a public extension surface. Other mods must be able to add messages
without modifying CardShopCoop or being added to a central allowlist.

## Registration and discovery

The wire contract types (`INetMessage`, `[NetworkMessage]`, `[MessageHandler]`, `Reliability`) and
the behaviour/session contract live in the `CardShopCoop.Api` assembly; core references it. External
mods reference the same assembly and are discovered from the BepInEx dependency graph (a mod that
soft-depends on `com.zwhit.cardshopcoop` is integrated automatically). There is no central allowlist.

- Provide supported public APIs for external assemblies to register DTOs, handlers, route
  policies, and transport/QoS metadata. Registration must return an explicit lifetime handle and
  support clean, idempotent unregistration.
- Automatic attribute discovery is a convenience, not the only integration path. It must support
  caller-supplied assemblies and must never assume all messages live in the CardShopCoop assembly.
- Preserve `Type.FullName` as the canonical identity for every registered DTO, including
  third-party DTOs. Do not add aliases or a second name source. During the named handshake both
  peers must exchange and exactly match the frozen, ordinally sorted `Type.FullName` catalog.
  After that validation, frames use the catalog's zero-based `ushort` index to reduce overhead;
  the numeric value is session-local shorthand derived only from `Type.FullName`.
- Fail fast on duplicate wire names, duplicate handlers, conflicting registrations, invalid
  descriptors, unsupported policy providers, and registration at unsafe lifecycle points.

## Declarative protocol contracts

- Never hardcode concrete DTO types into routing, connection-phase, authorization,
  transport-priority, atomic-transfer, coalescing, payload-limit, or work-budget lists.
- Define those semantics through immutable declarative descriptors and public policy interfaces.
  Generic protocol code must enforce the same contract for built-in and third-party messages.
- Keep wire identity, authorization, delivery/QoS, and work accounting as separate concerns. A
  DTO's CLR type name identifies it; identity must not imply permissions or transport behavior.
- Validate inbound and outbound traffic against the registered contract. External extensions may
  request supported behavior but may not bypass authentication, connection-state, payload,
  queue, memory, or work-budget limits.

## Lifecycle and safety

- Define whether registration is frozen while a session is active or implement an explicitly
  synchronized dynamic-registration protocol. Never allow one peer to silently change its
  message catalog mid-session.
- Handler registration and removal must be transactional. A failed registration must leave the
  previous registry intact, and unloading an extension must not leave callable delegates or stale
  transport policy entries behind.
- Unknown wire names, unauthorized phases/directions, malformed payloads, and exceeded resource
  limits must fail closed with contextual, rate-limited diagnostics.
- Preserve `Type.FullName` catalog identity when refactoring `MessageRegistry`, framing, or
  serialization. Module namespace moves intentionally change the wire contract unless both peers
  still use the same full type name. Never activate compact ids before exact catalog validation.
