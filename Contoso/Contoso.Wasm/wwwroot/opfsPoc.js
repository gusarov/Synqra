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
const opfsRootDirectoryName = "synqra-opfs-poc";
const asyncFileName = "async-suite.txt";
export function runOpfsPoc() {
    return __awaiter(this, void 0, void 0, function* () {
        const suites = [
            yield runAsyncSuite(),
            yield runSyncSuiteViaWorker()
        ];
        const isSuccess = suites.every(suite => suite.passed);
        const summary = isSuccess
            ? "All OPFS PoC suites passed."
            : suites.some(suite => !suite.supported)
                ? "OPFS PoC completed, but some browser features are unsupported."
                : "One or more OPFS PoC suites failed.";
        return {
            isSuccess,
            summary,
            completedAtUtc: new Date().toISOString(),
            suites
        };
    });
}
function runAsyncSuite() {
    return __awaiter(this, void 0, void 0, function* () {
        const steps = [];
        try {
            const storageManager = navigator.storage;
            const rootDirectory = yield runCheckedStep(steps, "detect-support", "Detect OPFS support", () => __awaiter(this, void 0, void 0, function* () {
                if (typeof storageManager.getDirectory !== "function") {
                    throw new UnsupportedFeatureError("navigator.storage.getDirectory is not available in this browser.");
                }
                return {
                    value: yield storageManager.getDirectory(),
                    message: "navigator.storage.getDirectory is available."
                };
            }));
            const suiteDirectory = yield runCheckedStep(steps, "open-directory", "Create or open PoC directory", () => __awaiter(this, void 0, void 0, function* () {
                return ({
                    value: yield rootDirectory.getDirectoryHandle(opfsRootDirectoryName, { create: true }),
                    message: `Directory '${opfsRootDirectoryName}' is ready.`
                });
            }));
            yield deleteIfExists(suiteDirectory, asyncFileName);
            const fileHandle = yield runCheckedStep(steps, "create-file", "Create async test file", () => __awaiter(this, void 0, void 0, function* () {
                return ({
                    value: yield suiteDirectory.getFileHandle(asyncFileName, { create: true }),
                    message: `File '${asyncFileName}' is ready.`
                });
            }));
            yield runCheckedStep(steps, "write-hello", "Write initial content", () => __awaiter(this, void 0, void 0, function* () {
                const writable = yield fileHandle.createWritable();
                try {
                    yield writable.write("hello");
                }
                finally {
                    yield writable.close();
                }
                return { value: undefined, message: "Wrote 'hello' to the file." };
            }));
            yield runCheckedStep(steps, "reopen-read-hello", "Reopen and read initial content", () => __awaiter(this, void 0, void 0, function* () {
                const text = yield readFileText(fileHandle);
                ensureText(text, "hello", "Expected the reopened file to contain 'hello'.");
                return { value: undefined, message: `Read back '${text}'.` };
            }));
            yield runCheckedStep(steps, "append-world", "Append more content", () => __awaiter(this, void 0, void 0, function* () {
                const existingFile = yield fileHandle.getFile();
                const writable = yield fileHandle.createWritable({ keepExistingData: true });
                try {
                    yield writable.write({
                        type: "write",
                        position: existingFile.size,
                        data: " world"
                    });
                }
                finally {
                    yield writable.close();
                }
                return { value: undefined, message: "Appended ' world' to the file." };
            }));
            yield runCheckedStep(steps, "reopen-read-full", "Reopen and verify appended content", () => __awaiter(this, void 0, void 0, function* () {
                const text = yield readFileText(fileHandle);
                ensureText(text, "hello world", "Expected the reopened file to contain 'hello world'.");
                return { value: undefined, message: `Read back '${text}'.` };
            }));
            yield runCheckedStep(steps, "enumerate-present", "Enumerate directory with file present", () => __awaiter(this, void 0, void 0, function* () {
                const names = yield listEntryNames(suiteDirectory);
                if (!names.includes(asyncFileName)) {
                    throw new Error(`Directory listing does not contain '${asyncFileName}'.`);
                }
                return { value: undefined, message: `Found '${asyncFileName}' in the directory.` };
            }));
            yield runCheckedStep(steps, "delete-file", "Delete async test file", () => __awaiter(this, void 0, void 0, function* () {
                yield suiteDirectory.removeEntry(asyncFileName);
                return { value: undefined, message: `Deleted '${asyncFileName}'.` };
            }));
            yield runCheckedStep(steps, "enumerate-absent", "Enumerate directory after delete", () => __awaiter(this, void 0, void 0, function* () {
                const names = yield listEntryNames(suiteDirectory);
                if (names.includes(asyncFileName)) {
                    throw new Error(`Directory listing still contains '${asyncFileName}'.`);
                }
                return { value: undefined, message: `Confirmed '${asyncFileName}' is gone.` };
            }));
            return createSuiteResult("async", "Async OPFS API", true, true, "All async OPFS checks passed.", steps);
        }
        catch (error) {
            return createSuiteResult("async", "Async OPFS API", !(error instanceof UnsupportedFeatureError), false, getErrorMessage(error), steps);
        }
    });
}
function runSyncSuiteViaWorker() {
    return __awaiter(this, void 0, void 0, function* () {
        const bootstrapSteps = [];
        let worker = null;
        try {
            worker = yield runCheckedStep(bootstrapSteps, "start-worker", "Start dedicated worker", () => __awaiter(this, void 0, void 0, function* () {
                return ({
                    value: new Worker(new URL("./opfsSyncWorker.js", import.meta.url), { type: "module" }),
                    message: "Dedicated worker is running."
                });
            }));
            const suite = yield runWorkerSyncSuite(worker);
            suite.steps = [...bootstrapSteps, ...suite.steps];
            return suite;
        }
        catch (error) {
            const supported = !(error instanceof UnsupportedFeatureError);
            return createSuiteResult("sync-worker", "Sync Access Handle Worker", supported, false, getErrorMessage(error), bootstrapSteps);
        }
        finally {
            worker === null || worker === void 0 ? void 0 : worker.terminate();
        }
    });
}
function runWorkerSyncSuite(worker) {
    return __awaiter(this, void 0, void 0, function* () {
        return yield new Promise((resolve, reject) => {
            const requestId = Date.now();
            const timeoutId = window.setTimeout(() => {
                cleanup();
                reject(new Error("Timed out while waiting for the sync access handle worker."));
            }, 30000);
            const cleanup = () => {
                window.clearTimeout(timeoutId);
                worker.removeEventListener("message", onMessage);
                worker.removeEventListener("error", onError);
                worker.removeEventListener("messageerror", onMessageError);
            };
            const onMessage = (event) => {
                if (!event.data || event.data.requestId !== requestId) {
                    return;
                }
                cleanup();
                if (event.data.error) {
                    reject(new Error(event.data.error));
                    return;
                }
                if (!event.data.suite) {
                    reject(new Error("Worker completed without returning a suite result."));
                    return;
                }
                resolve(event.data.suite);
            };
            const onError = (event) => {
                cleanup();
                reject(new Error(event.message || "The sync access handle worker raised an error."));
            };
            const onMessageError = () => {
                cleanup();
                reject(new Error("The sync access handle worker could not deserialize a message."));
            };
            worker.addEventListener("message", onMessage);
            worker.addEventListener("error", onError);
            worker.addEventListener("messageerror", onMessageError);
            const request = {
                requestId,
                kind: "runSyncSuite"
            };
            worker.postMessage(request);
        });
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
function readFileText(fileHandle) {
    return __awaiter(this, void 0, void 0, function* () {
        const file = yield fileHandle.getFile();
        return yield file.text();
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
//# sourceMappingURL=opfsPoc.js.map