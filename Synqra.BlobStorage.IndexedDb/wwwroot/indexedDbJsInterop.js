// One IndexedDB database per Synqra stream — the database name carries the stream id (see
// IndexedDbBlobStorage.ComposeDatabaseName on the C# side). A stream is the honest, replicated
// event copy every participant agrees on; a different user re-logging into the same browser gets
// a different stream, hence a different database, and never sees the previous user's records.
// Connections are therefore kept in a per-name map (not one module-global) so a graceful
// re-login switch can open the new stream's database while closing the old one.
const currentVersion = 1;
const separator = "§";
const openDatabases = new Map();
function getCompoundKey(storeName, keyText) {
    return `${storeName}${separator}${keyText}`;
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
export async function deleteByKey(databaseName, objectStoreName, storeName, keyText) {
    const opened = await initialize(databaseName, objectStoreName);
    const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
    const collection = transaction.objectStore(opened.objectStoreName);
    collection.delete(getCompoundKey(storeName, keyText));
}
// Used by resync recovery — wipes every record for this storeName only, not the whole
// database (one per-stream database can hold records for more than one storeName, see
// getCompoundKey), by cursor-deleting every key under this storeName's prefix.
export async function clearStore(databaseName, objectStoreName, storeName) {
    const opened = await initialize(databaseName, objectStoreName);
    return await new Promise((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
        const collection = transaction.objectStore(opened.objectStoreName);
        const prefix = `${storeName}${separator}`;
        const request = collection.openKeyCursor(IDBKeyRange.lowerBound(prefix));
        request.onsuccess = function (event) {
            const cursor = event.target.result;
            if (!cursor) {
                resolve();
                return;
            }
            const compoundKey = String(cursor.primaryKey);
            if (!compoundKey.startsWith(prefix)) {
                resolve();
                return;
            }
            collection.delete(compoundKey);
            cursor.continue();
        };
        request.onerror = function () {
            reject(request.error);
        };
    });
}
//# sourceMappingURL=indexedDbJsInterop.js.map