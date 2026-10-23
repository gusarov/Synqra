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

interface OpenDb {
    readonly db: IDBDatabase;
    readonly objectStoreName: string;
}

const openDatabases = new Map<string, Promise<OpenDb>>();

function getCompoundKey(storeName: string, keyText: string): string {
    return `${storeName}${separator}${keyText}`;
}

// Every compound key of one storeName: "{storeName}§" up to the highest character.
function storeRange(storeName: string): IDBKeyRange {
    const prefix = `${storeName}${separator}`;
    return IDBKeyRange.bound(prefix, `${prefix}\uffff`);
}

// Resolves when the transaction commits (not when a request is merely queued), rejects when it fails or aborts.
function completion(transaction: IDBTransaction): Promise<void> {
    return new Promise<void>((resolve, reject) => {
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error ?? new Error("IndexedDB transaction aborted"));
    });
}

export function initialize(databaseName: string, objectStoreName: string): Promise<OpenDb> {
    let existing = openDatabases.get(databaseName);
    if (!existing) {
        existing = openCore(databaseName, objectStoreName);
        openDatabases.set(databaseName, existing);
    }
    return existing;
}

function openCore(databaseName: string, objectStoreName: string): Promise<OpenDb> {
    return new Promise<OpenDb>((resolve, reject) => {
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
        } catch (error) {
            openDatabases.delete(databaseName);
            reject(error);
        }
    });
}

// Gracefully stop using a stream's database — closes the connection and drops it from the map so
// the next initialize() reopens it fresh. Used on re-login to release the previous stream before
// switching to the next one.
export async function closeDatabase(databaseName: string): Promise<void> {
    const existing = openDatabases.get(databaseName);
    if (!existing) {
        return;
    }

    openDatabases.delete(databaseName);
    try {
        const opened = await existing;
        opened.db.close();
    } catch {
        // A database that never finished opening has nothing to close.
    }
}

