"use strict";
var __awaiter = (this && this.__awaiter) || function (thisArg, _arguments, P, generator) {
    function adopt(value) { return value instanceof P ? value : new P(function (resolve) { resolve(value); }); }
    return new (P || (P = Promise))(function (resolve, reject) {
        function fulfilled(value) { try { step(generator.next(value)); } catch (e) { reject(e); } }
        function rejected(value) { try { step(generator["throw"](value)); } catch (e) { reject(e); } }
        function step(result) { result.done ? resolve(result.value) : adopt(result.value).then(fulfilled, rejected); }
        step((generator = generator.apply(thisArg, _arguments || [])).next());
    });
};
var __asyncValues = (this && this.__asyncValues) || function (o) {
    if (!Symbol.asyncIterator) throw new TypeError("Symbol.asyncIterator is not defined.");
    var m = o[Symbol.asyncIterator], i;
    return m ? m.call(o) : (o = typeof __values === "function" ? __values(o) : o[Symbol.iterator](), i = {}, verb("next"), verb("throw"), verb("return"), i[Symbol.asyncIterator] = function () { return this; }, i);
    function verb(n) { i[n] = o[n] && function (v) { return new Promise(function (resolve, reject) { v = o[n](v), settle(resolve, reject, v.done, v.value); }); }; }
    function settle(resolve, reject, d, v) { Promise.resolve(v).then(function(v) { resolve({ value: v, done: d }); }, reject); }
};
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
workerScope.addEventListener("message", (event) => __awaiter(void 0, void 0, void 0, function* () {
    if (!event.data || event.data.kind !== "runSyncSuite") {
        return;
    }
    const response = { requestId: event.data.requestId };
    try {
        response.suite = yield runSyncSuite();
    }
    catch (error) {
        response.error = getErrorMessage(error);
    }
    workerScope.postMessage(response);
}));
function runSyncSuite() {
    return __awaiter(this, void 0, void 0, function* () {
        const steps = [];
        try {
            const storageManager = workerScope.navigator.storage;
            const rootDirectory = yield runCheckedStep(steps, "detect-support", "Detect sync-handle OPFS support", () => __awaiter(this, void 0, void 0, function* () {
                if (typeof storageManager.getDirectory !== "function") {
                    throw new UnsupportedFeatureError("navigator.storage.getDirectory is not available in this worker.");
                }
                return {
                    value: yield storageManager.getDirectory(),
                    message: "Worker OPFS root is available."
                };
            }));
            const suiteDirectory = yield runCheckedStep(steps, "open-directory", "Create or open PoC directory", () => __awaiter(this, void 0, void 0, function* () {
                return ({
                    value: yield rootDirectory.getDirectoryHandle(opfsRootDirectoryName, { create: true }),
                    message: `Directory '${opfsRootDirectoryName}' is ready.`
                });
            }));
            yield deleteIfExists(suiteDirectory, syncFileName);
            const fileHandle = yield runCheckedStep(steps, "create-file", "Create sync test file", () => __awaiter(this, void 0, void 0, function* () {
                return ({
                    value: yield suiteDirectory.getFileHandle(syncFileName, { create: true }),
                    message: `File '${syncFileName}' is ready.`
                });
            }));
            yield runCheckedStep(steps, "check-sync-handle", "Check sync access handle support", () => __awaiter(this, void 0, void 0, function* () {
                if (typeof fileHandle.createSyncAccessHandle !== "function") {
                    throw new UnsupportedFeatureError("FileSystemSyncAccessHandle is not available in this browser.");
                }
                return { value: undefined, message: "createSyncAccessHandle is available." };
            }));
            yield runCheckedStep(steps, "write-hello", "Write initial content with sync handle", () => __awaiter(this, void 0, void 0, function* () {
                const handle = yield fileHandle.createSyncAccessHandle();
                try {
                    handle.write(encoder.encode("hello"), { at: 0 });
                    yield handle.flush();
                }
                finally {
                    yield handle.close();
                }
                return { value: undefined, message: "Wrote and flushed 'hello'." };
            }));
            yield runCheckedStep(steps, "reopen-read-hello", "Reopen sync handle and read initial content", () => __awaiter(this, void 0, void 0, function* () {
                const text = yield readAllText(fileHandle);
                ensureText(text, "hello", "Expected the reopened file to contain 'hello'.");
                return { value: undefined, message: `Read back '${text}'.` };
            }));
            yield runCheckedStep(steps, "append-world", "Append more content with sync handle", () => __awaiter(this, void 0, void 0, function* () {
                const handle = yield fileHandle.createSyncAccessHandle();
                try {
                    const appendPosition = handle.getSize();
                    handle.write(encoder.encode(" world"), { at: appendPosition });
                    yield handle.flush();
                }
                finally {
                    yield handle.close();
                }
                return { value: undefined, message: "Appended and flushed ' world'." };
            }));
            yield runCheckedStep(steps, "reopen-read-full", "Reopen sync handle and verify appended content", () => __awaiter(this, void 0, void 0, function* () {
                const text = yield readAllText(fileHandle);
                ensureText(text, "hello world", "Expected the reopened file to contain 'hello world'.");
                return { value: undefined, message: `Read back '${text}'.` };
            }));
            yield runCheckedStep(steps, "delete-file", "Delete sync test file", () => __awaiter(this, void 0, void 0, function* () {
                yield suiteDirectory.removeEntry(syncFileName);
                return { value: undefined, message: `Deleted '${syncFileName}'.` };
            }));
            yield runCheckedStep(steps, "verify-delete", "Verify sync test file deletion", () => __awaiter(this, void 0, void 0, function* () {
                const names = yield listEntryNames(suiteDirectory);
                if (names.includes(syncFileName)) {
                    throw new Error(`Directory listing still contains '${syncFileName}'.`);
                }
                return { value: undefined, message: `Confirmed '${syncFileName}' is gone.` };
            }));
            return createSuiteResult("sync-worker", "Sync Access Handle Worker", true, true, "All sync access handle checks passed.", steps);
        }
        catch (error) {
            return createSuiteResult("sync-worker", "Sync Access Handle Worker", !(error instanceof UnsupportedFeatureError), false, getErrorMessage(error), steps);
        }
    });
}
function runCheckedStep(steps, key, name, action) {
    return __awaiter(this, void 0, void 0, function* () {
        try {
            const result = yield action();
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
    });
}
function readAllText(fileHandle) {
    return __awaiter(this, void 0, void 0, function* () {
        const handle = yield fileHandle.createSyncAccessHandle();
        try {
            const size = handle.getSize();
            const buffer = new Uint8Array(size);
            const bytesRead = handle.read(buffer, { at: 0 });
            return decoder.decode(buffer.subarray(0, bytesRead));
        }
        finally {
            yield handle.close();
        }
    });
}
function listEntryNames(directoryHandle) {
    return __awaiter(this, void 0, void 0, function* () {
        var _a, e_1, _b, _c, _d, e_2, _e, _f;
        const names = [];
        const iterableHandle = directoryHandle;
        if (typeof iterableHandle.keys === "function") {
            try {
                for (var _g = true, _h = __asyncValues(iterableHandle.keys()), _j; _j = yield _h.next(), _a = _j.done, !_a; _g = true) {
                    _c = _j.value;
                    _g = false;
                    const name = _c;
                    names.push(name);
                }
            }
            catch (e_1_1) { e_1 = { error: e_1_1 }; }
            finally {
                try {
                    if (!_g && !_a && (_b = _h.return)) yield _b.call(_h);
                }
                finally { if (e_1) throw e_1.error; }
            }
            return names;
        }
        if (typeof iterableHandle.entries === "function") {
            try {
                for (var _k = true, _l = __asyncValues(iterableHandle.entries()), _m; _m = yield _l.next(), _d = _m.done, !_d; _k = true) {
                    _f = _m.value;
                    _k = false;
                    const [name] = _f;
                    names.push(name);
                }
            }
            catch (e_2_1) { e_2 = { error: e_2_1 }; }
            finally {
                try {
                    if (!_k && !_d && (_e = _l.return)) yield _e.call(_l);
                }
                finally { if (e_2) throw e_2.error; }
            }
            return names;
        }
        throw new Error("The browser does not expose directory iteration APIs for OPFS.");
    });
}
function deleteIfExists(directoryHandle, entryName) {
    return __awaiter(this, void 0, void 0, function* () {
        try {
            yield directoryHandle.removeEntry(entryName);
        }
        catch (error) {
            const domError = error;
            if ((domError === null || domError === void 0 ? void 0 : domError.name) !== "NotFoundError") {
                throw error;
            }
        }
    });
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