let synqraDb;
let synqraDbResult;
let synqraDbPromise;
let databaseName = "Synqra";
let collectionName = "blobs";
const currentVersion = 1;
const separator = "§";

function getCompoundKey(storeName, keyText) {
    return `${storeName}${separator}${keyText}`;
}

export function test(message) {
    return message + " pass";
}

export function initialize(dbName, objectStoreName) {
    if (dbName) {
        databaseName = dbName;
    }
    if (objectStoreName) {
        collectionName = objectStoreName;
    }

    if (!synqraDbPromise) {
        synqraDbPromise = new Promise((resolve, reject) => {
            try {
                initializeCore(resolve, reject);
            } catch (error) {
                reject(error);
            }
        });
    }
    return synqraDbPromise;
}

function initializeCore(resolve, reject) {
    synqraDb = indexedDB.open(databaseName, currentVersion);
    synqraDb.onsuccess = function () {
        synqraDbResult = synqraDb.result;
        resolve();
    };
    synqraDb.onerror = function () {
        reject(synqraDb.error);
    };
    synqraDb.onupgradeneeded = function () {
        const db = synqraDb.result;
        if (!db.objectStoreNames.contains(collectionName)) {
            db.createObjectStore(collectionName, { keyPath: "compoundKey" });
        }
    };
}

export async function addBlob(storeName, keyText, blob) {
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

export async function getBlob(storeName, keyText) {
    await initialize();
    return await new Promise((resolve, reject) => {
        const transaction = synqraDbResult.transaction(collectionName, "readonly");
        const collection = transaction.objectStore(collectionName);
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

export async function getKeys(storeName, fromKeyText, fromExclusive, pageSize) {
    await initialize();

    return await new Promise((resolve, reject) => {
        const transaction = synqraDbResult.transaction(collectionName, "readonly");
        const collection = transaction.objectStore(collectionName);
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

            const compoundKey = cursor.primaryKey;
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

export async function deleteByKey(storeName, keyText) {
    await initialize();
    const transaction = synqraDbResult.transaction(collectionName, "readwrite");
    const collection = transaction.objectStore(collectionName);
    collection.delete(getCompoundKey(storeName, keyText));
}