export async function addBlob(
    databaseName: string,
    objectStoreName: string,
    storeName: string,
    keyText: string,
    blob: Uint8Array | number[],
    json?: string | null
): Promise<void> {
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
export async function getTailKey(
    databaseName: string,
    objectStoreName: string,
    storeName: string
): Promise<string | null> {
    const opened = await initialize(databaseName, objectStoreName);

    return await new Promise<string | null>((resolve, reject) => {
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
export async function appendAtTail(
    databaseName: string,
    objectStoreName: string,
    storeName: string,
    keyText: string,
    blob: Uint8Array | number[],
    json: string | null | undefined,
    uniqueTag: string,
    expectedTail: string | null | undefined
): Promise<string> {
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
        } else {
            const tailRequest = collection.openKeyCursor(storeRange(storeName), "prev");
            tailRequest.onsuccess = function () {
                const cursor = tailRequest.result;
                const tail = cursor ? String(cursor.primaryKey).substring(storeName.length + separator.length) : null;
                if (tail !== (expectedTail ?? null)) {
                    outcome = "tailMoved";
                } else {
                    collection.add({ compoundKey: getCompoundKey(storeName, keyText), storeName, keyText, bin: blob, json: json ?? undefined });
                    collection.add({ compoundKey: tagKey, storeName: tagStoreName, keyText: uniqueTag, bin: new Uint8Array(0), json: keyText });
                }
            };
        }
    };

    await completion(transaction);
    return outcome;
}

export async function getBlob(
    databaseName: string,
    objectStoreName: string,
    storeName: string,
    keyText: string
): Promise<Uint8Array | number[] | null> {
    const opened = await initialize(databaseName, objectStoreName);

    return await new Promise<Uint8Array | number[] | null>((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const collection = transaction.objectStore(opened.objectStoreName);
        const request = collection.get(getCompoundKey(storeName, keyText));

        request.onsuccess = function () {
            const result = request.result as { bin?: Uint8Array | number[] | null; Bin?: Uint8Array | number[] | null } | undefined;
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

export async function getKeys(
    databaseName: string,
    objectStoreName: string,
    storeName: string,
    fromKeyText?: string | null,
    fromExclusive = false,
    pageSize = 1024
): Promise<string[]> {
    const opened = await initialize(databaseName, objectStoreName);

    return await new Promise<string[]>((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const collection = transaction.objectStore(opened.objectStoreName);
        const prefix = `${storeName}${separator}`;
        const startKey = fromKeyText !== undefined && fromKeyText !== null
            ? getCompoundKey(storeName, fromKeyText)
            : prefix;
        const request = collection.openKeyCursor(IDBKeyRange.lowerBound(startKey, !!fromExclusive && fromKeyText !== undefined && fromKeyText !== null));
        const keys: string[] = [];

        request.onsuccess = function (event) {
            const cursor = (event.target as IDBRequest<IDBCursorWithValue | IDBCursor | null>).result;
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

// One readonly transaction, two requests (getAllKeys, getAll): up to pageSize records of the store after the key, each key
// text with its bytes. A cursor stepped once per record; this is one round trip per page.
export async function getBlobPage(
    databaseName: string,
    objectStoreName: string,
    storeName: string,
    fromKeyText?: string | null,
    fromExclusive = false,
    pageSize = 256
): Promise<{ keys: string[]; blobs: Uint8Array[] }> {
    const opened = await initialize(databaseName, objectStoreName);

    return await new Promise<{ keys: string[]; blobs: Uint8Array[] }>((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const collection = transaction.objectStore(opened.objectStoreName);
        const prefix = `${storeName}${separator}`;
        const hasFrom = fromKeyText !== undefined && fromKeyText !== null;
        const startKey = hasFrom ? getCompoundKey(storeName, fromKeyText as string) : prefix;
        const range = IDBKeyRange.bound(startKey, `${prefix}￿`, !!fromExclusive && hasFrom); // the store's own range, as storeRange
        const keysRequest = collection.getAllKeys(range, pageSize);
        const valuesRequest = collection.getAll(range, pageSize);

        transaction.oncomplete = function () {
            const compoundKeys = keysRequest.result as IDBValidKey[];
            const values = valuesRequest.result as { bin?: Uint8Array | number[] | null; Bin?: Uint8Array | number[] | null }[];
            const keys: string[] = [];
            const blobs: Uint8Array[] = [];
            for (let i = 0; i < compoundKeys.length; i++) {
                const bin = values[i]?.bin ?? values[i]?.Bin ?? [];
                keys.push(String(compoundKeys[i]).substring(prefix.length));
                blobs.push(bin instanceof Uint8Array ? bin : new Uint8Array(bin)); // records written as number[] come back as bytes too
            }
            resolve({ keys, blobs });
        };

        transaction.onerror = function () {
            reject(transaction.error);
        };
    });
}

// One readonly transaction, one request (getAllKeys): up to pageSize key texts of the store after the key, no values read.
export async function getKeyPage(
    databaseName: string,
    objectStoreName: string,
    storeName: string,
    fromKeyText?: string | null,
    fromExclusive = false,
    pageSize = 4096
): Promise<string[]> {
    const opened = await initialize(databaseName, objectStoreName);

    return await new Promise<string[]>((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readonly");
        const collection = transaction.objectStore(opened.objectStoreName);
        const prefix = `${storeName}${separator}`;
        const hasFrom = fromKeyText !== undefined && fromKeyText !== null;
        const startKey = hasFrom ? getCompoundKey(storeName, fromKeyText as string) : prefix;
        const request = collection.getAllKeys(IDBKeyRange.bound(startKey, `${prefix}￿`, !!fromExclusive && hasFrom), pageSize);

        request.onsuccess = function () {
            resolve((request.result as IDBValidKey[]).map(key => String(key).substring(prefix.length)));
        };

        request.onerror = function () {
            reject(request.error);
        };
    });
}

export async function deleteByKey(
    databaseName: string,
    objectStoreName: string,
    storeName: string,
    keyText: string
): Promise<void> {
    const opened = await initialize(databaseName, objectStoreName);
    const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
    const collection = transaction.objectStore(opened.objectStoreName);
    collection.delete(getCompoundKey(storeName, keyText));
    await completion(transaction);
}

// Used by resync recovery — wipes every record for this storeName only, not the whole
// database (one per-stream database can hold records for more than one storeName, see
// getCompoundKey), by deleting this storeName's key range and its tags.
export async function clearStore(
    databaseName: string,
    objectStoreName: string,
    storeName: string
): Promise<void> {
    const opened = await initialize(databaseName, objectStoreName);
    const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
    const collection = transaction.objectStore(opened.objectStoreName);
    collection.delete(storeRange(storeName));
    collection.delete(storeRange(`${storeName}${tagSuffix}`)); // its tags too, or a resync could not append the same events again
    await completion(transaction);
}
