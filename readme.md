Synqra

[![Build Status](https://dev.azure.com/xkit/Synqra/_apis/build/status%2FBuild?branchName=master)](https://dev.azure.com/xkit/Synqra/_build/latest?definitionId=103&branchName=master)

1) State management framework. It brings abstractions and mechanisms to provide you a data model, events and commands

2) it is ES CQRS framework

3) It is a framework for building event-sourced applications

4) Abstractions for event storage & few default storages

5) Notifications by Reactive Framework & Object Model events. Models are WPF-bindable

6) Expressions for building declarative view-models out of models

7) A glue between POCO model and Commands & Query

========================= REQUIREMENTS ======================
1) A project should accept and fully support the System.Text.Json serialization. It might be used for storage, but it will be used for intermediate state processing thigns, like snapshoting before deletion.
1.2) JSON should be easy, safe and reasonable for v1. But going forward it totally make sense to pass state by
1.2.1) my own binary state for performance
1.2.2) direct object to object mappers to allow deepclone via serialization API without extra data copy


========================= ARCHITECTURE ======================

There are two main entities:
1) Events
2) Commands

Events are the bare bones of Synqra. Every even is a smallest state changing entity. It can be as basic as "Property Subject changed to ASD", to as complex as any custom event you define. You can define custom events to improve efficiency or customize the solution.
Commands are actions that sent to CQRS system in order to introduce a changeset of events.

Virtual Synchrony.
Every command is processed individually and parallely both at the client side and server side, to avoid any network delay and to work in offline. They should give equal change set and as a result the model state should match.

IAppedStorage - is storage abstractions that is suitable for append log, like events
IProjection - is abstraction that can be used to build projections, e.g. read models, inmemory or indatabase without any guarantees about real objects
IObjectStore - it abstraction that manages real object instances, caches them, tracks changes, propageate automatic commands and so on

========================= API ===============================
It all starts from IMyStoreContext : IStoreContext

========================= EVENTS ============================
0) All events have a base types IEvent and BaseEvent
1) There is ObjectCreated, ObjectDeleted, ObjectProperyChanged
2) There are events for object references management, e.g. ObjectMove { ParentOld, ParentNew } // this probably needs to be generalized to links

========================= COMMANDS ==========================

0) All commands have a base types ICommand and BaseCommand
1) There is CreateObject, DeleteObject, ChangeProperty
2) Commands are collected in a special collection "Commands" where user can refer to a command by ID in order to undo or redo it

========================= COMMUNICATION ==========================
1) Protobuf over WebSocket
2) JSON over WebSocket
3) SignalR died and removed because it failed in Native AOT scenario
4) It is communication, not just serialziation. So, some contracts should be defined in negotiation frames. E.g. endianess, compression, serialization format, protocol version etc.

1. Before any successful exchange of commands or events, client should send a negotiation "HELLO" frame

8 bytes magic: AD790DD0594578[01] - this represents Syncra [V1] protocol for Little Endian
