# Synqra — Core Concepts

Synqra is an Event Sourcing + CQRS framework designed to maintain **virtual synchrony**
between distributed runtimes (for example: a WASM client and a server, Android or Desktop)
while preserving determinism, replayability, and clear causality boundaries.

This document defines the *core ideas* and *invariants* of Synqra. It is deliberately opinionated.
It intentionally avoids implementation details, APIs, and storage specifics.

If a concept is not defined here, it is not fundamental.

---

## 1. The Problem Synqra Solves

Modern applications are no longer single processes with a single source of truth.

They are:
- Distributed across client and server
- Temporarily disconnected
- Partially replicated
- Subject to race conditions, retries, and replays
- Subject to realtime syncronization between devices on a different platform

Traditional state mutation fails in this environment because:
- It erases history
- It obscures causality
- It breaks determinism under replay

Synqra addresses this by making **time and causality first-class concepts**. In the esence it is ES/CQRS framework with full support for Virtual Synchrony.

---

---

## 2. Events and Commands

Synqra is built on a strict separation between **intent** and **fact**.

### Command — Intent

A **Command** represents an *intention to change the system*.

Properties:
- Expresses *what someone wants to happen*
- May be accepted or rejected
- Has no guarantee of success
- Exists only in the present

Examples:
- `CreateOrder`
- `MoveObjects`
- `TransferFunds`

A Command is **not history** for the system (but may be displayed as such for a user).
If a Command can be rejected, it is not an Event.

---

### Event — Fact

An **Event** represents something that *has already happened* and was fully accepted.

Event Properties:
- Immutable
- Append-only
- Cannot be rejected
- Represents a historical fact
- Exists virtually forever (you define the snaphot and reconciliation boundaries)

Examples:
- `OrderCreated`
- `ObjectsMoved`
- `FundsTransferred`

Once an Event exists, the system must be able to explain *why it exists*, but never whether it should exist.

> If rejection is possible, it was not an Event.

---

## 3. Time, Ordering, and Causality

Events define **time** in Synqra.

The system does not ask:
> "What is the current state?"

It asks:
> "What sequence of facts led us here?"

Key principles:
- Order matters
- Causality must be explicit
- Replaying events must reproduce the same results

Synqra assumes:
- Events are ordered within a stream
- Multiple streams may interleave
- Causation chains must be traceable

Time is not wall-clock time.
Time is **event order**.

---

## 4. State Is a Derived Cache

Synqra does not treat state as truth.

State is a **materialized view** derived from events.

Properties:
- Can be rebuilt at any time
- Can be discarded without loss of information
- Exists for performance, not correctness

If deleting state breaks correctness, the system was not event-sourced.

---

## 5. Materialization

Materialization is the process of turning a stream of Events into a usable model.

Synqra supports multiple materialization strategies:
- In-memory (hot, fast, ephemeral): universal dictionaries / JsonObjects / Real POCO Models
- ReDis-style external in-memory with optional persistance
- Persistent projections (durable, rebuildable): SQL Database, Non-SQL, Browser Local Storage or IndexedDB
- API to build your own materializers

All materializations are:
- Deterministic
- Replayable
- Secondary to the Event Store

Materialization never creates new facts.
It only *interprets* existing ones.

---

## 6. Virtual Synchrony

Virtual synchrony means:

> Distributed runtimes observe the same sequence of Events
> and therefore converge to the same derived state.

Synqra does not require:
- Perfect connectivity
- Zero latency
- Lockstep execution

It requires:
- Ordered event delivery
- Deterministic replay
- Explicit boundaries for side effects

Temporary divergence is acceptable.
Permanent divergence is a bug.

---

## 7. Effects and Side Effects

An **Effect** represents interaction with the outside world.

Examples:
- UI Updates
- File IO
- Database writes
- Third-party APIs state update

Effects are:
- Triggered by Events or Commands
- Executed outside the pure domain
- Non-deterministic by nature

Therefore:
- Effects must be isolated
- Effects must be idempotent
- Effects must not mutate state directly

Effects may **emit new Commands or Events**, but never rewrite history.

---

## 8. Determinism and Replay

Given:
- The same initial state
- The same sequence of Commands

Synqra must produce:
- The same emitted Events
- The same derived state
- The same observable behavior (excluding external timing)

This property enables:
- Debugging via replay
- Time travel for the model state
- Offline-first clients and occasionaly sync
- Recovery after crashes

Non-determinism is allowed only inside Effects,
and must be explicitly acknowledged.

---

## 9. Invariants (Non-Negotiable Rules)

The following rules define Synqra’s correctness model:

- Events are immutable
- Events are append-only
- Commands are intentions, not facts
- State is derived, never authoritative
- Projections may be deleted and rebuilt
- Effects do not mutate state directly
- Clients do not emit Events directly
- Replay must be safe and deterministic

Breaking these rules may still produce working code,
but it is no longer Synqra.

---

## 10. Mental Model Summary

A useful way to think about Synqra:

1. Someone expresses intent (Command)
2. The system decides (validation / logic)
3. A fact is recorded (Event)
4. State is derived (Materialization)
5. The world is affected (Effect)
6. New intent may arise (Command)

This loop defines the life of the system.

---

## 11. What This Document Is Not

This document does **not**:
- Explain APIs
- Explain storage engines
- Explain performance tuning
- Explain deployment
- Explain frameworks or libraries

Those belong elsewhere.

This document explains **why the system exists and how it thinks**.

---

End of core concepts.