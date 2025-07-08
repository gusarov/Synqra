Synqra

1) State management framework. It brings abstractions and mechanisms to provide you a data model, events and commands

2) it is ES CQRS framework

3) It is a framework for building event-sourced applications

4) Abstractions for event storage & few default storages

5) Notifications by Reactive Framework & Object Model events. Models are WPF-bindable

6) Expressions for building declarative view-models out of models

7) A glue between POCO model and Commands & Query

========================= REQUIREMENTS ======================
1) A project should accept and fully support the System.Text.Json serialization. It might be used for storage, but it will be used for intermediate state processing thigns, like snapshoting before deletion.


========================= ARCHITECTURE ======================

There are two main entities:
1) Events
2) Commands

Events are the bare bones of Synqra. Every even is a smallest state changing entity. It can be as basic as "Property Subject changed to ASD", to as complex as any custom event you define. You can define custom events to improve efficiency or customize the solution.
Commands are actions that sent to CQRS system in order to introduce a changeset of events.

Virtual Synchrony.
Every command is processed individually and parallely both at the client side and server side, to avoid any network delay and to work in offline. They should give equal change set and as a result the model state should match.

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
