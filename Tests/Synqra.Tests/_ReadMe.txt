STAGE 1 - THERE IS NO COMMANDS! ONLY EVENTS!

	1. Event production
		a) Explicit event object creation (to where? Store? Processor? Processor is a store? Separate entrypoint?)
		b) Generation of events by changing the live model

	2. Event reducers (thinkg NGRX/Redux), StateProcessor.
		a) Always has one source of events stream
		b) Need to check a world state (merkle tree?) with 3rdparties

	3. Live model.
		a) Read-only mode when properties are not settable
		b) Editable mode when properties are settable and generate events on change

	4. Containers (Collections, a set of model data bound to specific container, user can have many, container is like DB or collection but per user, and it's a unit of replication, each container has it's event stream)

	5. Event streams - RX or IEnumerable or IAsyncEnumerable?

	6. Event syncronization
		a) Get only new events by event ID if this is master node (Stage 1 is only sync with master node)
		b) Push new events to master node.
		c) Subscribe to new events feed
		d) Master node feeds drives only confirmed, applied and sanitized events
		e) Stage2 - peer to peer sync with Version Vectors

Commands:
	It is an objects built and tracked by regular events, but it is associated with changes, so every set of event is pushed in scope of command.
	Commands should be a unit of syncronization, not events, because commands are smaller for Virtual Synchrony

Events:
	Are simpler, lower-level, e.g. property changed. Commands can do something that will generate bunch of events.

Problem:
	It feels more reliable and easier to maintain to use events in a storage. If CommandHandler would be changed, that might be devastating for the system. Events feels more reliable and easier. And it does not matter too much what to store, events or commands. But what matters is: what to sync... From virtual synchrony standpoint it is easier to sync commands, less comminucaiton for live updates and both system will generate seme events from it by EventHandler (reducer of commands to events).

Solution:
	1. Events are primary and mandatory


