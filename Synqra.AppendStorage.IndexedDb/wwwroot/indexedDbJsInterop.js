const DATABASE_NAME = "Synqra";
const CURRENT_VERSION = 1;

const collectionName = "events";

let synqraDb;
let synqraDbResult;
let synqraDbPromise;

export function initialize() {
    if (!synqraDbPromise) {
        synqraDbPromise = new Promise((resolve, reject) => {
            try {
                initialize_core(resolve);
            } catch (error) {
                reject(error);
            }
        });
    }
    return synqraDbPromise;
}

export function initialize_core(resolve) {
    synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
    console.log('Synqra Indexed DB Initialize...', synqraDb);
    synqraDb.onsuccess = function () {
        console.log('Synqra Indexed DB Initialize succeeded.', synqraDb.result);
        synqraDbResult = synqraDb.result;
        resolve();
    }
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

export async function add(newItem, key) {
    console.log('Synqra Indexed DB add|');
    await initialize();
    console.log('Synqra Indexed DB add...');
    console.log('L1...');
    // const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
    const transaction = synqraDbResult.transaction(collectionName, "readwrite");
    console.log('L2...');
    const collection = transaction.objectStore(collectionName)
    console.log('L3...');
    collection.add(newItem, key);
    console.log('L4...');
    console.log('Synqra Indexed DB add succeeded.');
}

export async function addBatch(newItems, keyField) {
    await initialize();
    console.log('Synqra Indexed DB addBatch...');
    const transaction = synqraDbResult.transaction(collectionName, "readwrite");
    console.log('L1...');
    const collection = transaction.objectStore(collectionName)
    console.log('L2...');
    newItems.forEach(newItem => {
        console.log('L3...');
        collection.add(newItem, newItem[keyField]);
    });
    console.log('L4...');
    console.log('Synqra Indexed DB addBatch succeeded.');
}

export async function getAll(fromKey, pageSize) {
    return await new Promise((resolve, reject) => {
        const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
        synqraDb.onsuccess = function () {
            const transaction = synqraDb.result.transaction(collectionName, "readonly");
            const collection = transaction.objectStore(collectionName);
            const result = collection.getAll(IDBKeyRange.lowerBound(fromKey, true), pageSize);

            result.onsuccess = function (e) {
                console.log('indexedDbInterop getAll')
                resolve(result.result);
                console.log('indexedDbInterop getAll ok')
            }

            result.onerror = function (e) {
                console.error(result.error)
                reject(result.error);
            }
        }
    });
}
