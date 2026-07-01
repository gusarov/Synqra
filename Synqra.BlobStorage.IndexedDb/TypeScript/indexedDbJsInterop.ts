// One IndexedDB database per Synqra stream — the database name carries the stream id (see
// IndexedDbBlobStorage.ComposeDatabaseName on the C# side). A stream is the honest, replicated
// event copy every participant agrees on; a different user re-logging into the same browser gets
// a different stream, hence a different database, and never sees the previous user's records.
// Connections are therefore kept in a per-name map (not one module-global) so a graceful
// re-login switch can open the new stream's database while closing the old one.
const currentVersion = 1;
const separator = "§";

interface OpenDb {
    readonly db: IDBDatabase;
    readonly objectStoreName: string;
}

const openDatabases = new Map<string, Promise<OpenDb>>();

function getCompoundKey(storeName: string, keyText: string): string {
    return `${storeName}${separator}${keyText}`;
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
}

// Used by resync recovery — wipes every record for this storeName only, not the whole
// database (one per-stream database can hold records for more than one storeName, see
// getCompoundKey), by cursor-deleting every key under this storeName's prefix.
export async function clearStore(
    databaseName: string,
    objectStoreName: string,
    storeName: string
): Promise<void> {
    const opened = await initialize(databaseName, objectStoreName);

    return await new Promise<void>((resolve, reject) => {
        const transaction = opened.db.transaction(opened.objectStoreName, "readwrite");
        const collection = transaction.objectStore(opened.objectStoreName);
        const prefix = `${storeName}${separator}`;
        const request = collection.openKeyCursor(IDBKeyRange.lowerBound(prefix));

        request.onsuccess = function (event) {
            const cursor = (event.target as IDBRequest<IDBCursorWithValue | IDBCursor | null>).result;
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
