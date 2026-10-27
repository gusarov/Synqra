**XBS Serializer**
1. It should use JSON as a basic reference for capabilities. Everything that is possible in JSON should be possible in XBS, e.g. lack of knowledge about current type name, named properties, arrays, dictionaries, etc.
2. The goal is - to beat performance and data size, and many sacrifices is there to achieve that, e.g. simplicity.
3. It is context dependent, so it can compress data recently referred in context (use shorter names for known types, datetime diffs, node_ids for guid etc)

**Supported types**

| Type | Encoding | Type id in an object-typed position |
|---|---|---|
| `sbyte` `short` `int` `long` | zigzag varint | -2 |
| `byte` `ushort` `uint` `ulong` | varint | -3 |
| `string` | zero-terminated utf8, or an interned pointer | -4 |
| `Guid` | compressed (v7 against the stream time base, presence mask) | -5 |
| `float` / `double` | 4 / 8 bytes little-endian | -8 / -9 |
| `bool` | one byte: `0x00` false, `0x01` true | -12 |
| `byte[]` | varint length + 1 (0 = null), then the raw bytes | -13 |
| lists, `IDictionary<string, object>` / `<string, string>` | count, then items | -17..-32 / -10 / -11 |
| a mapped model | its generated binding | its `Map(...)` id |
| anything else | by name (Level 3, below) | 0 |

Nullable forms: `long?` and `ulong?` shift by one so that 0 is null; `bool?` follows them as an unsigned varint, 0 null, 1 false, 2 true; `float?` / `double?` use negative zero for null; `string?` and `Guid?` have their own null marks (a `Guid?` value is the `Guid`'s own encoding, its glyph included; null is glyph `0x03`). `byte[]` is always nullable and never a list of varints: a truncated payload throws a `FormatException` naming the position. A `bool` byte other than `0x00`/`0x01`, or a `bool?` value above 2, is corrupt data: decoding throws a `FormatException` naming the position and the value, and never reads "non-zero" as true. An object-typed position (a `Data` dictionary value, a heterogeneous list item, an `object` member) writes the type id first, so a boxed `true` is `17 01` and reads back as a `bool`.

**Schema Automation**
I want to have a system that tracks historical formats/schemas of binary serialization. It can be automated similar to EF migrations. Ideally the schema history should be right there where class is, so let's try [Schema] attribute.
It is important that this data should be stored in a repo! Let's also try to write the schema automatically to that attributes when schema drift is detected.

**IMPORTANT**
A schema refactoring is only needed to be able to read old streams and accept previous version of clients. It should be no more than 1-2 versions apart. Master should migrate streams to the next snapshot in order to upgrade data.

It should be safe to amend existing schema versions as long as it does not break compatibility. For example, adding a new nullable field is safe, you can do that by adding "fieldName string?" at the end of the schema

It is also critically important to understand that SBX is general purpose, so even if there is no new schema attribute or it has to talk to other party on older schema, it should be able to do so. Any unknown fields are added as named fields (by key string, not by key id, like properties in json), this way the data is always preserved, just probably not in the most efficient way yet.

**Types without a Map entry (Level 3)**
Any type with no `Map(...)` entry - a plain POCO or a `[SynqraModel]` nobody mapped - is written by name and fields: type id `0`, then `"Namespace.Type, Assembly"` (no version, so an assembly upgrade still resolves), then `name value` pairs closed by an empty name. The name is written only where a type id is: when the requested type is sealed the reader already knows it, so neither id nor name is written. List headers follow the same rule for their collection/element types. Mapping a type later only makes it smaller; the by-name form keeps reading. In DEBUG this path is a test protection: a type outside the allowlist in SBXSerializer throws "quaranteened" until it is approved there.

Under trimming/NativeAOT a by-name type must keep its constructor and properties: `[SynqraModel]` classes are annotated by the model generator, which also emits `[DynamicDependency]` on `SetCore` for every concrete class a model property refers to (e.g. `IList<BatchExpectation>`). A POCO reached any other way has to be rooted by hand.
