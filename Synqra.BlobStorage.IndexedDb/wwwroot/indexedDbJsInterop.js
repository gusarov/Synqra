// One IndexedDB database per Synqra stream — the database name carries the stream id (see
// IndexedDbBlobStorage.ComposeDatabaseName on the C# side). A stream is the honest, replicated
// event copy every participant agrees on; a different user re-logging into the same browser gets
// a different stream, hence a different database, and never sees the previous user's records.
// Connections are therefore kept in a per-name map (not one module-global) so a graceful
// re-login switch can open the new stream's database while closing the old one.
const currentVersion = 1;
const separator = "§";
// A store's unique tags (an event's EventId) live beside its records under "{storeName}#tag", so the store's own
// key range (and getKeys) never sees them.
const tagSuffix = "#tag";
const openDatabases = new Map();
function getCompoundKey(storeName, keyText) {
    return `${storeName}${separator}${keyText}`;
}
// Every compound key of one storeName: "{storeName}§" up to the highest character.
function storeRange(storeName) {
    const prefix = `${storeName}${separator}`;
    return IDBKeyRange.bound(prefix, `${prefix}\uffff`);
}
// Resolves when the transaction commits (not when a request is merely queued), rejects when it fails or aborts.
function completion(transaction) {
    return new Promise((resolve, reject) => {
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error ?? new Error("IndexedDB transaction aborted"));
    });
}
export function initialize(databaseName, objectStoreName) {
    let existing = openDatabases.get(databaseName);
    if (!existing) {
        existing = openCore(databaseName, objectStoreName);
        openDatabases.set(databaseName, existing);
    }
    return existing;
}
function openCore(databaseName, objectStoreName) {
    return new Promise((resolve, reject) => {
        try {
            const request = indexedDB.open(databaseName, currentVersion);
            request.onsuccess = function () {
                resolve({ db: request.result, objectStoreName });
            };
            request.onerror = function () {
                openDatabases.delete(databaseName);
                reject(request.error);
            };
            request.onupgradeneeded = function () {
                const db = request.result;
                if (!db.objectStoreNames.contains(objectStoreName)) {
                    db.createObjectStore(objectStoreName, { keyPath: "compoundKey" });
                }
            };
        }
        catch (error) {
            openDatabases.delete(databaseName);
            reject(error);
        }
    });
}
// Gracefully stop using a stream's database — closes the connection and drops it from the map so
// the next initialize() reopens it fresh. Used on re-login to release the previous stream before
// switching to the next one.
export async function closeDatabase(databaseName) {
    const existing = openDatabases.get(databaseName);
    if (!existing) {
        return;
    }
    openDatabases.delete(databaseName);
    try {
        const opened = await existing;
        opened.db.close();
    }
    catch {
        // A database that never finished opening has nothing to close.
    }
}
export async function addBlob(databaseName, objectStoreName, storeName, keyText, blob, json) {
    const opened = await initialize(databaseName, objectStoreName);
    const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
    const collection = transaction.objectStore(opened.objectStoreName);
    collection.add({
        compoundKey: getCompoundKey(storeName, keyText),
        storeName,
        keyText,
        bin: blob,
        // Optional human-readable mirror of the same record, alongside `bin` — not a
        // separate store/key. Only present when the caller asked for it (see
        // IJsonMirrorBlobStorage/IndexedDbBlobStorageOptions.PopulateDebugJson).
        json: json ?? undefined
    });
    await completion(transaction); // a failed write (a duplicate key) surfaces instead of vanishing
}
// The store's last key, or null when it is empty.
export async function getTailKey(databaseName, objectStoreName, storeName) {
    const opened = await initialize(databaseName, objectStoreName);
    return await new Promise((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const request = transaction.objectStore(opened.objectStoreName).openKeyCursor(storeRange(storeName), "prev");
        request.onsuccess = function () {
            const cursor = request.result;
            resolve(cursor ? String(cursor.primaryKey).substring(storeName.length + separator.length) : null);
        };
        request.onerror = function () {
            reject(request.error);
        };
    });
}
// One readwrite transaction: IndexedDB runs readwrite transactions on a store one at a time, across tabs too, so
// the check and the write cannot interleave with another writer's. Returns "appended", "duplicate" (the tag is
// stored already) or "tailMoved" (the store's last key is not expectedTail; null = empty), writing only on "appended".
export async function appendAtTail(databaseName, objectStoreName, storeName, keyText, blob, json, uniqueTag, expectedTail) {
    const opened = await initialize(databaseName, objectStoreName);
    const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
    const collection = transaction.objectStore(opened.objectStoreName);
    const tagStoreName = `${storeName}${tagSuffix}`;
    const tagKey = getCompoundKey(tagStoreName, uniqueTag);
    let outcome = "appended";
    const tagRequest = collection.getKey(tagKey);
    tagRequest.onsuccess = function () {
        if (tagRequest.result !== undefined) {
            outcome = "duplicate";
        }
        else {
            const tailRequest = collection.openKeyCursor(storeRange(storeName), "prev");
            tailRequest.onsuccess = function () {
                const cursor = tailRequest.result;
                const tail = cursor ? String(cursor.primaryKey).substring(storeName.length + separator.length) : null;
                if (tail !== (expectedTail ?? null)) {
                    outcome = "tailMoved";
                }
                else {
                    collection.add({ compoundKey: getCompoundKey(storeName, keyText), storeName, keyText, bin: blob, json: json ?? undefined });
                    collection.add({ compoundKey: tagKey, storeName: tagStoreName, keyText: uniqueTag, bin: new Uint8Array(0), json: keyText });
                }
            };
        }
    };
    await completion(transaction);
    return outcome;
}
export async function getBlob(databaseName, objectStoreName, storeName, keyText) {
    const opened = await initialize(databaseName, objectStoreName);
    return await new Promise((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const collection = transaction.objectStore(opened.objectStoreName);
        const request = collection.get(getCompoundKey(storeName, keyText));
        request.onsuccess = function () {
            const result = request.result;
            if (!result) {
                resolve(null);
                return;
            }
            resolve(result.bin ?? result.Bin ?? null);
        };
        request.onerror = function () {
            reject(request.error);
        };
    });
}
export async function getKeys(databaseName, objectStoreName, storeName, fromKeyText, fromExclusive = false, pageSize = 1024) {
    const opened = await initialize(databaseName, objectStoreName);
    return await new Promise((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const collection = transaction.objectStore(opened.objectStoreName);
        const prefix = `${storeName}${separator}`;
        const startKey = fromKeyText !== undefined && fromKeyText !== null
            ? getCompoundKey(storeName, fromKeyText)
            : prefix;
        const request = collection.openKeyCursor(IDBKeyRange.lowerBound(startKey, !!fromExclusive && fromKeyText !== undefined && fromKeyText !== null));
        const keys = [];
        request.onsuccess = function (event) {
            const cursor = event.target.result;
            if (!cursor) {
                resolve(keys);
                return;
            }
            const compoundKey = String(cursor.primaryKey);
            if (!compoundKey.startsWith(prefix)) {
                resolve(keys);
                return;
            }
            keys.push(compoundKey.substring(prefix.length));
            if (keys.length >= pageSize) {
                resolve(keys);
                return;
            }
            cursor.continue();
        };
        request.onerror = function () {
            reject(request.error);
        };
    });
}
// One page of records in one readonly transaction, packed into a single buffer so the whole page crosses to .NET as one
// byte array: per record a 4-byte little-endian key length, the key's UTF-8, a 4-byte blob length, the blob. Reading
// a log with getKeys + one getBlob per key costs a transaction per record (minutes for a 14k-event browser log).
export async function getBlobsPage(databaseName, objectStoreName, storeName, fromKeyText, fromExclusive = false, pageSize = 1024) {
    const opened = await initialize(databaseName, objectStoreName);
    return await new Promise((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const collection = transaction.objectStore(opened.objectStoreName);
        const prefix = `${storeName}${separator}`;
        const hasFrom = fromKeyText !== undefined && fromKeyText !== null;
        const startKey = hasFrom ? getCompoundKey(storeName, fromKeyText) : prefix;
        const request = collection.openCursor(IDBKeyRange.lowerBound(startKey, !!fromExclusive && hasFrom));
        const encoder = new TextEncoder();
        const parts = [];
        let total = 0;
        const finish = () => {
            const packed = new Uint8Array(total);
            const view = new DataView(packed.buffer);
            let at = 0;
            for (const part of parts) {
                view.setUint32(at, part.key.length, true);
                packed.set(part.key, at + 4);
                at += 4 + part.key.length;
                view.setUint32(at, part.blob.length, true);
                packed.set(part.blob, at + 4);
                at += 4 + part.blob.length;
            }
            resolve(packed);
        };
        request.onsuccess = function (event) {
            const cursor = event.target.result;
            if (!cursor) {
                finish();
                return;
            }
            const compoundKey = String(cursor.primaryKey);
            if (!compoundKey.startsWith(prefix)) {
                finish();
                return;
            }
            const value = cursor.value;
            const bin = value ? (value.bin ?? value.Bin ?? null) : null;
            const blob = bin instanceof Uint8Array ? bin : bin ? new Uint8Array(bin) : new Uint8Array(0);
            const key = encoder.encode(compoundKey.substring(prefix.length));
            parts.push({ key, blob });
            total += 8 + key.length + blob.length;
            if (parts.length >= pageSize) {
                finish();
                return;
            }
            cursor.continue();
        };
        request.onerror = function () {
            reject(request.error);
        };
    });
}
// One readonly transaction, one request (getAllKeys): up to pageSize key texts of the store after the key, no values read.
export async function getKeyPage(databaseName, objectStoreName, storeName, fromKeyText, fromExclusive = false, pageSize = 4096) {
    const opened = await initialize(databaseName, objectStoreName);
    return await new Promise((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const collection = transaction.objectStore(opened.objectStoreName);
        const prefix = `${storeName}${separator}`;
        const hasFrom = fromKeyText !== undefined && fromKeyText !== null;
        const startKey = hasFrom ? getCompoundKey(storeName, fromKeyText) : prefix;
        const request = collection.getAllKeys(IDBKeyRange.bound(startKey, `${prefix}￿`, !!fromExclusive && hasFrom), pageSize);
        request.onsuccess = function () {
            resolve(request.result.map(key => String(key).substring(prefix.length)));
        };
        request.onerror = function () {
            reject(request.error);
        };
    });
}
export async function deleteByKey(databaseName, objectStoreName, storeName, keyText) {
    const opened = await initialize(databaseName, objectStoreName);
    const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
    const collection = transaction.objectStore(opened.objectStoreName);
    collection.delete(getCompoundKey(storeName, keyText));
    await completion(transaction);
}
// Used by resync recovery — wipes every record for this storeName only, not the whole
// database (one per-stream database can hold records for more than one storeName, see
// getCompoundKey), by deleting this storeName's key range and its tags.
export async function clearStore(databaseName, objectStoreName, storeName) {
    const opened = await initialize(databaseName, objectStoreName);
    const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
    const collection = transaction.objectStore(opened.objectStoreName);
    collection.delete(storeRange(storeName));
    collection.delete(storeRange(`${storeName}${tagSuffix}`)); // its tags too, or a resync could not append the same events again
    await completion(transaction);
}
//# sourceMappingURL=indexedDbJsInterop.js.map