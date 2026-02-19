const DATABASE_NAME = "Synqra";
const CURRENT_VERSION = 1;

const collectionName = "events";

let synqraDb;

export function test(message) {
    return message + " pass";
}

export function initialize() {
    synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
    synqraDb.onupgradeneeded = function (event) {
        const db = synqraDb.result;
        const oldVersion = event.oldVersion;
        console.warn('Synqra Indexed DB Upgrade needed from v' + oldVersion);

        if (oldVersion < 1) {
            // const objStore = db.createObjectStore("events", { keyPath: "seq_id", autoIncrement: true });
            // objStore.add({ _sys: 'ObjectStoreCreated' });
            const objStore = db.createObjectStore(collectionName);
        }
    }
}

/*
export function set(collectionName, value) {
    const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);

    synqraDb.onsuccess = function () {
        const transaction = synqraDb.result.transaction(collectionName, "readwrite");
        const collection = transaction.objectStore(collectionName)
        collection.put(value);
    }
}

export async function get(collectionName, id) {
    const request = new Promise((resolve) => {
        const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
        synqraDb.onsuccess = function () {
            const transaction = synqraDb.result.transaction(collectionName, "readonly");
            const collection = transaction.objectStore(collectionName);
            const result = collection.get(id);

            result.onsuccess = function (e) {
                resolve(result.result);
            }
        }
    });

    const result = await request;

    return result;
}
*/

export function add(newItem, key) {
    const transaction = synqraDb.result.transaction(collectionName, "readwrite");
    const collection = transaction.objectStore(collectionName)
    collection.add(newItem, key);
}

export function addBatch(newItems, keyField) {
    const transaction = synqraDb.result.transaction(collectionName, "readwrite");
    const collection = transaction.objectStore(collectionName)
    newItems.forEach(newItem => {
        collection.add(newItem, newItem[keyField]);
    });
}

export async function getAll(fromKey, pageSize) {
    return await new Promise((resolve, reject) => {
        const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
        synqraDb.onsuccess = function () {
            const transaction = synqraDb.result.transaction(collectionName, "readonly");
            const collection = transaction.objectStore(collectionName);
            const result = collection.getAll(IDBKeyRange.lowerBound(fromKey, true), pageSize);

            result.onsuccess = function (e) {
                // console.log('indexedDbInterop getAll', result.result)
                resolve(result.result);
            }

            result.onerror = function (e) {
                console.error(result.error)
                reject(result.error);
            }
        }
    });
}
