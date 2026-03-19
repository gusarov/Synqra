const DATABASE_NAME = "Synqra";
const CURRENT_VERSION = 1;

const collectionName = "events";

let synqraDb: IDBDatabase;
let synqraDbPromise: Promise<void> | undefined;

export function initialize(): Promise<void> {
    if (!synqraDbPromise) {
        synqraDbPromise = new Promise<void>((resolve, reject) => {
            try {
                let synqraDbRequest = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
                // console.log('Synqra Indexed DB Initialize...', synqraDb);
                synqraDbRequest.onsuccess = function () {
                    // console.log('Synqra Indexed DB Initialize succeeded.', synqraDb.result);
                    synqraDb = synqraDbRequest.result;
                    resolve();
                };
                synqraDbRequest.onerror = function () {
                    // console.error('Synqra Indexed DB Initialize error.', synqraDb.error);
                    reject(synqraDbRequest.error);
                };
                synqraDbRequest.onupgradeneeded = function (event: IDBVersionChangeEvent) {
                    const db = synqraDbRequest.result;
                    const oldVersion = event.oldVersion;
                    console.warn("Synqra Indexed DB Upgrade needed from v" + oldVersion);

                    if (oldVersion < 1) {
                        // const objStore = db.createObjectStore("events", { keyPath: "seq_id", autoIncrement: true });
                        // objStore.add({ _sys: 'ObjectStoreCreated' });
                        db.createObjectStore(collectionName);
                    }
                };
            } catch (error) {
                reject(error);
            }
        });
    }
    return synqraDbPromise;
}

/*
export function set(collectionName: string, value: unknown): void {
    const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);

    synqraDb.onsuccess = function () {
        const transaction = synqraDb.result.transaction(collectionName, "readwrite");
        const collection = transaction.objectStore(collectionName);
        collection.put(value);
    };
}

export async function get(collectionName: string, id: IDBValidKey): Promise<unknown> {
    const request = new Promise<unknown>((resolve) => {
        const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
        synqraDb.onsuccess = function () {
            const transaction = synqraDb.result.transaction(collectionName, "readonly");
            const collection = transaction.objectStore(collectionName);
            const result = collection.get(id);

            result.onsuccess = function () {
                resolve(result.result);
            };
        };
    });

    const result = await request;

    return result;
}
*/

export async function add(newItem: unknown, key: IDBValidKey): Promise<void> {
    console.log("Synqra Indexed DB add|");
    await initialize();
    console.log("Synqra Indexed DB add...");
    console.log("L1...");
    // const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
    const transaction = synqraDb.transaction(collectionName, "readwrite");
    console.log("L2...");
    const collection = transaction.objectStore(collectionName);
    console.log("L3...");
    collection.add(newItem, key);
    console.log("L4...");
    console.log("Synqra Indexed DB add succeeded.");
}

export async function addBatch(newItems: Record<string, unknown>[], keyField: string): Promise<void> {
    await initialize();
    console.log("Synqra Indexed DB addBatch...");
    const transaction = synqraDb.transaction(collectionName, "readwrite");
    console.log("L1...");
    const collection = transaction.objectStore(collectionName);
    console.log("L2...");
    newItems.forEach((newItem) => {
        console.log("L3...");
        collection.add(newItem, newItem[keyField] as IDBValidKey);
    });
    console.log("L4...");
    console.log("Synqra Indexed DB addBatch succeeded.");
}

export async function getAll(fromKey: IDBValidKey, pageSize: number): Promise<unknown[]> {
    return await new Promise<unknown[]>((resolve, reject) => {
        const synqraDb = indexedDB.open(DATABASE_NAME, CURRENT_VERSION);
        synqraDb.onerror = function () {
            reject(synqraDb.error);
        };
        synqraDb.onsuccess = function () {
            const transaction = synqraDb.result.transaction(collectionName, "readonly");
            const collection = transaction.objectStore(collectionName);
            const result = collection.getAll(IDBKeyRange.lowerBound(fromKey, true), pageSize);

            result.onsuccess = function () {
                console.log("indexedDbInterop getAll");
                resolve(result.result);
                console.log("indexedDbInterop getAll ok");
            };

            result.onerror = function () {
                console.error(result.error);
                reject(result.error);
            };
        };
    });
}
