**XBS Serializer**
1. It should use JSON as a basic reference for capabilities. Everything that is possible in JSON should be possible in XBS, e.g. lack of knowledge about current type name, named properties, arrays, dictionaries, etc.
2. The goal is - to beat performance and data size, and many sacrifices is there to achieve that, e.g. simplicity.
3. It is context dependent, so it can compress data recently referred in context (use shorter names for known types, datetime diffs, node_ids for guid etc)

**Schema Automation**
I want to have a system that tracks historical formats/schemas of binary serialization. It can be automated similar to EF migrations. Ideally the schema history should be right there where class is, so let's try [Schema] attribute.
It is important that this data should be stored in a repo! Let's also try to write the schema automatically to that attributes when schema drift is detected.

**IMPORTANT**
A schema refactoring is only needed to be able to read old streams and accept previous version of clients. It should be no more than 1-2 versions apart. Master should migrate streams to the next snapshot in order to upgrade data.

It should be safe to amend existing schema versions as long as it does not break compatibility. For example, adding a new nullable field is safe, you can do that by adding "fieldName string?" at the end of the schema

It is also critically important to understand that SBX is general purpose, so even if there is no new schema attribute or it has to talk to other party on older schema, it should be able to do so. Any unknown fields are added as named fields (by key string, not by key id, like properties in json), this way the data is always preserved, just probably not in the most efficient way yet.