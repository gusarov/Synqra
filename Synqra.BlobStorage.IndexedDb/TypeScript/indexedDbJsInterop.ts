let synqraDbRequest: IDBOpenDBRequest;
let synqraDbResult: IDBDatabase;
let synqraDbPromise: Promise<void> | undefined;
let databaseName = "Synqra";
let collectionName = "blobs";
const currentVersion = 1;
const separator = "§";

function getCompoundKey(storeName: string, keyText: string): string {
    return `${storeName}${separator}${keyText}`;
}

export function initialize(dbName?: string, objectStoreName?: string): Promise<void> {
    if (dbName) {
        databaseName = dbName;
    }

    if (objectStoreName) {
        collectionName = objectStoreName;
    }

    if (!synqraDbPromise) {
        synqraDbPromise = new Promise<void>((resolve, reject) => {
            try {
                initializeCore(resolve, reject);
            } catch (error) {
                reject(error);
            }
        });
    }

    return synqraDbPromise;
}

function initializeCore(resolve: () => void, reject: (reason?: unknown) => void): void {
    synqraDbRequest = indexedDB.open(databaseName, currentVersion);

    synqraDbRequest.onsuccess = function () {
        synqraDbResult = synqraDbRequest.result;
        resolve();
    };

    synqraDbRequest.onerror = function () {
        reject(synqraDbRequest.error);
    };

    synqraDbRequest.onupgradeneeded = function () {
        const db = synqraDbRequest.result;
        if (!db.objectStoreNames.contains(collectionName)) {
            db.createObjectStore(collectionName, { keyPath: "compoundKey" });
        }
    };
}

export async function addBlob(storeName: string, keyText: string, blob: Uint8Array | number[]): Promise<void> {
    await initialize();
    const transaction = synqraDbResult.transaction(collectionName, "readwrite");
    const collection = transaction.objectStore(collectionName);

    collection.add({
        compoundKey: getCompoundKey(storeName, keyText),
        storeName,
        keyText,
        bin: blob
    });
}

export async function getBlob(storeName: string, keyText: string): Promise<Uint8Array | number[] | null> {
    await initialize();

    return await new Promise<Uint8Array | number[] | null>((resolve, reject) => {
        const transaction = synqraDbResult.transaction(collectionName, "readonly");
        const collection = transaction.objectStore(collectionName);
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
    storeName: string,
    fromKeyText?: string | null,
    fromExclusive = false,
    pageSize = 1024
): Promise<string[]> {
    await initialize();

    return await new Promise<string[]>((resolve, reject) => {
        const transaction = synqraDbResult.transaction(collectionName, "readonly");
        const collection = transaction.objectStore(collectionName);
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

export async function deleteByKey(storeName: string, keyText: string): Promise<void> {
    await initialize();
    const transaction = synqraDbResult.transaction(collectionName, "readwrite");
    const collection = transaction.objectStore(collectionName);
    collection.delete(getCompoundKey(storeName, keyText));
}
