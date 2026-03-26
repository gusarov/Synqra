"use strict";
class UnsupportedFeatureError extends Error {
    constructor(message) {
        super(message);
        this.name = "UnsupportedFeatureError";
    }
}
const workerScope = self;
const opfsRootDirectoryName = "synqra-opfs-poc";
const syncFileName = "sync-worker-suite.txt";
const encoder = new TextEncoder();
const decoder = new TextDecoder();
workerScope.addEventListener("message", async (event) => {
    if (!event.data || event.data.kind !== "runSyncSuite") {
        return;
    }
    const response = { requestId: event.data.requestId };
    try {
        response.suite = await runSyncSuite();
    }
    catch (error) {
        response.error = getErrorMessage(error);
    }
    workerScope.postMessage(response);
});
async function runSyncSuite() {
    const steps = [];
    try {
        const storageManager = workerScope.navigator.storage;
        const rootDirectory = await runCheckedStep(steps, "detect-support", "Detect sync-handle OPFS support", async () => {
            if (typeof storageManager.getDirectory !== "function") {
                throw new UnsupportedFeatureError("navigator.storage.getDirectory is not available in this worker.");
            }
            return {
                value: await storageManager.getDirectory(),
                message: "Worker OPFS root is available."
            };
        });
        const suiteDirectory = await runCheckedStep(steps, "open-directory", "Create or open PoC directory", async () => ({
            value: await rootDirectory.getDirectoryHandle(opfsRootDirectoryName, { create: true }),
            message: `Directory '${opfsRootDirectoryName}' is ready.`
        }));
        await deleteIfExists(suiteDirectory, syncFileName);
        const fileHandle = await runCheckedStep(steps, "create-file", "Create sync test file", async () => ({
            value: await suiteDirectory.getFileHandle(syncFileName, { create: true }),
            message: `File '${syncFileName}' is ready.`
        }));
        await runCheckedStep(steps, "check-sync-handle", "Check sync access handle support", async () => {
            if (typeof fileHandle.createSyncAccessHandle !== "function") {
                throw new UnsupportedFeatureError("FileSystemSyncAccessHandle is not available in this browser.");
            }
            return { value: undefined, message: "createSyncAccessHandle is available." };
        });
        await runCheckedStep(steps, "write-hello", "Write initial content with sync handle", async () => {
            const handle = await fileHandle.createSyncAccessHandle();
            try {
                handle.write(encoder.encode("hello"), { at: 0 });
                await handle.flush();
            }
            finally {
                await handle.close();
            }
            return { value: undefined, message: "Wrote and flushed 'hello'." };
        });
        await runCheckedStep(steps, "reopen-read-hello", "Reopen sync handle and read initial content", async () => {
            const text = await readAllText(fileHandle);
            ensureText(text, "hello", "Expected the reopened file to contain 'hello'.");
            return { value: undefined, message: `Read back '${text}'.` };
        });
        await runCheckedStep(steps, "append-world", "Append more content with sync handle", async () => {
            const handle = await fileHandle.createSyncAccessHandle();
            try {
                const appendPosition = handle.getSize();
                handle.write(encoder.encode(" world"), { at: appendPosition });
                await handle.flush();
            }
            finally {
                await handle.close();
            }
            return { value: undefined, message: "Appended and flushed ' world'." };
        });
        await runCheckedStep(steps, "reopen-read-full", "Reopen sync handle and verify appended content", async () => {
            const text = await readAllText(fileHandle);
            ensureText(text, "hello world", "Expected the reopened file to contain 'hello world'.");
            return { value: undefined, message: `Read back '${text}'.` };
        });
        await runCheckedStep(steps, "delete-file", "Delete sync test file", async () => {
            await suiteDirectory.removeEntry(syncFileName);
            return { value: undefined, message: `Deleted '${syncFileName}'.` };
        });
        await runCheckedStep(steps, "verify-delete", "Verify sync test file deletion", async () => {
            const names = await listEntryNames(suiteDirectory);
            if (names.includes(syncFileName)) {
                throw new Error(`Directory listing still contains '${syncFileName}'.`);
            }
            return { value: undefined, message: `Confirmed '${syncFileName}' is gone.` };
        });
        return createSuiteResult("sync-worker", "Sync Access Handle Worker", true, true, "All sync access handle checks passed.", steps);
    }
    catch (error) {
        return createSuiteResult("sync-worker", "Sync Access Handle Worker", !(error instanceof UnsupportedFeatureError), false, getErrorMessage(error), steps);
    }
}
async function runCheckedStep(steps, key, name, action) {
    try {
        const result = await action();
        steps.push({
            key,
            name,
            passed: true,
            message: result.message
        });
        return result.value;
    }
    catch (error) {
        steps.push({
            key,
            name,
            passed: false,
            message: getErrorMessage(error)
        });
        throw error;
    }
}
async function readAllText(fileHandle) {
    const handle = await fileHandle.createSyncAccessHandle();
    try {
        const size = handle.getSize();
        const buffer = new Uint8Array(size);
        const bytesRead = handle.read(buffer, { at: 0 });
        return decoder.decode(buffer.subarray(0, bytesRead));
    }
    finally {
        await handle.close();
    }
}
async function listEntryNames(directoryHandle) {
    const names = [];
    const iterableHandle = directoryHandle;
    if (typeof iterableHandle.keys === "function") {
        for await (const name of iterableHandle.keys()) {
            names.push(name);
        }
        return names;
    }
    if (typeof iterableHandle.entries === "function") {
        for await (const [name] of iterableHandle.entries()) {
            names.push(name);
        }
        return names;
    }
    throw new Error("The browser does not expose directory iteration APIs for OPFS.");
}
async function deleteIfExists(directoryHandle, entryName) {
    try {
        await directoryHandle.removeEntry(entryName);
    }
    catch (error) {
        const domError = error;
        if (domError?.name !== "NotFoundError") {
            throw error;
        }
    }
}
function ensureText(actual, expected, message) {
    if (actual !== expected) {
        throw new Error(`${message} Actual: '${actual}'.`);
    }
}
function createSuiteResult(key, name, supported, passed, message, steps) {
    return {
        key,
        name,
        supported,
        passed,
        message,
        steps
    };
}
function getErrorMessage(error) {
    if (error instanceof Error) {
        return error.message;
    }
    return typeof error === "string" ? error : JSON.stringify(error);
}
//# sourceMappingURL=opfsSyncWorker.js.map